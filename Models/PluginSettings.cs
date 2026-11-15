using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using ClassIsland.Core.Helpers;

namespace LargeScreenClockRevived.Models;

public class PluginSettings : INotifyPropertyChanged
{
    private ObservableCollection<string> _targetCourseNames = new() { "晚自习" };
    private double _decibelQuietThreshold = 0.005;
    private double _decibelGoodThreshold = 0.015;
    private double _decibelNormalThreshold = 0.04;
    private double _decibelNoisyThreshold = 0.08;
    private string _backgroundColor = "#000000";
    private string _fontColor = "#ffffff";
    private string _progressColor = "#4FC3F7";
    private string _courseInfoColor = "#88ccff";
    private string _noiseTitleColor = "#bbffffff";
    private string _rainColor = "#7FD4FF";
    private bool _showDecibelMeter = true;
    private bool _showCourseInfo = true;
    private bool _showBellTime = true;
    private bool _showNoisyCounter = true;
    private bool _skipFirst3Min = true;
    private int _skipFirstMinutes = 3;
    private double _noisySustainSeconds = 1.0;
    private double _fallWindowSeconds = 5.0;
    private double _samplingIntervalSeconds = 0.2;
    private bool _enableNoiseDebugLog;
    private int _logRetentionDays = 3;
    private bool _logKeepByDuration = true;
    private int _logKeepMinutes = 10;
    private int _logKeepMinKb = 100;
    private int _clockFontSize = 180;
    private double _clockPositionPercent = 30;      // 时钟中心在主体区高度的 30% 处
    private double _volumePositionPercent = 80;     // 音量条中心在主体区高度的 80% 处
    // 非时钟/音量组件的默认字号统一到同一档：课程信息与吵闹计数都是 24，
    // 与 ClockSurface 里 ComponentFontSize 资源保持一致（日期/天气/提醒也用它）。
    private int _courseInfoFontSize = 24;
    private int _countFontSize = 24;
    private int _themeMode;
    private int _appliedPaletteVariant = ClockTheme.VariantUnknown;
    private bool _backgroundEnabled = false;
    private bool _backgroundSectionExpanded;
    private int _backgroundKind = (int)LargeScreenClockRevived.Models.BackgroundKind.Wallpaper;
    private int _backgroundThemeMode;
    private string _backgroundImageFile = "";
    private int _backgroundImageStretch;
    // 默认就把背景压暗 30%：花壁纸上白字才读得清（负值=压暗，见 BackgroundDim）
    private double _backgroundDim = -30;
    private double _wallpaperZoom = 1.15;
    private double _wallpaperBlurRadius = 24;
    private double _animationSpeed = 1.0;
    private string _windowTitle = "大屏时钟";
    private bool _showReminderPanel = true;
    private bool _showWeatherReminder = true;
    private bool _showAlertsReminder = true;
    private bool _showCountdownReminder = true;
    private bool _showTextReminder = true;
    private bool _showRainReminder = true;
    private bool _showEmojiSubtitles = true;
    private bool _showTrayMenuEntry = true;
    private bool _pauseWhenMinimized = true;
    private int _minimizePauseMinutes = 5;
    private bool _showCameraReminder = true;
    private double _cameraAlertFontSize = MinCameraAlertFontSize;
    private bool _showEarthquakeReminder = true;
    private bool _cameraListFilled;
    private ObservableCollection<string> _monitoredCameras = new();

    /// <summary>
    /// 目标课程的科目 Id（= CI 课表里 Subjects 字典的键）。与 <see cref="TargetCourseNames"/>
    /// 一一对应存一份：同名但绑定了不同科目的课，只靠名字区分不开，必须靠这个 Id。
    /// 老配置没有这一项时按名字匹配（向后兼容）。
    /// </summary>
    private ObservableCollection<string> _targetCourseIds = new();

