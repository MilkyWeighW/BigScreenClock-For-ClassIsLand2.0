// =============================================================================
// FullScreenClockViewModel（partial）— 核心：字段与构造 / 基础显示属性 / 设置变更响应 / 主题与外观 / 属性变更通知
// 职责：本类是「大屏时钟」视图模型的骨架 —— 依赖与全部字段声明、构造与订阅、
// CI 虚拟时间、时间/日期/课名等基础绑定属性、外观画刷与主题切换、设置变更响应，
// 以及 INotifyPropertyChanged 通知基座。其余 partial 文件都是围绕这些状态的行为。
// 本类按职责拆成 6 个 partial 文件（同一 namespace，均声明 partial class + INotifyPropertyChanged）：
//   FullScreenClockViewModel.cs                   核心：字段、构造、基础属性、设置变更、主题与外观
//   FullScreenClockViewModel.Lifecycle.cs         窗口生命周期、定时器与每秒刷新循环
//   FullScreenClockViewModel.Course.cs            课表加载、课程定位与打铃时间
//   FullScreenClockViewModel.Noise.cs             噪音统计、音量条、计数片段与「正在记录」
//   FullScreenClockViewModel.Reminders.cs         提醒面板、天气/预警数据、地震提醒
//   FullScreenClockViewModel.ReminderLayout.cs    提醒首行合并布局与颜文字副标题
// 成员只是原样搬移（未改逻辑/可见性/命名），每个字段/成员只在一处声明。
// =============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Icons;
using ClassIsland.Core.Models.Components;
using ClassIsland.Core.Models.Theming;
using ClassIsland.Shared.Enums;
using LargeScreenClockRevived.Helpers;
using LargeScreenClockRevived.Models;
using LargeScreenClockRevived.NoiseDetection;
using LargeScreenClockRevived.Services;
using LargeScreenClockRevived.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LargeScreenClockRevived.ViewModels;

public partial class FullScreenClockViewModel : INotifyPropertyChanged
{
    private readonly PluginSettings _settings;
    private readonly DecibelMeterService _decibelService;
    private readonly CameraActivityService _cameraService;
    private readonly IServiceProvider _serviceProvider;
    private readonly EarthquakeReader _earthquakeReader;
    private readonly NoiseCounter _counter = new();
    private System.Timers.Timer? _updateTimer;
    private FullScreenClockWindow? _window;

    /// <summary>
    /// CI 里设置的时间偏移（秒）。正 = 打铃提前，负 = 打铃延后。
    /// </summary>
    private int _timeOffsetSeconds;

    // 计数显示颜色：一般 = 黄，吵闹 = 红。两套主题各一版（浅底上原色太亮看不清）
    private static readonly IBrush CountNormalBrush = new SolidColorBrush(Color.Parse(SemanticColors.CountNormalHex(false)));
    private static readonly IBrush CountNoisyBrush = new SolidColorBrush(Color.Parse(SemanticColors.CountNoisyHex(false)));
    private static readonly IBrush CountNormalLightBrush = new SolidColorBrush(Color.Parse(SemanticColors.CountNormalHex(true)));
    private static readonly IBrush CountNoisyLightBrush = new SolidColorBrush(Color.Parse(SemanticColors.CountNoisyHex(true)));

    /// <summary>当前主题下「一般」计数色。</summary>
    private IBrush CountNormal => Chrome.IsLight ? CountNormalLightBrush : CountNormalBrush;

    /// <summary>当前主题下「吵闹」计数色。</summary>
    private IBrush CountNoisy => Chrome.IsLight ? CountNoisyLightBrush : CountNoisyBrush;

    /// <summary>给设置页预览的「一般」示意文字用，跟真实计数同一份语义色。</summary>
    public IBrush CountNormalForeground => CountNormal;

    /// <summary>给设置页预览的「吵闹」示意文字用。</summary>
    public IBrush CountNoisyForeground => CountNoisy;

    // 计数区「暂未记录到吵闹」前的对勾图标字形（矢量）
    private static readonly string CheckIcon = IconGlyph.Of(LucideIconKind.CircleCheck);

