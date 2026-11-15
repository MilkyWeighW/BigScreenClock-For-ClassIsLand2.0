// ===== SettingsPage 拆分之一：主题与外观 =====
// 职责：主题档位（跟随 ClassIsland / 明亮 / 黑暗）、背景主题档位、主题切换后设置页这半边的
//       颜色刷新、HSV 颜色选择器绑定的 Color/色块/hex，以及「显示什么」「提醒面板里显示什么」
//       这些开关。成员与逻辑从原 SettingsPage.axaml.cs 原样搬移。

using System.ComponentModel;
using Avalonia.Media;
using Avalonia.Styling;
using LargeScreenClockRevived.Helpers;
using LargeScreenClockRevived.Models;
using LargeScreenClockRevived.Services;

namespace LargeScreenClockRevived.Settings;

public partial class SettingsPage
{

    // ===== 主题 =====

    /// <summary>CI 当前是不是深色主题。设置页就在 CI 的窗口里，自身生效变体即 CI 的。</summary>
    private bool CiIsDark => ActualThemeVariant != ThemeVariant.Light;

    /// <summary>CI 数据目录（data\），插件配置目录往上三级。</summary>
    private string CiDataDir => Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..", "..", ".."));

    /// <summary>外观主题档位：0 = 跟随 ClassIsland，1 = 明亮，2 = 黑暗。背景开关打开时整项失效。</summary>
    public int ThemeModeIndex
    {
        get => _settings.ThemeMode;
        set
        {
            if (_settings.ThemeMode == value) return;
            _settings.ThemeMode = value;
            // 切主题即套用该主题的颜色默认值（之后仍可手动微调），预览立刻跟着变
            ClockThemeApplier.Apply(_settings, CiIsDark);
            OnPropertyChanged(nameof(ThemeModeIndex));
            NotifyAllColorsChanged();
            ApplyThemeFromSettings();
        }
    }

    /// <summary>背景开关打开后，外观里那项「主题」就不起作用了，置灰。</summary>
    public bool IsAppearanceThemeEnabled => !_settings.BackgroundEnabled;

    /// <summary>
    /// 背景总开关：关着一整组背景设置都不生效，外观与没有这套功能时逐像素一致。
    /// （本轮 axaml 不再暴露这个开关，PluginSettings 里字段保留。）
    /// 默认关闭 —— 升级上来的配置没这个字段，默认开着会在用户没点过任何东西的情况下换掉他的主题。
    /// </summary>
    public bool BackgroundEnabled
    {
        get => _settings.BackgroundEnabled;
        set
        {
            if (_settings.BackgroundEnabled == value) return;
            _settings.BackgroundEnabled = value;
            OnPropertyChanged(nameof(BackgroundEnabled));
            OnPropertyChanged(nameof(IsAppearanceThemeEnabled));
            OnPropertyChanged(nameof(BackgroundSectionOpacity));
            // 打开就把这一组展开，省得再点一下
            if (value) IsBackgroundSectionExpanded = true;
            RefreshThemeFromBackground();
        }
    }

    /// <summary>
    /// 「背景」那一组在开关关着时整体压暗用的透明度。
    /// Expander 上的 IsEnabled=false 只会让下拉、按钮这些模板自带禁用态的控件变灰，
    /// 那几行说明文字各自写了 Foreground，不跟着变 —— 只靠 IsEnabled 看着像还能调。
    /// </summary>
    public double BackgroundSectionOpacity => _settings.BackgroundEnabled ? 1.0 : 0.4;

    /// <summary>「背景」那一组是不是展开着。默认收起，拨开上面的开关会自动展开。</summary>
    public bool IsBackgroundSectionExpanded
    {
        get => _settings.BackgroundSectionExpanded;
        set
        {
            if (_settings.BackgroundSectionExpanded == value) return;
            _settings.BackgroundSectionExpanded = value;
            OnPropertyChanged(nameof(IsBackgroundSectionExpanded));
        }
    }

    /// <summary>
    /// 背景主题档位：0 = 自动（跟着背景图），1 = 暗底白字，2 = 亮底黑字。
    /// 只在背景开关打开时生效——名字按看到的样子和字来叫，免得「黑暗」在亮壁纸上反而调出一屏白字。
    /// </summary>
    public int BackgroundThemeModeIndex
    {
        get => _settings.BackgroundThemeMode;
        set
        {
            if (_settings.BackgroundThemeMode == value) return;
            _settings.BackgroundThemeMode = value;
            OnPropertyChanged(nameof(BackgroundThemeModeIndex));
            RefreshThemeFromBackground();
        }
    }