    public PluginSettings()
    {
        _targetCourseNames.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TargetCourseNames));
        _targetCourseIds.CollectionChanged += (_, _) => OnPropertyChanged(nameof(TargetCourseIds));
        _monitoredCameras.CollectionChanged += (_, _) => OnPropertyChanged(nameof(MonitoredCameras));
        _imageInstances.CollectionChanged += OnImageInstancesChanged;
    }

    /// <summary>
    /// 触发全屏时钟的课程名称列表（精确匹配：名称完全相同才触发，见 <see cref="CourseMatch"/>）。
    /// 只是给人看的名字；真正的身份以 <see cref="TargetCourseIds"/> 为准。
    /// </summary>
    public ObservableCollection<string> TargetCourseNames
    {
        get => _targetCourseNames;
        set { _targetCourseNames = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 触发全屏时钟的科目 Id 列表（CI 的 Guid）。同名课程靠它区分。
    /// </summary>
    public ObservableCollection<string> TargetCourseIds
    {
        get => _targetCourseIds;
        set { _targetCourseIds = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 安静阈值 (RMS)，低于此值为"安静"
    /// </summary>
    public double DecibelQuietThreshold
    {
        get => _decibelQuietThreshold;
        set { _decibelQuietThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 良好阈值 (RMS)，低于此值为"良好"
    /// </summary>
    public double DecibelGoodThreshold
    {
        get => _decibelGoodThreshold;
        set { _decibelGoodThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 一般阈值 (RMS)，低于此值为"一般"
    /// </summary>
    public double DecibelNormalThreshold
    {
        get => _decibelNormalThreshold;
        set { _decibelNormalThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 吵闹阈值 (RMS)，高于此值为"吵闹"，以上为"嘈杂"
    /// </summary>
    public double DecibelNoisyThreshold
    {
        get => _decibelNoisyThreshold;
        set { _decibelNoisyThreshold = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 背景颜色 (HEX)
    /// </summary>
    public string BackgroundColor
    {
        get => _backgroundColor;
        set { _backgroundColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 文字颜色 (HEX)
    /// </summary>
    public string FontColor
    {
        get => _fontColor;
        set { _fontColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 主题：0 = 跟随 ClassIsland，1 = 明亮，2 = 黑暗，3 = 自动（按背景图明暗，见 <see cref="ClockThemeMode"/>）。
    /// 切换主题会把颜色换成该主题那套（见 <see cref="LightColors"/> / <see cref="DarkColors"/>）。
    /// </summary>
    public int ThemeMode
    {
        get => _themeMode;
        set { _themeMode = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 上面 6 个颜色当前是哪个主题那一套（<see cref="ClockTheme.VariantLight"/> / <see cref="ClockTheme.VariantDark"/>）。
    /// 用来判断「生效主题变了没有」：只有变了才换色，否则用户手改的颜色会被反复覆盖。
    /// 初值取「未知」——老配置里没有这个字段，正好用它认出「还没进过主题系统」的配置来做升级迁移
    /// （见 <see cref="LargeScreenClockRevived.Helpers.ClockThemeApplier.MigrateLegacySettings"/>）。
    /// </summary>
    public int AppliedPaletteVariant
    {
        get => _appliedPaletteVariant;
        set { _appliedPaletteVariant = value; OnPropertyChanged(); }
    }

    // ===== 背景图片与背景类型 =====
    // 选中的图片会被复制一份到插件配置目录，这里只记副本的文件名（原图被挪走/删掉也不影响）。
    // 背景层由 BackgroundKind 决定（壁纸 / 纯色 / 自定义图片），三种都照常铺底。

    /// <summary>
    /// 「背景说了算」开关：开着时主题由 <see cref="BackgroundThemeMode"/> 决定（按背景明暗自动/固定亮暗），
    /// 关着时仍由外观里那项 <see cref="ThemeMode"/> 决定。
    ///
    /// 注意背景层本身（壁纸/纯色/自定义图）不受它影响 —— 背景类型改由 <see cref="BackgroundKind"/> 决定，
    /// 三种类型都会照常铺底。这个标记只用于「主题跟谁走」这一件事，设置页里不再单独露出；
    /// 默认关（主题仍由外观里的「主题」那一项决定），老配置里若它本来是开着的则维持原语义。
    /// </summary>
    public bool BackgroundEnabled
    {
        get => _backgroundEnabled;
        set { _backgroundEnabled = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 设置页「背景设置」那一组展开着没有。
    /// 纯粹是界面状态，但不存下来的话，每次重开 CI、或者跳到别的设置页再回来，
    /// 它都会自己折回去，调个参数还得重新点开一次。
    /// </summary>
    public bool BackgroundSectionExpanded
    {
        get => _backgroundSectionExpanded;
        set { _backgroundSectionExpanded = value; OnPropertyChanged(); }
    }

    private bool _adjustingLayout;

    /// <summary>
    /// 设置页正在拖「位置 / 字号」这类连着改布局的滑块（松手 300 毫秒后自动归假）。
    ///
    /// 拖动期间大屏与预览把背景的「明暗遮罩 + 过渡」暂时收一收（见 <see cref="Views.BackgroundLayer"/>），
    /// 免得每帧重算整屏背景。纯界面状态，不存盘；<see cref="Plugin.Initialize"/> 里另外兜底归零一次。
    /// </summary>
    [JsonIgnore]
    public bool AdjustingLayout
    {
        get => _adjustingLayout;
        set
        {
            if (_adjustingLayout == value) return;
            _adjustingLayout = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 背景主题档位（0 自动 / 1 暗底白字 / 2 亮底黑字，见 <see cref="BackgroundThemeMode"/>）。
    /// 只在「背景」开关打开时生效，此时 <see cref="ThemeMode"/> 整项失效。
    /// </summary>
    public int BackgroundThemeMode
    {
        get => _backgroundThemeMode;
        set { _backgroundThemeMode = Math.Clamp(value, 0, 2); OnPropertyChanged(); }
    }

    /// <summary>
    /// 背景图片文件名（相对插件配置目录）。空 = 仍用纯色底。
    /// </summary>
    public string BackgroundImageFile
    {
        get => _backgroundImageFile;
        set { _backgroundImageFile = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>
    /// 背景图片填充方式：0 = 铺满裁切，1 = 完整显示（留白），2 = 拉伸。
    /// </summary>
    public int BackgroundImageStretch
    {
        get => _backgroundImageStretch;
        set { _backgroundImageStretch = Math.Clamp(value, 0, 2); OnPropertyChanged(); }
    }

    // ===== 「自定义图片」组件 =====

    /// <summary>
    /// 自定义图片组件要显示的图片文件名（相对插件配置目录，由 <see cref="ComponentImageStore"/> 存放）。
    /// 空 = 还没选图，这个组件在大屏上不显示。
    /// </summary>
    public string CustomImageFile
    {
        get => _customImageFile;
        set { _customImageFile = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>
    /// 自定义图片的填充方式：0 = 完整显示（等比，默认），1 = 铺满裁切（等比填满），2 = 拉伸（填满且可能变形）。
    /// </summary>
    public int CustomImageStretch
    {
        get => _customImageStretch;
        set { _customImageStretch = Math.Clamp(value, 0, 2); OnPropertyChanged(); }
    }

    private string _customImageFile = "";
    private int _customImageStretch;

    // ===== 贴纸（自定义图片组件的多实例）=====

    private ObservableCollection<ImageInstance> _imageInstances = new();

    /// <summary>
    /// 贴纸实例列表：自定义图片组件从 1.4.0.0 起支持多个实例，一个实例对应一张图片。
    /// 每个实例在 <see cref="ComponentLayouts"/> 里有自己的一份布局（键见 <see cref="ImageInstance.ComponentId"/>）。
    /// </summary>
    public ObservableCollection<ImageInstance> ImageInstances
    {
        get => _imageInstances;
        set
        {
            if (_imageInstances is not null)
            {
                _imageInstances.CollectionChanged -= OnImageInstancesChanged;
                // 反序列化/整体换列表时旧实例也要退订，否则旧列表会一直挂在事件上
                foreach (var instance in _imageInstances)
                    instance.PropertyChanged -= OnImageInstancePropertyChanged;
            }

            _imageInstances = value ?? new ObservableCollection<ImageInstance>();

            // 从 Settings.json 读出来的实例不会再走 CollectionChanged，必须在这里补订阅：
            // 否则改 File/Stretch 既不会通知 UI，也不会触发防抖存盘。
            foreach (var instance in _imageInstances)
                instance.PropertyChanged += OnImageInstancePropertyChanged;

            _imageInstances.CollectionChanged += OnImageInstancesChanged;
            OnPropertyChanged();
        }
    }

    private void OnImageInstancesChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
            foreach (ImageInstance instance in e.NewItems)
                instance.PropertyChanged += OnImageInstancePropertyChanged;
        if (e.OldItems is not null)
            foreach (ImageInstance instance in e.OldItems)
                instance.PropertyChanged -= OnImageInstancePropertyChanged;

        OnPropertyChanged(nameof(ImageInstances));
    }

    private void OnImageInstancePropertyChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(ImageInstances));

    /// <summary>
    /// 「贴纸实例」迁移版本：0 = 还没迁移过。老配置里只有一张 <see cref="CustomImageFile"/>，
    /// 升级上来后包装成第一个贴纸实例，外观不变；迁移完置 1。
    /// </summary>
    public int StickerMigrationVersion
    {
        get => _stickerMigrationVersion;
        set { _stickerMigrationVersion = value; OnPropertyChanged(); }
    }

    private int _stickerMigrationVersion;

    /// <summary>
    /// 补齐贴纸列表：老的单图配置包装成第一个实例；顺手补上每个实例的组件布局。
    /// 启动时调一次（见 <see cref="Plugin.Initialize"/>），设置页打开时也会兜一次。
    /// </summary>
    public void EnsureImageInstances()
    {
        if (ImageInstances is null) ImageInstances = new ObservableCollection<ImageInstance>();

        if (StickerMigrationVersion < 1)
        {
            if (ImageInstances.Count == 0 && !string.IsNullOrWhiteSpace(CustomImageFile))
            {
                var migrated = new ImageInstance
                {
                    File = CustomImageFile,
                    Stretch = CustomImageStretch,
                };
                ImageInstances.Add(migrated);

                // 迁移完就把老的单图字段清空：不清的话用户删光贴纸后，
                // ClockSurface 的「实例为空 → 回退旧单图」分支会把那张图又变出来。
                CustomImageFile = "";
                CustomImageStretch = 0;
            }

            StickerMigrationVersion = 1;
        }

        foreach (var instance in ImageInstances)
            GetComponentLayout(instance.ComponentId);
    }

    /// <summary>新建一个贴纸实例（空图，等用户在设置页里选）；返回实例本身。</summary>
    public ImageInstance AddImageInstance(string? file = null, int stretch = 0)
    {
        var instance = new ImageInstance { File = file ?? "", Stretch = stretch };

        // id 是布局字典的键：重复会让两张贴纸共用一份布局（手改 Settings.json 才会撞）。
        // 新建时主动避开已有 id，撞了就换一个。
        var used = new HashSet<string>(ImageInstances.Select(i => i.Id), StringComparer.Ordinal);
        while (used.Contains(instance.Id)) instance.Id = Guid.NewGuid().ToString("N")[..8];

        ImageInstances.Add(instance);
        GetComponentLayout(instance.ComponentId);
        return instance;
    }

    /// <summary>删掉一个贴纸实例：连同它的布局一起摘掉。</summary>
    public void RemoveImageInstance(ImageInstance? instance)
    {
        if (instance is null) return;
        ImageInstances.Remove(instance);
        ComponentLayouts.Remove(instance.ComponentId);
    }

    // ===== 1.4.0.0：网页背景 / 原生模糊 / 主题色跟随 =====

    private string _backgroundWebUrl = "https://";

    /// <summary>
    /// 网页背景地址（只在 <see cref="BackgroundKind"/> = <see cref="Models.BackgroundKind.WebPage"/> 时用）。
    /// 默认留一个 https:// 前缀，方便用户直接粘贴域名。
    /// </summary>
    public string BackgroundWebUrl
    {
        get => _backgroundWebUrl;
        set { _backgroundWebUrl = value ?? ""; OnPropertyChanged(); }
    }

    private bool _followClassIslandAccent = true;

    /// <summary>
    /// 主题色是否跟随 ClassIsland（单向读取：只把 CI 的强调色抄过来，不回写 CI）。
    /// 默认开；CI 拿不到强调色时回退到本插件自己的默认主题色（见 <see cref="ThemeAccentColor"/>）。
    /// </summary>
    public bool FollowClassIslandAccent
    {
        get => _followClassIslandAccent;
        set { _followClassIslandAccent = value; OnPropertyChanged(); }
    }

    private string _themeAccentColor = "";

    /// <summary>
    /// 当前生效的强调色（HEX，空 = 用主题自带的那套）。跟随 CI 时由
    /// <c>Helpers.CiThemeReader</c> 在启动与主题切换时写入；关掉跟随后可由用户自己调。
    /// </summary>
    public string ThemeAccentColor
    {
        get => _themeAccentColor;
        set { _themeAccentColor = value ?? ""; OnPropertyChanged(); }
    }

    /// <summary>
    /// 设置页预览的渲染倍率：预览按「最终输出分辨率的 40%」渲染（只影响预览，不影响大屏输出质量）。
    /// 放在这里当常量，设置页与预览宿主共用同一个数。
    /// </summary>
    [JsonIgnore]
    public const double PreviewRenderScale = 0.4;

    /// <summary>
    /// 背景明暗遮罩：负值压暗、正值提亮（-100 ~ 100），0 = 不改。
    /// 花壁纸上文字读不清时用它压一压。
    /// </summary>
    public double BackgroundDim
    {
        get => _backgroundDim;
        set { _backgroundDim = Math.Clamp(value, -100, 100); OnPropertyChanged(); }
    }

    /// <summary>
    /// 背景类型：0 = 提取当前壁纸，1 = 纯色背景，2 = 自定义图片，3 = 原生实时模糊，4 = 网页（见 <see cref="BackgroundKind"/>）。
    /// 默认「提取当前壁纸」——开箱就是桌面壁纸做底，不用先去设置里挑一张。
    /// </summary>
    public int BackgroundKind
    {
        get => _backgroundKind;
        set { _backgroundKind = Math.Clamp(value, 0, 4); OnPropertyChanged(); }
    }

    /// <summary>
    /// 壁纸「景深放大」倍率（1.0 ~ 1.6，默认 1.15）。
    /// 壁纸比屏幕大一圈、再多糊一层，画面才有「镜头在墙外一层」的纵深；1.0 = 不放大。
    /// 只对「提取当前壁纸」生效。
    /// </summary>
    public double WallpaperZoom
    {
        get => _wallpaperZoom;
        set { _wallpaperZoom = Math.Clamp(value, 1.0, 1.6); OnPropertyChanged(); }
    }

    /// <summary>
    /// 壁纸模糊半径（0 ~ 64 屏幕像素，默认 24）。数值按 1080p 视觉调，4K 上按比例放大。
    /// 只对「提取当前壁纸」生效。
    /// </summary>
    public double WallpaperBlurRadius
    {
        get => _wallpaperBlurRadius;
        set { _wallpaperBlurRadius = Math.Clamp(value, 0, 64); OnPropertyChanged(); }
    }

    /// <summary>
    /// 开屏/退场的动画速率（倍率，0.5~2.0，越大越快，1.0 = 默认时长）。
    /// 影响：开屏压暗截图的停留、背景交叉淡入、壁纸推近、组件淡入、退出时的淡出。
    /// </summary>
    public double AnimationSpeed
    {
        get => _animationSpeed;
        set { _animationSpeed = Math.Clamp(value, 0.5, 2.0); OnPropertyChanged(); }
    }

    /// <summary>
    /// 课程进度条颜色 (HEX)。已进行部分用此色（不透明），未进行部分用半透明派生。
    /// </summary>
    public string ProgressColor
    {
        get => _progressColor;
        set { _progressColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 当前课程信息文字颜色 (HEX)。
    /// </summary>
    public string CourseInfoColor
    {
        get => _courseInfoColor;
        set { _courseInfoColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 教室音量条标题文字颜色 (HEX)。
    /// </summary>
    public string NoiseTitleColor
    {
        get => _noiseTitleColor;
        set { _noiseTitleColor = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 降水提醒颜色 (HEX)：有降雨提醒、或当前天气是雨雪时，相应文字与图标用这个色（默认蓝）。
    /// </summary>
    public string RainColor
    {
        get => _rainColor;
        set { _rainColor = value; OnPropertyChanged(); }
    }

    // ===== 两套主题各记一套颜色 =====
    // 切主题时上面这 6 个颜色会被换掉，所以换之前先把当前这套存回它自己的槽位、再读另一套
    // （见 ClockThemeApplier），这样切回来还是你调过的色，而不是又被打回该主题默认值。

    // 两套槽位是「随手存一下」的配色快照，但 ClockThemeApplier 会直接改它们的字段。
    // 早先它们是普通自动属性：改了不通知、也不触发 400ms 防抖存盘，只靠同一流程里顺带改的
    // 6 个颜色属性“搭便车”落盘——一旦哪天只改槽位不改颜色，用户的调色就会静默丢失。
    // 这里改成带通知的属性，让槽位自己也能触发保存。

    private ThemeColorSet _lightColors = ThemeColorSet.FromPalette(ClockTheme.Light);
    private ThemeColorSet _darkColors = ThemeColorSet.FromPalette(ClockTheme.Dark);

    /// <summary>明亮主题那一套颜色（用户调过就记在这里，切回明亮时恢复）。</summary>
    public ThemeColorSet LightColors
    {
        get => _lightColors;
        set { _lightColors = value ?? ThemeColorSet.FromPalette(ClockTheme.Light); OnPropertyChanged(); }
    }

    /// <summary>黑暗主题那一套颜色。</summary>
    public ThemeColorSet DarkColors
    {
        get => _darkColors;
        set { _darkColors = value ?? ThemeColorSet.FromPalette(ClockTheme.Dark); OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否显示教室分贝
    /// </summary>
    public bool ShowDecibelMeter
    {
        get => _showDecibelMeter;
        set { _showDecibelMeter = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否显示课程信息
    /// </summary>
    public bool ShowCourseInfo
    {
        get => _showCourseInfo;
        set { _showCourseInfo = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 课程信息里是否附带「实际打铃」时间（课表时间加时间偏移后的真实时间）。
    /// 关掉则只显示课表时间那一行，字短些。
    /// </summary>
    public bool ShowBellTime
    {
        get => _showBellTime;
        set { _showBellTime = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否显示吵闹计数
    /// </summary>
    public bool ShowNoisyCounter
    {
        get => _showNoisyCounter;
        set { _showNoisyCounter = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 上课前 3 分钟是否不计数
    /// </summary>
    public bool SkipFirst3Min
    {
        get => _skipFirst3Min;
        set { _skipFirst3Min = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 上课初期保护时长（分钟）
    /// </summary>
    public int SkipFirstMinutes
    {
        get => _skipFirstMinutes;
        set { _skipFirstMinutes = Math.Clamp(value, 0, 10); OnPropertyChanged(); }
    }

    /// <summary>
    /// 起算底线（秒）：一段噪音内「有效时长」（处于一般/吵闹的累计时间）达到该值，
    /// 段结束才记一次；不足的短段（喷嚏/咳嗽）丢弃。默认 1 秒。
    /// </summary>
    public double NoisySustainSeconds
    {
        get => _noisySustainSeconds;
        set { _noisySustainSeconds = Math.Clamp(value, 0.5, 5.0); OnPropertyChanged(); }
    }

    /// <summary>
    /// 回落窗口（秒）：音量掉到安静/良好后，回落不超过该值则与前面并成一段（短暂停顿不断段）；
    /// 超过该值段才结束、记一次。默认 5 秒，范围 0~15。
    /// </summary>
    public double FallWindowSeconds
    {
        get => _fallWindowSeconds;
        set { _fallWindowSeconds = Math.Clamp(value, 0, 15); OnPropertyChanged(); }
    }

    /// <summary>
    /// 音量采样间隔（秒）：麦克风每采集这么长一段音频才算一次读数（也是「记录阈值」的频率）。
    /// 间隔越大采样越稀——越省性能、越不拖日志和大屏时钟，但读数越不跟手；条靠动画补平滑。
    /// 默认 0.2 秒（每秒采样 5 次）。范围 0.1~1.0 秒。
    /// </summary>
    public double SamplingIntervalSeconds
    {
        get => _samplingIntervalSeconds;
        set { _samplingIntervalSeconds = Math.Clamp(value, 0.1, 1.0); OnPropertyChanged(); }
    }

    /// <summary>
    /// 是否输出噪音调试日志（NoiseDebugLog.csv），用于收集真实数据校准阈值
    /// </summary>
    public bool EnableNoiseDebugLog
    {
        get => _enableNoiseDebugLog;
        set { _enableNoiseDebugLog = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 调试日志保留天数：超过该天数的旧日志自动清理。默认 3 天，范围 1~5。
    /// </summary>
    public int LogRetentionDays
    {
        get => _logRetentionDays;
        set { _logRetentionDays = Math.Clamp(value, 1, 5); OnPropertyChanged(); }
    }

    /// <summary>
    /// 调试日志的保留规则：true = 按记录时长，false = 按文件大小。
    /// 打开开关后又进进出出大屏时钟会留下一堆几秒钟的废记录，两种规则都是为了把它们删掉。
    /// </summary>
    public bool LogKeepByDuration
    {
        get => _logKeepByDuration;
        set { _logKeepByDuration = value; OnPropertyChanged(); }
    }

    /// <summary>按记录时长保留（分钟）：记录跨度不足该值的日志文件在停止记录时删除。默认 10 分钟，范围 1~120。</summary>
    public int LogKeepMinutes
    {
        get => _logKeepMinutes;
        set { _logKeepMinutes = Math.Clamp(value, 1, 120); OnPropertyChanged(); }
    }

    /// <summary>按文件大小保留（KB）：小于该值的日志文件在停止记录时删除。默认 100KB，范围 10~3072。</summary>
    public int LogKeepMinKb
    {
        get => _logKeepMinKb;
        set { _logKeepMinKb = Math.Clamp(value, 10, 3072); OnPropertyChanged(); }
    }

    /// <summary>
    /// 时钟字号 (px)
    /// </summary>
    public int ClockFontSize
    {
        get => _clockFontSize;
        set { _clockFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 课程信息字号 (px)：底部居中的「当前课程为 …」那行
    /// </summary>
    public int CourseInfoFontSize
    {
        get => _courseInfoFontSize;
        set { _courseInfoFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 计数字号 (px)：左下角吵闹计数与「正在记录」提示
    /// </summary>
    public int CountFontSize
    {
        get => _countFontSize;
        set { _countFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 时钟垂直位置：时钟中心落在主体区（去掉顶部提醒行与底部各行后剩下的那块）高度的百分之几。
    /// 0 = 最上，100 = 最下；实际按该块的实测高度做了钳制，滑块拉到两端也不会跑到屏幕外。
    /// </summary>
    public double ClockPositionPercent
    {
        get => _clockPositionPercent;
        set { _clockPositionPercent = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    /// <summary>
    /// 音量条垂直位置：同 <see cref="ClockPositionPercent"/>，音量条中心在主体区高度的百分之几。
    /// 与时钟各调各的，互不牵连。
    /// </summary>
    public double VolumePositionPercent
    {
        get => _volumePositionPercent;
        set { _volumePositionPercent = Math.Clamp(value, 0, 100); OnPropertyChanged(); }
    }

    /// <summary>
    /// 窗口标题
    /// </summary>
    public string WindowTitle
    {
        get => _windowTitle;
        set { _windowTitle = value; OnPropertyChanged(); }
    }

    /// <summary>是否显示左上角「提醒」面板（总开关；面板同时要求至少一个子项有内容）。</summary>
    public bool ShowReminderPanel
    {
        get => _showReminderPanel;
        set { _showReminderPanel = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示天气（跟随 CI 的天气缓存，不用 CI 的实时预报判断）。</summary>
    public bool ShowWeatherReminder
    {
        get => _showWeatherReminder;
        set { _showWeatherReminder = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示天气预警（区级统一发布，跟随 CI 的预警缓存）。</summary>
    public bool ShowAlertsReminder
    {
        get => _showAlertsReminder;
        set { _showAlertsReminder = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示倒计时（跟随 CI 主界面组件里的倒计时组件）。</summary>
    public bool ShowCountdownReminder
    {
        get => _showCountdownReminder;
        set { _showCountdownReminder = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示文本框（跟随 CI 主界面组件里的文本组件）。</summary>
    public bool ShowTextReminder
    {
        get => _showTextReminder;
        set { _showTextReminder = value; OnPropertyChanged(); }
    }

    /// <summary>提醒面板中是否显示「多久下雨」降雨提醒（跟随 CI 的分钟级/逐小时降水数据）。</summary>
    public bool ShowRainReminder
    {
        get => _showRainReminder;
        set { _showRainReminder = value; OnPropertyChanged(); }
    }

    /// <summary>是否显示颜文字副标题（气温旁「记得防晒 (っ'ω')ﾉ」、日期旁「别熬啦 (｡-_-｡)」等趣味提醒；关闭则只留正文字幕）。</summary>
    public bool ShowEmojiSubtitles
    {
        get => _showEmojiSubtitles;
        set { _showEmojiSubtitles = value; OnPropertyChanged(); }
    }

    /// <summary>是否在 CI 托盘的右键菜单里显示「进入大屏时钟」入口（关闭则从菜单移除）。</summary>
    public bool ShowTrayMenuEntry
    {
        get => _showTrayMenuEntry;
        set { _showTrayMenuEntry = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 大屏时钟最小化超过 <see cref="MinimizePauseMinutes"/> 分钟后是否暂停运行
    /// （停计时、音量检测、摄像头检测、防休眠与课程事件）。暂停不关窗口，任务栏入口保留，点回即恢复。
    /// </summary>
    public bool PauseWhenMinimized
    {
        get => _pauseWhenMinimized;
        set { _pauseWhenMinimized = value; OnPropertyChanged(); }
    }

    /// <summary>最小化多久后暂停（分钟，1~30）。</summary>
    public int MinimizePauseMinutes
    {
        get => _minimizePauseMinutes;
        set { _minimizePauseMinutes = Math.Clamp(value, 1, 30); OnPropertyChanged(); }
    }

    /// <summary>是否显示「摄像头使用中」提醒（设备被程序占用时，在预警那一行出一条）。</summary>
    public bool ShowCameraReminder
    {
        get => _showCameraReminder;
        set { _showCameraReminder = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 「摄像头使用中」那一条的字号。可调范围就是「默认字号 ~ 天气提醒的字号」——
    /// 比默认小会更看不清，比天气还大就压过其它提醒了。
    /// </summary>
    public double CameraAlertFontSize
    {
        get => _cameraAlertFontSize;
        set
        {
            _cameraAlertFontSize = Math.Clamp(value, MinCameraAlertFontSize, MaxCameraAlertFontSize);
            OnPropertyChanged();
        }
    }

    /// <summary>摄像头提醒字号下限：不设这个设置项时那条的字号。</summary>
    public const double MinCameraAlertFontSize = AlertInfo.DefaultFontSize;

    /// <summary>摄像头提醒字号上限：顶部天气温度提醒的字号。</summary>
    public const double MaxCameraAlertFontSize = 19;

    /// <summary>
    /// 是否显示地震提醒（读「地震预警」插件的实时状态，在时钟正上方弹出，见 <see cref="Services.EarthquakeReader"/>）。
    /// 该插件没装 / 没启用时这一项不起作用。
    /// </summary>
    public bool ShowEarthquakeReminder
    {
        get => _showEarthquakeReminder;
        set { _showEarthquakeReminder = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 要提醒的摄像头设备名（设置页里勾的，见 <see cref="CameraAlertBuilder"/>）。空 = 不提醒。
    /// </summary>
    public ObservableCollection<string> MonitoredCameras
    {
        get => _monitoredCameras;
        set { _monitoredCameras = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 设备列表是否已经按本机摄像头「填过默认勾选」。首次打开设置页时把枚举到的设备全部勾上，
    /// 之后用户取消全部勾选就真的不提醒了（不再把空列表当成「还没选过」）。
    /// </summary>
    public bool CameraListFilled
    {
        get => _cameraListFilled;
        set { _cameraListFilled = value; OnPropertyChanged(); }
    }

    // ===== 组件（逐元素的大小 / 位置 / 外框 / 底色）=====
    // 每个组件一份 ComponentLayout，按稳定 id（见 ComponentCatalog）存在字典里。
    // 字典里没有的 id 说明是老配置或者从没调过，GetComponentLayout 会补一份默认值出来，
    // 所以老用户升级上来外观一个像素都不变，而设置页永远有东西可绑。

    private Dictionary<string, ComponentLayout> _componentLayouts = new();

    /// <summary>
    /// 各组件的外观与位置微调（id → <see cref="ComponentLayout"/>）。
    /// 存进 Settings.json；缺项按默认值处理（见 <see cref="GetComponentLayout"/>）。
    /// </summary>
    public Dictionary<string, ComponentLayout> ComponentLayouts
    {
        get => _componentLayouts;
        set
        {
            // 反序列化/换字典时先退订旧的那批，避免把整张旧字典一直挂在事件上
            foreach (var layout in _componentLayouts.Values) UnsubscribeComponentLayout(layout);

            _componentLayouts = value ?? new Dictionary<string, ComponentLayout>();
            foreach (var layout in _componentLayouts.Values) SubscribeComponentLayout(layout);
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// 取某个组件的布局设置；没有就按默认值补一份（补出来的这份会跟着下次存盘一起写进 Settings.json）。
    /// 监听具体某一项改动由使用方自己挂（见 Views/ComponentHost.cs）；这里的订阅只负责把改动冒泡成
    /// Settings 的变更，好让「防抖存盘」把它写进 Settings.json。
    /// </summary>
    public ComponentLayout GetComponentLayout(string id)
    {
        if (_componentLayouts.TryGetValue(id, out var layout) && layout is not null)
            return layout;

        // 新补出来的布局按清单里的默认启用状态走：天气/日期/倒计时默认隐藏（与 CI 主界面重合），
        // 其余组件默认显示。老配置里已有的布局不会被这里覆盖。
        var descriptor = ComponentCatalog.Find(id);
        layout = new ComponentLayout { Enabled = descriptor?.DefaultEnabled ?? true };
        _componentLayouts[id] = layout;
        SubscribeComponentLayout(layout);
        return layout;
    }

    /// <summary>把清单里所有组件的布局设置都补出来（插件启动时调一次，旧配置升级后字典也是全的）。</summary>
    public void EnsureComponentLayouts()
    {
        foreach (var descriptor in ComponentCatalog.All)
            GetComponentLayout(descriptor.Id);

        // 贴纸实例的布局键是动态的，跟着实例列表补
        EnsureImageInstances();
    }

    /// <summary>
    /// 一键重置：把所有公开设置项恢复成出厂默认（组件开关/顺序/大小位置、颜色字号、音量阈值、
    /// 背景与运行设置全在内）。做法是拿一份全新的 <see cref="PluginSettings"/> 当模板逐项拷贝，
    /// 这样默认值只写在字段声明处，以后新增设置项不用再来改这里。
    ///
    /// 不重置的三项：<see cref="AdjustingLayout"/>（拖动中的运行时状态）、
    /// <see cref="AppliedPaletteVariant"/>（主题跟随状态，不是用户设置）、
    /// <see cref="ComponentLayouts"/>（字典要整体换成默认实例，单独处理）；
    /// 两个迁移版本号也不重置，否则下一次启动会把已经套过的迁移再跑一遍。
    /// </summary>
    public void ResetAllToDefaults()
    {
        var defaults = new PluginSettings();

        foreach (var property in typeof(PluginSettings).GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            if (!property.CanRead || !property.CanWrite) continue;
            if (property.GetIndexParameters().Length > 0) continue;

            switch (property.Name)
            {
                case nameof(ComponentLayouts):
                case nameof(AdjustingLayout):
                case nameof(AppliedPaletteVariant):
                // 迁移版本不能回 0：否则下次启动会再跑一遍「旧显示开关 → 组件启用」的迁移，
                // 把用户重置后手动关掉的组件又全部打开。
                case nameof(ComponentVisibilityMigrationVersion):
                // 「默认压暗 30%」迁移同理：已经套过一次就不要再动用户重置后的明暗值。
                case nameof(BackgroundDimMigrationVersion):
                // 贴纸迁移同理：重置后贴纸列表已经清空，再跑一次迁移会把它又补回来。
                case nameof(StickerMigrationVersion):
                    continue;
            }

            try
            {
                property.SetValue(this, property.GetValue(defaults));
            }
            catch
            {
                // 单个属性拷贝失败不该让整次重置失败
            }
        }

        // 组件布局换成一套全新的默认实例（setter 会负责退订旧字典、订阅新字典并通知）。
        defaults.EnsureComponentLayouts();
        ComponentLayouts = defaults.ComponentLayouts;
    }

    /// <summary>
    /// 「组件启用」迁移版本：0 = 还没迁移过（旧版把显隐拆成一堆 ShowXxx 开关，组件化后改由
    /// ComponentLayout.Enabled 统一管）。迁移完置 1，之后不再动用户的设置。
    /// </summary>
    public int ComponentVisibilityMigrationVersion
    {
        get => _componentVisibilityMigrationVersion;
        set { _componentVisibilityMigrationVersion = value; OnPropertyChanged(); }
    }

    private int _componentVisibilityMigrationVersion;

    /// <summary>
    /// 「默认压暗 30%」迁移版本：0 = 还没套过。老配置里 <see cref="BackgroundDim"/> 一直是 0
    /// （用户从没碰过明暗滑条），升级上来后自动补成 -30；用户自己调过的非零值一律不动。
    /// 迁移完置 1，之后以用户设置为准。
    /// </summary>
    public int BackgroundDimMigrationVersion
    {
        get => _backgroundDimMigrationVersion;
        set { _backgroundDimMigrationVersion = value; OnPropertyChanged(); }
    }

    private int _backgroundDimMigrationVersion;

    /// <summary>一次性把「从没调过明暗」的老配置补成默认压暗 30%（只补一次，见版本号）。</summary>
    public void MigrateBackgroundDimDefault()
    {
        if (BackgroundDimMigrationVersion >= 1) return;
        if (Math.Abs(BackgroundDim) < 0.01) BackgroundDim = -30;
        BackgroundDimMigrationVersion = 1;
    }

    /// <summary>一次性把旧的显示开关搬进组件的启用开关：原本关掉某个组件的用户，迁移后还是关掉的，
    /// 不会因为「组件默认全开」突然多出一堆东西。搬完把旧开关置真（大屏侧已经不再看它们，
    /// 留着真值只是免得老配置里残留的 false 在别处又冒出来）。
    /// </summary>
    public void MigrateLegacyComponentVisibility()
    {
        if (ComponentVisibilityMigrationVersion >= 1) return;

        void Move(string componentId, bool visible) => GetComponentLayout(componentId).Enabled = visible;

        Move("volume", ShowDecibelMeter);
        Move("course", ShowCourseInfo);
        Move("counter", ShowNoisyCounter);
        Move("reminderPanel", ShowReminderPanel);
        Move("weather", ShowWeatherReminder);
        Move("alertDetail", ShowAlertsReminder);
        Move("countdownMerge", ShowCountdownReminder);
        Move("textMerge", ShowTextReminder);
        Move("rainMerge", ShowRainReminder);
        Move("earthquake", ShowEarthquakeReminder);

        ShowDecibelMeter = true;
        ShowCourseInfo = true;
        ShowNoisyCounter = true;
        ShowReminderPanel = true;
        ShowWeatherReminder = true;
        ShowAlertsReminder = true;
        ShowCountdownReminder = true;
        ShowTextReminder = true;
        ShowRainReminder = true;
        ShowEarthquakeReminder = true;

        ComponentVisibilityMigrationVersion = 1;
    }

    private void SubscribeComponentLayout(ComponentLayout? layout)
    {
        if (layout is null) return;
        layout.PropertyChanged -= OnComponentLayoutPropertyChanged;
        layout.PropertyChanged += OnComponentLayoutPropertyChanged;
    }

    private void UnsubscribeComponentLayout(ComponentLayout? layout)
    {
        if (layout is null) return;
        layout.PropertyChanged -= OnComponentLayoutPropertyChanged;
    }

    /// <summary>
    /// 组件布局也是设置的一部分：冒泡成 <see cref="ComponentLayouts"/> 的变更，
    /// 这样 Plugin 里挂的 400ms 防抖存盘才会把滑块改动写进 Settings.json。
    /// </summary>
    private void OnComponentLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
        => OnPropertyChanged(nameof(ComponentLayouts));

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    /// <summary>
    /// 检查一门课是否命中目标课程。
    /// 优先按科目 Id 匹配（能区分同名但绑定了不同科目的课）；
    /// 老配置没有 Id、或当前拿不到 Id 时，退回按名字精确匹配（<see cref="CourseMatch"/>）。
    /// </summary>
    public bool IsTargetCourse(string? courseName, string? subjectId = null)
    {
        if (!string.IsNullOrWhiteSpace(subjectId) && TargetCourseIds.Count > 0)
            return TargetCourseIds.Contains(subjectId);
        return CourseMatch.IsMatch(courseName, TargetCourseNames);
    }
}