    // 音量条档位：档位名来自 NoiseLevelDisplay（与检测器分级同源），
    // 五档高亮、右侧状态、填充色全部以 AnimatedProgress 反推出的 CurrentSlot 为唯一源，避免不同步。
    private static readonly string[] SlotNames = NoiseLevelDisplay.SlotNames;

    /// <summary>五档轨道填充色：静态缓存，避免每次刷新都新建 SolidColorBrush（会让控件反复失效重绘、观感发顿）。</summary>
    private static readonly IBrush[] SlotBrushes = BuildSlotBrushes(LevelSlotCalculator.SlotColors);

    /// <summary>五档文字色（未达到该档）：深色 / 明亮两套（明亮用压暗版，白底上才看得清）。</summary>
    private static readonly IBrush[] SlotTextBrushesDark = BuildSlotBrushes(LevelSlotCalculator.SlotTextColors(false));
    private static readonly IBrush[] SlotTextBrushesLight = BuildSlotBrushes(LevelSlotCalculator.SlotTextColors(true));

    /// <summary>五档文字色（已到达该档）：只有「一般」与未达到时不同（亮黄 vs 压暗琥珀）。</summary>
    private static readonly IBrush[] SlotReachedBrushesDark = BuildReachedBrushes(false);
    private static readonly IBrush[] SlotReachedBrushesLight = BuildReachedBrushes(true);

    // 进度条与降水提醒的画刷缓存：这两个 getter 绑在界面上，主题/颜色/强调色变化时通知一次即可，
    // 每次求值都 new SolidColorBrush 会让控件反复失效重绘（1.4 顺手修掉的每秒热点）。
    private IBrush? _progressFillBrush;
    private IBrush? _rainColorBrush;

    /// <summary>CI 主题服务（订阅强调色变化用；宿主没提供时为 null，退化为打开大屏时读一次）。</summary>
    private IThemeService? _ciThemeService;
    private bool _ciThemeHooked;       // 已订阅 IThemeService.ThemeUpdated
    private bool _ciResourcesHooked;   // 已订阅 Application.ResourcesChanged

    /// <summary>非当前档位文字的淡化程度：浅底上要比深底淡得少，否则整排字糊在背景里。</summary>
    private const double SlotFadeDark = 0.35;
    private const double SlotFadeLight = 0.5;

    /// <summary>逐档取「已达到」色（<see cref="LevelSlotCalculator.ReachedColorOf"/>）。</summary>
    private static IBrush[] BuildReachedBrushes(bool light)
    {
        var colors = new uint[SlotNames.Length];
        for (var i = 0; i < colors.Length; i++)
            colors[i] = LevelSlotCalculator.ReachedColorOf(i, light);
        return BuildSlotBrushes(colors);
    }