    /// <summary>
    /// 背景图 / 明暗遮罩变了：「自动」主题档判的就是它俩，可能整批翻主题。
    /// 没翻就只刷新预览（不然拖遮罩滑块时每格都要重读一次字体等无关东西）。
    /// </summary>
    private void RefreshThemeFromBackground()
    {
        if (ClockThemeApplier.Apply(_settings, CiIsDark))
        {
            NotifyAllColorsChanged();
            ApplyThemeFromSettings();   // 里面会再刷一次预览
            return;
        }
        SchedulePreviewSync();
    }

    /// <summary>
    /// 换主题后设置页这半边要跟着翻（色块、hex），预览那半边由 ClockSurface 自己盯着 ViewModel 走，
    /// 不用在这里另刷一遍。
    /// </summary>
    private void ApplyThemeFromSettings()
    {
        NotifyAllColorsChanged();
        SchedulePreviewSync();
    }

    /// <summary>5 个颜色设置的值/色块/hex 全部重新求值（主题切换、颜色被改写后调用）。</summary>
    private void NotifyAllColorsChanged()
    {
        OnPropertyChanged(nameof(BackgroundColorValue));
        OnPropertyChanged(nameof(BackgroundColorBrush));
        OnPropertyChanged(nameof(BackgroundColorHex));
        OnPropertyChanged(nameof(FontColorValue));
        OnPropertyChanged(nameof(FontColorBrush));
        OnPropertyChanged(nameof(FontColorHex));
        OnPropertyChanged(nameof(ProgressColorValue));
        OnPropertyChanged(nameof(ProgressColorBrush));
        OnPropertyChanged(nameof(ProgressColorHex));
        OnPropertyChanged(nameof(CourseInfoColorValue));
        OnPropertyChanged(nameof(CourseInfoColorBrush));
        OnPropertyChanged(nameof(CourseInfoColorHex));
        OnPropertyChanged(nameof(NoiseTitleColorValue));
        OnPropertyChanged(nameof(NoiseTitleColorBrush));
        OnPropertyChanged(nameof(NoiseTitleColorHex));
        OnPropertyChanged(nameof(RainColorValue));
        OnPropertyChanged(nameof(RainColorBrush));
        OnPropertyChanged(nameof(RainColorHex));
    }

    // ShowDecibelMeter / ShowCourseInfo / ShowNoisyCounter 三个包装属性已删除：
    // 组件化之后显隐由各组件自己的 Enabled 管，XAML 里已经没有绑定，留着只会让人以为还有一条生效路径。
    // 设置对象上的同名字段仍然保留——它们是「老配置 → 组件启用」迁移的输入（见 MigrateLegacyComponentVisibility）。
    // ShowBellTime 是例外：课程信息那一行里「显示打铃时间」仍是生效设置，XAML 还在绑它。

    public bool ShowBellTime
    {
        get => _settings.ShowBellTime;
        set { _settings.ShowBellTime = value; OnPropertyChanged(nameof(ShowBellTime)); }
    }

    // ===== 提醒面板开关 =====
    public bool ShowReminderPanel
    {
        get => _settings.ShowReminderPanel;
        set { _settings.ShowReminderPanel = value; OnPropertyChanged(nameof(ShowReminderPanel)); }
    }

    public bool ShowWeatherReminder
    {
        get => _settings.ShowWeatherReminder;
        set { _settings.ShowWeatherReminder = value; OnPropertyChanged(nameof(ShowWeatherReminder)); }
    }

    public bool ShowAlertsReminder
    {
        get => _settings.ShowAlertsReminder;
        set { _settings.ShowAlertsReminder = value; OnPropertyChanged(nameof(ShowAlertsReminder)); }
    }

    public bool ShowCountdownReminder
    {
        get => _settings.ShowCountdownReminder;
        set { _settings.ShowCountdownReminder = value; OnPropertyChanged(nameof(ShowCountdownReminder)); }
    }

    public bool ShowTextReminder
    {
        get => _settings.ShowTextReminder;
        set { _settings.ShowTextReminder = value; OnPropertyChanged(nameof(ShowTextReminder)); }
    }

    public bool ShowRainReminder
    {
        get => _settings.ShowRainReminder;
        set { _settings.ShowRainReminder = value; OnPropertyChanged(nameof(ShowRainReminder)); }
    }

    public bool ShowCameraReminder
    {
        get => _settings.ShowCameraReminder;
        set { _settings.ShowCameraReminder = value; OnPropertyChanged(nameof(ShowCameraReminder)); }
    }

    /// <summary>「摄像头使用中」那一条的字号。范围就是「默认 ~ 天气提醒字号」。</summary>
    public double CameraAlertFontSize
    {
        get => _settings.CameraAlertFontSize;
        set
        {
            if (Math.Abs(_settings.CameraAlertFontSize - value) < 0.01) return;
            BeginLayoutAdjust();
            _settings.CameraAlertFontSize = value;
            OnPropertyChanged(nameof(CameraAlertFontSize));
            OnPropertyChanged(nameof(CameraAlertFontSizeText));
        }
    }

    public string CameraAlertFontSizeText => $"{_settings.CameraAlertFontSize:0}px";

    /// <summary>地震提醒（读「地震预警」插件的实时状态，在时钟正上方弹出）。</summary>
    public bool ShowEarthquakeReminder
    {
        get => _settings.ShowEarthquakeReminder;
        set { _settings.ShowEarthquakeReminder = value; OnPropertyChanged(nameof(ShowEarthquakeReminder)); }
    }

    /// <summary>没装「地震预警」插件时，在那一项下方给出原因。</summary>
    public bool ShowEarthquakePluginHint => !_earthquakePluginAvailable;

    private static bool CheckEarthquakePlugin()
    {
        try
        {
            return Plugin.ServiceProvider is { } sp && new EarthquakeReader(sp).IsAvailable();
        }
        catch
        {
            return false;
        }
    }

    public bool ShowEmojiSubtitles
    {
        get => _settings.ShowEmojiSubtitles;
        set { _settings.ShowEmojiSubtitles = value; OnPropertyChanged(nameof(ShowEmojiSubtitles)); }
    }

    /// <summary>是否在 CI 托盘右键菜单显示「进入大屏时钟」入口。</summary>
    public bool ShowTrayMenuEntry
    {
        get => _settings.ShowTrayMenuEntry;
        set { _settings.ShowTrayMenuEntry = value; OnPropertyChanged(nameof(ShowTrayMenuEntry)); }
    }

    /// <summary>最小化超过设定时间后暂停运行（不关窗口，任务栏入口保留）。</summary>
    public bool PauseWhenMinimized
    {
        get => _settings.PauseWhenMinimized;
        set { _settings.PauseWhenMinimized = value; OnPropertyChanged(nameof(PauseWhenMinimized)); }
    }

    public int MinimizePauseMinutes
    {
        get => _settings.MinimizePauseMinutes;
        set
        {
            _settings.MinimizePauseMinutes = value;
            OnPropertyChanged(nameof(MinimizePauseMinutes));
            OnPropertyChanged(nameof(MinimizePauseMinutesText));
        }
    }

    public string MinimizePauseMinutesText => $"{_settings.MinimizePauseMinutes} 分钟";

    // ===== 主题色（1.4.0.0）=====
    //
    // 强调色由 platform 侧从 ClassIsland 主题读取后写进 PluginSettings.ThemeAccentColor，这里只负责展示。
    // 取不到值（CI 主题服务不可用 / 老配置为空）不算错误：色块退回插件自带的默认蓝，并把回退方式
    // 写在说明里 —— 用户看到颜色没变时能知道是「回退了」而不是「坏了」。

    /// <summary>
    /// 主题色是否跟随 ClassIsland。打开时由 platform 侧在启动与 CI 主题切换时抄强调色，
    /// 这里只落开关值；关掉后沿用已有的 <see cref="PluginSettings.ThemeAccentColor"/>（空则用默认蓝）。
    /// </summary>
    public bool FollowClassIslandAccent
    {
        get => _settings.FollowClassIslandAccent;
        set
        {
            if (_settings.FollowClassIslandAccent == value) return;
            _settings.FollowClassIslandAccent = value;
            NotifyThemeAccentChanged();
        }
    }

    /// <summary>ClassIsland 本身的默认强调色：跟随开关打开、但还没取到 CI 颜色时拿它兜底，免得色块是空的。</summary>
    private static readonly Color DefaultAccent = Color.FromRgb(0x00, 0x78, 0xD4);

    private Color AccentColorValue => string.IsNullOrWhiteSpace(_settings.ThemeAccentColor)
        ? DefaultAccent
        : DisplayColor(_settings.ThemeAccentColor);

    /// <summary>主题色预览色块。不直接绑 ColorPicker：强调色是只读展示，写回由 platform 的读取逻辑负责。</summary>
    public IBrush ThemeAccentBrush => new SolidColorBrush(AccentColorValue);

    /// <summary>当前生效的强调色文本；还没取到值时说明「自动 / 默认」，别让用户对着空色块猜。</summary>
    public string ThemeAccentText
    {
        get
        {
            if (!string.IsNullOrWhiteSpace(_settings.ThemeAccentColor))
                return ToHexRgb(AccentColorValue);
            return _settings.FollowClassIslandAccent ? "自动（未取到值时为默认蓝）" : "插件默认主题色";
        }
    }

    /// <summary>回退说明：跟随打开时讲清「读不到就回退」，关掉时讲清「不再随 CI 变」。</summary>
    public string ThemeAccentHint => _settings.FollowClassIslandAccent
        ? "进入大屏与 CI 主题切换时从 ClassIsland 当前主题读取强调色；读取失败则用插件默认色。"
        : "已关闭跟随：沿用当前强调色，不再随 ClassIsland 主题变化。";