    private static IBrush[] BuildSlotBrushes(uint[] colors)
    {
        var brushes = new IBrush[colors.Length];
        for (var i = 0; i < brushes.Length; i++)
        {
            var c = colors[i];
            brushes[i] = new SolidColorBrush(Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c));
        }
        return brushes;
    }

    private readonly ObservableCollection<NoiseLevelSlotItem> _noiseLevelSlots = new();

    private string _currentTime = "00:00:00";
    private string _currentDate = "";
    private string _currentCourseName = "";
    private string _currentClassName = "";
    private bool _isWindowVisible;
    private bool _isInBreak;
    private DateTime _classStartTime;
    private string _courseInfoText = "";
    private List<TimeSlot> _todaySlots = new();
    private DateTime _scheduleDate;   // _todaySlots 是哪一天的课表（跨天要重读）
    private bool _loadingSchedule;    // 课表异步重载进行中：防止每秒的换天检查排队重入
    private System.Timers.Timer? _antiScreensaverTimer;
    private string _noisyDisplaySignature = "";
    private bool _showCountRules;

    // ===== 提醒面板数据（跟随 CI 组件配置 + CI 天气缓存 + 摄像头占用） =====
    private readonly ReminderData _reminderData = new();
    /// <summary>天气预警（CI 缓存读来的，60 秒刷新一次）。</summary>
    private readonly List<AlertInfo> _weatherAlerts = new();
    private int _reminderTick;
    private DateTime _lastWeatherRead = DateTime.MinValue;

    public FullScreenClockViewModel(
        PluginSettings settings,
        DecibelMeterService decibelService,
        CameraActivityService cameraService,
        IServiceProvider serviceProvider)
    {
        _settings = settings;
        _decibelService = decibelService;
        _cameraService = cameraService;
        _serviceProvider = serviceProvider;
        _earthquakeReader = new EarthquakeReader(serviceProvider);

        // 统一时间源先接上宿主：大屏读数、课程进度、打铃此后共用同一条时间轴。
        // 解析是惰性的（首次取时间时才查 DI），宿主晚于插件就绪也不会错过。
        CiTimeService.Attach(serviceProvider);

        // 主题色跟随：订阅 CI 的主题服务与资源变化（换主题/换强调色时刷新大屏与预览）。
        // 构造这一刻宿主可能还没完全就绪，所以订阅做成幂等的，打开大屏时再兜底一次。
        EnsureCiThemeSubscribed();

        _decibelService.PropertyChanged += OnDecibelPropertyChanged;
        _decibelService.NoiseEventRaised += OnNoiseEventRaised;
        _settings.PropertyChanged += OnSettingsPropertyChanged;

        for (var i = 0; i < SlotNames.Length; i++)
            _noiseLevelSlots.Add(new NoiseLevelSlotItem(SlotNames[i], SlotTextBrushesDark[i], SlotReachedBrushesDark[i]));
        ApplySlotTheme();
        UpdateNoiseLevelSlots();
    }

    /// <summary>
    /// CI 的「虚拟本地时间」（含时间偏移），课程定位/保护计时/打铃都用它。
    ///
    /// 1.4.0.0 起大屏读数也走同一条时间轴（<see cref="CiTimeService"/>）：
    /// 以前读数用本机时间、课程用虚拟时间，时间偏移不为 0 时读数与进度/打铃对不齐。
    /// 偏移为 0 时 CiTimeService.Now == DateTime.Now，所以默认设置下行为与 1.3.1.1 一致。
    /// </summary>
    private DateTime NowVirtual => CiTimeService.Now;

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(PluginSettings.ClockFontSize)
            or nameof(PluginSettings.CourseInfoFontSize)
            or nameof(PluginSettings.CountFontSize))
        {
            // 字号：设置页那边每拖一格就写一次设置，这里立刻搬到界面上。
            //
            // 以前字号只靠设置页那根 200 毫秒防抖的「重读一遍」同步，而防抖是「一停手才计时」：
            // 拖的过程中一直不动，手指打个顿才跳一下 —— 看着就是一卡一卡的。
            // 位置滑块是即时读设置的（ClockSurface 里直接吃 ClockPositionPercent），
            // 所以同样在拖，位置丝滑、字号发顿。这里补上这一条即时通道，两边就一样了。
            ClockFontSize = _settings.ClockFontSize;
            CourseInfoFontSize = _settings.CourseInfoFontSize;
            CountFontSize = _settings.CountFontSize;
        }
        else if (args.PropertyName is nameof(PluginSettings.SkipFirst3Min)
            or nameof(PluginSettings.SkipFirstMinutes)
            or nameof(PluginSettings.NoisySustainSeconds)
            or nameof(PluginSettings.FallWindowSeconds))
        {
            OnPropertyChanged(nameof(CountRulesText));
        }
        else if (args.PropertyName is nameof(PluginSettings.ShowReminderPanel)
            or nameof(PluginSettings.ShowWeatherReminder)
            or nameof(PluginSettings.ShowCountdownReminder)
            or nameof(PluginSettings.ShowTextReminder)
            or nameof(PluginSettings.ShowRainReminder))
        {
            NotifyReminderChanged();
        }
        else if (args.PropertyName is nameof(PluginSettings.ShowEarthquakeReminder))
        {
            // 地震提醒开关：立刻显示/收起，不用等下一次整点刷新
            UpdateEarthquakeAlert();
        }
        else if (args.PropertyName is nameof(PluginSettings.ShowAlertsReminder)
            or nameof(PluginSettings.ShowCameraReminder)
            or nameof(PluginSettings.CameraAlertFontSize)
            or nameof(PluginSettings.MonitoredCameras))
        {
            // 预警/摄像头开关或勾选的设备变了 → 立刻重算条目（集合内容变化不会走上面那支）
            if (args.PropertyName is nameof(PluginSettings.ShowCameraReminder))
            {
                // 大屏正开着时改开关：立即开始/停止监视（没开大屏则等 Show() 时再起）
                // 最小化暂停期间不因为改设置把监视重新拉起来（点回时 Resume 会统一恢复）
                if (_settings.ShowCameraReminder && IsWindowVisible && !_pausedForMinimize)
                    _cameraService.StartMonitoring();
                else if (!_settings.ShowCameraReminder) _cameraService.StopMonitoring();
            }
            UpdateAlertItems();
        }
        else if (args.PropertyName is nameof(PluginSettings.ShowEmojiSubtitles))
        {
            // 颜文字开关：全部副标题即时重算（清空/恢复）
            OnPropertyChanged(nameof(RainSubtitle));
            OnPropertyChanged(nameof(HasRainSubtitle));
            UpdateWeatherSubtitle();
            UpdateDateSubtitle();
            UpdateCountdownSubtitle();
        }
        else if (args.PropertyName is nameof(PluginSettings.ShowBellTime))
        {
            // 「显示精确打铃时间」子项：即时把末尾那段加减回去/去掉
            UpdateCourseInfo(NowVirtual);
        }
        else if (args.PropertyName is nameof(PluginSettings.BackgroundEnabled)
                 or nameof(PluginSettings.BackgroundThemeMode))
        {
            // 背景开关一拨，说了算的主题就从「外观那项」换成「背景那项」（或反过来）
            Dispatcher.UIThread.Post(ApplyThemePalette);
        }
        else if (args.PropertyName is nameof(PluginSettings.BackgroundImageFile)
                 or nameof(PluginSettings.BackgroundDim))
        {
            // 只有「自动」档要重判：它看的就是背景图明暗，换了图 / 调了遮罩都可能翻主题。
            // 固定档下拖明暗滑块与主题无关（遮罩本身由背景层自己画），别白刷一遍画刷
            if (_settings.BackgroundEnabled && _settings.BackgroundThemeMode == (int)BackgroundThemeMode.Auto)
                Dispatcher.UIThread.Post(ApplyThemePalette);
        }
        else if (args.PropertyName is nameof(PluginSettings.FollowClassIslandAccent))
        {
            // 关掉「跟随 CI 主题色」→ 立刻把抄来的强调色清空：设置页显示「回退主题自带色」，
            // 配色彻底回到 1.3.1.1。重新打开 → 当场再读一次 CI 的强调色。
            ClockThemeApplier.SyncAccent(_settings, _settings.FollowClassIslandAccent ? CiThemeReader.TryReadAccentColor() : null);
            _progressFillBrush = null;
            OnPropertyChanged(nameof(ProgressFillBrush));
        }
        else if (args.PropertyName is nameof(PluginSettings.ThemeAccentColor))
        {
            // 强调色（CI 同步或设置页手改）变了：进度条色可能换人，丢掉画刷缓存重新算
            _progressFillBrush = null;
            OnPropertyChanged(nameof(ProgressFillBrush));
        }
        else if (args.PropertyName is nameof(PluginSettings.ThemeMode))
        {
            // 设置页里切了主题（颜色已被设置页改写）→ 大屏时钟正开着也立刻换配色
            Dispatcher.UIThread.Post(() =>
            {
                ApplyThemePalette();
                RefreshAppearanceBindings();
            });
        }
    }

    public string CurrentTime
    {
        get => _currentTime;
        set => SetProperty(ref _currentTime, value);
    }

    public string CurrentDate
    {
        get => _currentDate;
        set => SetProperty(ref _currentDate, value);
    }

    public string CurrentCourseName
    {
        get => _currentCourseName;
        set => SetProperty(ref _currentCourseName, value);
    }

    public bool IsWindowVisible
    {
        get => _isWindowVisible;
        private set { _isWindowVisible = value; OnPropertyChanged(); }
    }

    // 这 6 个颜色就是设置里那套值：主题档只决定「切主题时把哪一套默认色写进来」，不在这里改算。
    public string BackgroundColor => _settings.BackgroundColor;
    public string FontColor => _settings.FontColor;
    public string ProgressColor => _settings.ProgressColor;
    public string CourseInfoColor => _settings.CourseInfoColor;
    public string NoiseTitleColor => _settings.NoiseTitleColor;
    public string RainColor => _settings.RainColor;

    /// <summary>
    /// 当前主题的界面「底色 / 文字」画刷（背景白/黑时整批翻转）。
    /// 界面绑 Chrome.Xxx，换主题只需通知这一个属性。
    /// </summary>
    public ClockBrushes Chrome { get; private set; } = ClockBrushes.From(ClockTheme.Dark);

    /// <summary>
    /// 时钟数字字体：跟随 CI 主界面字体（CI 里选什么字体，大屏时钟就用什么），
    /// 换掉原来的 Consolas（数字 0 中间带斜杠）。
    /// </summary>
    public FontFamily ClockFontFamily { get; private set; } = FontFamily.Parse(DefaultClockFont);

    /// <summary>数字用表格宽度（tnum）：秒数跳动时整行不左右抖。</summary>
    public FontFeatureCollection ClockFontFeatures { get; } =
        new FontFeatureCollection { FontFeature.Parse("tnum") };

    /// <summary>读不到 CI 字体设置时的回退（原来的窗口字体）。</summary>
    private const string DefaultClockFont = "Microsoft YaHei UI, SimHei, sans-serif";

    /// <summary>
    /// 进度条「已进行」部分颜色。跟随 CI 强调色且用户没改过进度条色时，这里拿到的是强调色
    /// （判据见 <see cref="ClockThemeApplier.ResolveProgressColor(PluginSettings)"/> 的重载）。
    /// 画刷实例缓存复用：这个 getter 被绑定，每次 new 画刷都会让控件反复失效重绘。
    /// </summary>
    public IBrush ProgressFillBrush
        => _progressFillBrush ??= new SolidColorBrush(Color.Parse(ClockThemeApplier.ResolveProgressColor(_settings)));

    /// <summary>进度带「未进行」部分颜色（随主题：深色底上的低饱和深蓝 / 浅色底上的浅灰蓝）。</summary>
    public IBrush ProgressTrackBrush => Chrome.VolumeTrack;

    /// <summary>降水提醒色（设置项，默认蓝）：降雨提醒块用它，颜色可在设置里调。画刷实例缓存复用。</summary>
    public IBrush RainColorBrush => _rainColorBrush ??= new SolidColorBrush(Color.Parse(RainColor));

    /// <summary>当前天气是不是降水类（按 CI 天气码判断；雾/霾/沙尘不算）。</summary>
    private bool IsPrecipitationWeather => _weatherCode is { } code && CiWeatherReader.RainCodes.Contains(code);

    /// <summary>天气块的颜色：当前在下雨/下雪时用降水色（蓝），否则用主题正文色。</summary>
    public IBrush WeatherBrush => IsPrecipitationWeather ? RainColorBrush : Chrome.TextPrimary;

    /// <summary>
    /// 触发所有外观属性变更通知，让窗口绑定重新求值。
    /// 窗口从 Hide 退出后复用时，这些表达式体 getter 不会自动刷新，需主动通知。
    /// </summary>
    private void RefreshAppearanceBindings()
    {
        OnPropertyChanged(nameof(BackgroundColor));
        OnPropertyChanged(nameof(FontColor));
        OnPropertyChanged(nameof(ProgressColor));
        OnPropertyChanged(nameof(ProgressFillBrush));
        OnPropertyChanged(nameof(ProgressTrackBrush));
        OnPropertyChanged(nameof(CourseInfoColor));
        OnPropertyChanged(nameof(NoiseTitleColor));
        OnPropertyChanged(nameof(RainColor));
        OnPropertyChanged(nameof(RainColorBrush));
        OnPropertyChanged(nameof(WeatherBrush));
        OnPropertyChanged(nameof(ClockFontSize));
        OnPropertyChanged(nameof(CourseInfoFontSize));
        OnPropertyChanged(nameof(CountFontSize));
        OnPropertyChanged(nameof(WindowTitle));
    }

    /// <summary>CI 当前是不是深色主题。取 Application 的生效变体 —— CI 的主题设置改的就是它。</summary>
    private static bool CiIsDark => Application.Current?.ActualThemeVariant != ThemeVariant.Light;

    /// <summary>按设置里的主题档位 + CI 当前亮暗刷新界面画刷与语义色（不改动颜色设置）。</summary>
    private void RefreshChrome()
    {
        Chrome = ClockBrushes.From(ClockThemeApplier.ResolvePalette(_settings, CiIsDark));
        // 主题/强调色都可能在两次刷新之间变过：丢掉画刷缓存，下次求值按新颜色重建
        _progressFillBrush = null;
        _rainColorBrush = null;
        OnPropertyChanged(nameof(Chrome));
        OnPropertyChanged(nameof(NoiseLevelTrackBrush));
        OnPropertyChanged(nameof(ProgressTrackBrush));
        OnPropertyChanged(nameof(ProgressFillBrush));
        OnPropertyChanged(nameof(RainColorBrush));
        OnPropertyChanged(nameof(WeatherBrush));   // 非降水天气的天气块用主题正文色，换主题要跟着变
        RefreshSemanticColors();
    }

    /// <summary>
    /// 语义色（五档文字、预警等级、吵闹计数）随底色深浅换一版：
    /// 浅底上黄绿/黄的原始档位色和预警黄几乎看不见，改用同色相的压暗版。
    /// </summary>
    private void RefreshSemanticColors()
    {
        ApplySlotTheme();

        // 预警列表：就地改色，不重建列表（重建会把正在悬停的那条弹幕重置重播）
        var light = Chrome.IsLight;
        foreach (var a in AlertItems)
            a.Foreground = AlertBrush(a.Level, light);

        // 地震提醒（等级色也分明暗两版）
        NotifyEarthquakeChanged();

        // 计数片段：签名只记 黄/红/其它，换主题后签名不变会被判成「没变化」而不刷新，
        // 故清掉签名强制重建一次（下面会各自填回新颜色）
        _noisyDisplaySignature = "";
        UpdateNoisyDisplay();
        RefreshRecordingColor();

        // 预览里的示意计数直接绑这两个（不经过片段列表），换主题得单独通知
        OnPropertyChanged(nameof(CountNormalForeground));
        OnPropertyChanged(nameof(CountNoisyForeground));
    }

    /// <summary>档位文字：换主题时同时换「未达到/已达到」两色与淡化程度（浅底要淡得少些）。</summary>
    private void ApplySlotTheme()
    {
        var light = Chrome.IsLight;
        var brushes = light ? SlotTextBrushesLight : SlotTextBrushesDark;
        var reached = light ? SlotReachedBrushesLight : SlotReachedBrushesDark;
        var fade = light ? SlotFadeLight : SlotFadeDark;
        for (var i = 0; i < _noiseLevelSlots.Count; i++)
            _noiseLevelSlots[i].SetStyle(brushes[i], reached[i], fade);
        OnPropertyChanged(nameof(NoiseLevelTextBrush));
    }

    private static bool IsCountNormal(IBrush b) => ReferenceEquals(b, CountNormalBrush) || ReferenceEquals(b, CountNormalLightBrush);
    private static bool IsCountNoisy(IBrush b) => ReferenceEquals(b, CountNoisyBrush) || ReferenceEquals(b, CountNoisyLightBrush);

    /// <summary>
    /// 应用主题：生效主题变了就把 5 个颜色写成该主题的默认值（见 <see cref="ClockThemeApplier"/>），
    /// 再刷新画刷。进入大屏时钟时、以及跟随档下 CI 主题变化时调用。
    /// </summary>
    private void ApplyThemePalette()
    {
        // 先同步 CI 强调色（只写 ThemeAccentColor），再套调色板：进度条色依赖它，
        // 顺序反了会让「换强调色」要等下一次刷新才生效。
        SyncAccentFromCi();
        var colorsChanged = ClockThemeApplier.Apply(_settings, CiIsDark);
        RefreshChrome();
        if (colorsChanged) RefreshAppearanceBindings();
    }

    /// <summary>
    /// 从 CI 读一次强调色并单向写进设置（<see cref="PluginSettings.ThemeAccentColor"/>）。
    /// 返回 true 表示强调色变了。读不到强调色时 <see cref="ClockThemeApplier.SyncAccent"/> 会把
    /// ThemeAccentColor 清空 —— 配色完全回退到本插件主题自带的那套。
    /// </summary>
    private bool SyncAccentFromCi()
    {
        try { return ClockThemeApplier.SyncAccent(_settings, CiThemeReader.TryReadAccentColor()); }
        catch { return false; }
    }

    /// <summary>
    /// 订阅 CI 主题变化（换主题 / 换强调色）。幂等：宿主服务或 Avalonia Application
    /// 晚于本 VM 构造就绪时，打开大屏会再补订一次。
    /// 两个信号任一到达都够用：ThemeUpdated 是 CI 明确通知，ResourcesChanged 兜住
    /// 「只换强调色、没走主题切换流程」的情况。
    /// </summary>
    private void EnsureCiThemeSubscribed()
    {
        if (!_ciThemeHooked)
        {
            try
            {
                _ciThemeService ??= _serviceProvider.GetService<IThemeService>();
                if (_ciThemeService is not null)
                {
                    _ciThemeService.ThemeUpdated += OnCiThemeUpdated;
                    _ciThemeHooked = true;
                }
            }
            catch { /* 拿不到主题服务：退化为打开大屏/预览时各读一次（ApplyThemePalette 里就是） */ }
        }

        if (!_ciResourcesHooked)
        {
            try
            {
                if (Application.Current is { } app)
                {
                    app.ResourcesChanged += OnCiResourcesChanged;
                    _ciResourcesHooked = true;
                }
            }
            catch { }
        }
    }

    /// <summary>CI 主题变化（明暗 / 强调色）→ 大屏正开着就立刻换配色，与设置页预览同步。</summary>
    private void OnCiThemeUpdated(object? sender, ThemeUpdatedEventArgs e)
    {
        // 可能来自 CI 的后台线程：读资源与改画刷都排回 UI 线程
        CiThemeReader.Invalidate();
        Dispatcher.UIThread.Post(() =>
        {
            try { ApplyThemePalette(); }
            catch { /* 主题服务给的时机不一定落在可用状态，失败就等下一次刷新 */ }
        });
    }

    /// <summary>
    /// CI 资源重建（换主题/换强调色都会触发）。只在强调色真的变了时刷新，
    /// 否则 CI 任何资源变动都会把大屏整体重排一遍，白费性能。
    /// </summary>
    private void OnCiResourcesChanged(object? sender, ResourcesChangedEventArgs e)
    {
        CiThemeReader.Invalidate();
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                if (!SyncAccentFromCi()) return;
                RefreshChrome();
                RefreshAppearanceBindings();
            }
            catch { }
        });
    }

    /// <summary>刷新时钟字体：重新读 CI 设置里的主界面字体（用户可能在 CI 里改过）。</summary>
    private void RefreshClockFont()
    {
        var font = CiMainFont.ReadFontString(CiDataDir) ?? DefaultClockFont;
        try { ClockFontFamily = FontFamily.Parse(font); }
        catch { ClockFontFamily = FontFamily.Parse(DefaultClockFont); }
        OnPropertyChanged(nameof(ClockFontFamily));
    }

    public int ClockFontSize
    {
        get => _clockFontSize;
        set { _clockFontSize = value; OnPropertyChanged(); }
    }

    private bool _showDecibelMeter = true;
    private bool _showCourseInfo = true;
    private int _clockFontSize = 180;
    private int _courseInfoFontSize = 28;
    private int _countFontSize = 24;

    public bool ShowDecibelMeter
    {
        get => _showDecibelMeter;
        set
        {
            if (_showDecibelMeter == value) return;
            _showDecibelMeter = value;
            OnPropertyChanged();
            // 音量条在不在，决定「正在记录」站哪儿（见 RecordingInMeterTitle/RecordingInCounter）
            NotifyRecordingPlacement();
        }
    }

    public string WindowTitle => _settings.WindowTitle;

    // ===== 时钟/音量条滑块的可拖范围（百分比） =====
    // 大屏那边按限位算出「实际能落到哪」后写进来（见 FullScreenClockWindow.ApplyBodyPositions），
    // 设置页的滑块拿它当 Minimum/Maximum。不这么做滑块能一路拖到 0，
    // 而屏幕上早就顶住不动了——「还能继续调小、右边数字却不变」（用户 2026-10-01 反馈）。

    public double ClockPositionMin => _clockPositionMin;
    public double ClockPositionMax => _clockPositionMax;
    public double VolumePositionMin => _volumePositionMin;
    public double VolumePositionMax => _volumePositionMax;

    private double _clockPositionMin;
    private double _clockPositionMax = 100;
    private double _volumePositionMin;
    private double _volumePositionMax = 100;

    /// <summary>大屏那边算完限位后同步滑块范围（全屏时钟没开时是默认的 0~100，等同不限制）。</summary>
    public void SetPositionLimits(double clockMin, double clockMax, double volumeMin, double volumeMax)
    {
        SetLimit(ref _clockPositionMin, clockMin, nameof(ClockPositionMin));
        SetLimit(ref _clockPositionMax, clockMax, nameof(ClockPositionMax));
        SetLimit(ref _volumePositionMin, volumeMin, nameof(VolumePositionMin));
        SetLimit(ref _volumePositionMax, volumeMax, nameof(VolumePositionMax));
    }

    /// <summary>全屏时钟收起时恢复成整段可拖（这时没有任何限位在起作用）。</summary>
    public void ResetPositionLimits() => SetPositionLimits(0, 100, 0, 100);

    private void SetLimit(ref double field, double value, string propertyName)
    {
        if (Math.Abs(field - value) < 0.05) return;   // 布局每轮都算一遍，没变就别惊动绑定
        field = value;
        OnPropertyChanged(propertyName);
    }

    /// <summary>
    /// 把「平时只在 <see cref="Show"/> 里同步一次」的那几项（字号、音量条/课程信息开关、五个颜色、时钟字体、
    /// 提醒数据）当场从设置里重读一遍，供设置页的预览调用。
    ///
    /// 为什么需要它：预览绑的就是这个实例，但字号、颜色这些平时只有「进大屏」那一刻才从设置里搬过来，
    /// 不重读的话，在设置页改了字号、拖了颜色，预览要等下一次进大屏才跟上 —— 那就等于没有预览。
    ///
    /// 与 <see cref="Show"/> 的区别：这里不碰窗口、不开始监测、不重置计数，纯粹是把显示值刷一遍，
    /// 所以设置页可以随时调，也能在大屏正开着的时候调（等于顺手帮大屏也刷新一次，没有副作用）。
    /// </summary>
    public void RefreshForPreview()
    {
        ShowDecibelMeter = _settings.ShowDecibelMeter;
        ShowCourseInfo = _settings.ShowCourseInfo;
        ClockFontSize = _settings.ClockFontSize;
        CourseInfoFontSize = _settings.CourseInfoFontSize;
        CountFontSize = _settings.CountFontSize;

        // 主题色不在这里 Apply 回设置（那是设置页自己的事），只按当前生效的颜色重算画刷
        RefreshChrome();
        RefreshAppearanceBindings();
        RefreshClockFont();
        // 只重读 CI 组件配置（内存数据）；天气那路要读 CI 的 Settings.json 磁盘文件，
        // 预览每 200ms 刷新一次，放这里等于把「重复读盘」热点重新引回来。
        // 天气/预警本来就有 60s 定时刷新，设置开关变化也各自走 OnSettingsPropertyChanged。
        RefreshComponents();
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// 只在值真的变化时才发通知。1s 刷新循环里大部分属性（日期、课名、倒计时文案）每秒算出的
    /// 是同一个值，无条件通知会让绑定每秒重排一次——这就是「大屏有微弱掉帧」的来源之一。
    /// </summary>
    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