    /// <summary>
    /// 强调色 / 跟随开关变了（含 platform 侧写回设置）时，把这一组的绑定全部重发一次。
    /// 这几个属性是转发到 settings 的包装器，settings 变了它们自己不会发通知。
    /// </summary>
    private void NotifyThemeAccentChanged()
    {
        OnPropertyChanged(nameof(FollowClassIslandAccent));
        OnPropertyChanged(nameof(ThemeAccentBrush));
        OnPropertyChanged(nameof(ThemeAccentText));
        OnPropertyChanged(nameof(ThemeAccentHint));
    }

    // ===== 外观颜色（HSV 颜色选择器，Avalonia.Controls.ColorPicker 双向绑定） =====

    // ColorPicker 的 Color 属性是 Avalonia.Media.Color（默认 TwoWay），
    // 这里把它与 PluginSettings 的 hex string 互相转换；Color.ToString() 输出 #AARRGGBB，Color.Parse 可读回。

    public Color RainColorValue
    {
        get => DisplayColor(_settings.RainColor);
        set
        {
            _settings.RainColor = value.ToString();
            OnPropertyChanged(nameof(RainColorValue));
            OnPropertyChanged(nameof(RainColorBrush));
            OnPropertyChanged(nameof(RainColorHex));
        }
    }

    /// <summary>色块上显示的值（就是设置里存的那个；解析不了就按黑色显示）。</summary>
    private static Color DisplayColor(string hex)
    {
        try { return Color.Parse(hex); }
        catch { return Colors.Black; }
    }

    public Color BackgroundColorValue
    {
        get => DisplayColor(_settings.BackgroundColor);
        set
        {
            _settings.BackgroundColor = value.ToString();
            OnPropertyChanged(nameof(BackgroundColorValue));
            OnPropertyChanged(nameof(BackgroundColorBrush));
            OnPropertyChanged(nameof(BackgroundColorHex));
        }
    }

    public Color FontColorValue
    {
        get => DisplayColor(_settings.FontColor);
        set
        {
            _settings.FontColor = value.ToString();
            OnPropertyChanged(nameof(FontColorValue));
            OnPropertyChanged(nameof(FontColorBrush));
            OnPropertyChanged(nameof(FontColorHex));
        }
    }

    public Color ProgressColorValue
    {
        get => DisplayColor(_settings.ProgressColor);
        set
        {
            _settings.ProgressColor = value.ToString();
            OnPropertyChanged(nameof(ProgressColorValue));
            OnPropertyChanged(nameof(ProgressColorBrush));
            OnPropertyChanged(nameof(ProgressColorHex));
        }
    }

    public Color CourseInfoColorValue
    {
        get => DisplayColor(_settings.CourseInfoColor);
        set
        {
            _settings.CourseInfoColor = value.ToString();
            OnPropertyChanged(nameof(CourseInfoColorValue));
            OnPropertyChanged(nameof(CourseInfoColorBrush));
            OnPropertyChanged(nameof(CourseInfoColorHex));
        }
    }

    public Color NoiseTitleColorValue
    {
        get => DisplayColor(_settings.NoiseTitleColor);
        set
        {
            _settings.NoiseTitleColor = value.ToString();
            OnPropertyChanged(nameof(NoiseTitleColorValue));
            OnPropertyChanged(nameof(NoiseTitleColorBrush));
            OnPropertyChanged(nameof(NoiseTitleColorHex));
        }
    }

    // 供 ColorPicker 自定义预览（大色块）绑定：色块填充色 + 显示用 #RRGGBB
    public IBrush BackgroundColorBrush => new SolidColorBrush(BackgroundColorValue);
    public string BackgroundColorHex => ToHexRgb(BackgroundColorValue);
    public IBrush FontColorBrush => new SolidColorBrush(FontColorValue);
    public string FontColorHex => ToHexRgb(FontColorValue);
    public IBrush ProgressColorBrush => new SolidColorBrush(ProgressColorValue);
    public string ProgressColorHex => ToHexRgb(ProgressColorValue);
    public IBrush CourseInfoColorBrush => new SolidColorBrush(CourseInfoColorValue);
    public string CourseInfoColorHex => ToHexRgb(CourseInfoColorValue);
    public IBrush NoiseTitleColorBrush => new SolidColorBrush(NoiseTitleColorValue);
    public string NoiseTitleColorHex => ToHexRgb(NoiseTitleColorValue);

    /// <summary>降水提醒色：预览里的降雨行与设置页色块都用它。</summary>
    public IBrush RainColorBrush => new SolidColorBrush(RainColorValue);
    public string RainColorHex => ToHexRgb(RainColorValue);

    /// <summary>Color → #RRGGBB（去掉 alpha，设置界面显示友好）。</summary>
    private static string ToHexRgb(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";
}
