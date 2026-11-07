using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Media.Transformation;
using Avalonia.Styling;
using Avalonia.Threading;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.ViewModels;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 大屏时钟的全部内容。全屏窗口（<see cref="FullScreenClockWindow"/>）与设置页的预览放的都是这一份，
/// 预览只是把它等比缩小塞进预览框 —— 两边的字号、颜色、位置出自同一份布局，不可能对不上。
///
/// <see cref="IsPreview"/> = true 的那份（设置页里的）只负责「长得一样」：不往设置里写回位置、
/// 不起弹幕定时器、鼠标事件也由外层掐掉，免得预览反过来改动真实状态。
/// </summary>
public partial class ClockSurface : UserControl
{
    /// <summary>
    /// 这一份是设置页里的预览吗？
    /// 预览按真实屏幕尺寸（等比缩放到预览框里的是整个面），所以实测尺寸、行位置与大屏一模一样。
    /// </summary>
    public static readonly StyledProperty<bool> IsPreviewProperty =
        AvaloniaProperty.Register<ClockSurface, bool>(nameof(IsPreview));

    public bool IsPreview
    {
        get => GetValue(IsPreviewProperty);
        set => SetValue(IsPreviewProperty, value);
    }

    /// <summary>
    /// 背景层的圆角。大屏全屏时是 0；设置页的预览是一张圆角卡片，要设成和卡片一样的圆角，
    /// 否则背景图会在卡片四角露出方角（Border 的 ClipToBounds 只裁矩形，不裁圆角）。
    /// </summary>
    public CornerRadius BackdropCornerRadius
    {
        get => BackLayer.CornerRadius;
        set => BackLayer.CornerRadius = value;
    }

    public ClockSurface()
    {
        InitializeComponent();

        RulesPopup.PlacementTarget = CounterAreaBorder; // 规则浮层锚定在计数区域上方
        SizeChanged += (_, _) => UpdateSizeDependentLayout();
        DataContextChanged += (_, _) =>
        {
            UpdateSizeDependentLayout();
            SetupBackdrop();
            UpdatePreviewPlaceholders();
            ApplyComponentVisibility();
            ApplyComponentOrder();
        };

        // 时钟/音量条的上下位置：主体区或两块自身的尺寸一变（换字号、开关注音条、内容行数变化）就重算
        // 地震提醒的「左右绽开」：横向从中间展开，不从下往上冒。
        // 显示与否在外层那格上（整屏正中的覆盖层），卡片本身一直存在，所以盯的是外层。
        EarthquakeAlertBlock.RenderTransformOrigin = RelativePoint.Center;
        EarthquakeAlertLayer.PropertyChanged += OnEarthquakeAlertPropertyChanged;

        BodyArea.SizeChanged += (_, _) => ApplyBodyPositions();
        ClockText.SizeChanged += (_, _) => ApplyBodyPositions();
        VolumePanel.SizeChanged += (_, _) => ApplyBodyPositions();
        ClockHost.SizeChanged += (_, _) => ApplyBodyPositions();
        VolumeHost.SizeChanged += (_, _) => ApplyBodyPositions();
        Loaded += (_, _) => ApplyBodyPositions();
        Loaded += (_, _) => UpdatePreviewPlaceholders();
        Loaded += (_, _) =>
        {
            ApplyComponentVisibility();
            ApplyComponentOrder();
        };
    }

    // ===== 生命周期：挂上/摘掉那几根「全局事件」的线 =====

    // Plugin.Settings 与 ViewModel 都是长命的单例，挂上去的处理器会让它们反过来攥住这一整棵
    // 视觉树。设置页每打开一次就新建一个 ClockSurface（预览那份），不摘线的话每开一次就漏一整份
    // 控件树 + 背景层的位图，而且之后每次改设置都要多跑一遍 —— 内存一路涨、界面一路钝。
    // 所以挂线只在真正进了视觉树时做，离开时全部摘掉（大屏窗口只是最小化、不脱离树，不受影响）。

    private bool _settingsHooked;
    private FullScreenClockViewModel? _hookedVm;

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        HookSignals();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        UnhookSignals();
    }

    private void HookSignals()
    {
        if (!_settingsHooked && Plugin.Settings is { } settings)
        {
            settings.PropertyChanged += OnPluginSettingsChanged;
            _settingsHooked = true;
        }

        if (_hookedVm is null && DataContext is FullScreenClockViewModel vm)
        {
            vm.PropertyChanged += OnClockViewModelPropertyChanged;
            vm.NoisyDisplaySegments.CollectionChanged += OnNoisySegmentsChanged;
            _hookedVm = vm;
        }
    }

    private void UnhookSignals()
    {
        if (_settingsHooked && Plugin.Settings is { } settings)
            settings.PropertyChanged -= OnPluginSettingsChanged;
        _settingsHooked = false;

        if (_hookedVm is { } vm)
        {
            vm.PropertyChanged -= OnClockViewModelPropertyChanged;
            vm.NoisyDisplaySegments.CollectionChanged -= OnNoisySegmentsChanged;
        }
        _hookedVm = null;
    }

    private void OnNoisySegmentsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdatePreviewPlaceholders();

    private void OnPluginSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 组件页（启用/排序/大小/位置）改的是 ComponentLayout 字典里的对象；
        // PluginSettings 会在任何一份布局变动时冒泡一个 ComponentLayouts 通知，这里接住：
        // 显隐与分区顺序重算，位置再排一遍（大小/位置本身由 ComponentHost 自己跟）。
        if (e.PropertyName is nameof(PluginSettings.ComponentLayouts))
        {
            ApplyComponentVisibility();
            ApplyComponentOrder();
            ApplyBodyPositions();
            return;
        }

        // 设置页拖滑块改的是同一个 PluginSettings 实例 → 立刻重排，不用等重开窗口
        if (e.PropertyName is nameof(PluginSettings.ClockPositionPercent)
            or nameof(PluginSettings.VolumePositionPercent)
            or nameof(PluginSettings.ShowDecibelMeter))
        {
            ApplyBodyPositions();
            UpdatePreviewPlaceholders();
            return;
        }

        // 预览里的假内容跟着这两项开关走；它们不进玻璃，不用重烘
        if (e.PropertyName is nameof(PluginSettings.ShowNoisyCounter)
            or nameof(PluginSettings.ShowCourseInfo))
        {
            UpdatePreviewPlaceholders();
            return;
        }

        OnBackdropSettingsChanged(sender, e);
    }

    // ===== 背景层 =====

    /// <summary>背景层只初始化一次（DataContext 可能来不止一次）。</summary>
    private bool _backdropBound;

    /// <summary>开屏过渡正在演：避免重复调用叠在一起。</summary>
    private bool _introRunning;

    /// <summary>
    /// 接上背景层：套一次静态值，并挂好「设置页改了背景相关项 → 立刻生效」这条线。
    ///
    /// 背景层不参与布局（组件的 Margin / 尺寸一个都没动），ApplyBodyPositions 那套定位与写回不受影响。
    /// </summary>
    private void SetupBackdrop()
    {
        if (DataContext is not FullScreenClockViewModel vm) return;

        if (!_backdropBound)
        {
            _backdropBound = true;

            // 设置页里改背景相关项 → 立刻生效，不用重开窗口。
            // 挂线统一走 HookSignals（进视觉树时挂、离开时摘）。
            HookSignals();
        }

        ApplyButtonHalo();
        ApplyBackdrop();
    }

    /// <summary>
    /// 窗口显示之前调用：先把内容层收起来，避免窗口出现的第一帧把整个界面「闪」出来。
    /// 真正的开屏过渡仍由 <see cref="BeginIntro"/> 驱动；窗口没走过渡（比如预览）时不会被这个影响。
    /// </summary>
    public void PrepareIntro()
    {
        if (IsPreview) return;
        // 关键：上一次开屏结束时给 ContentLayer 挂过淡入用的 Transitions，
        // 如果不清掉，这次把 Opacity 设 0 会变成「从 1 慢慢淡出到 0」——
        // 于是第二次开屏时界面会先整块露一下再消失，正是「只有第一次不闪」的原因。
        ContentLayer.Transitions = null;
        ContentLayer.Opacity = 0;
        // 背景层同样瞬时收掉：否则第二次开屏的第一帧会先露出上一轮的壁纸，再开始过渡。
        BackLayer.PrepareForIntro();
    }

    /// <summary>
    /// 由全屏窗口在「窗口显示之后」显式调用，每次显示都重演一遍开屏过渡。
    /// <paramref name="priorScreenshot"/> 是窗口显示前拍好的当前屏幕（干净、不含大屏自己）。
    /// </summary>
    public void BeginIntro(Bitmap? priorScreenshot)
    {
        _ = PlayIntroAsync(priorScreenshot);
    }

    /// <summary>
    /// 开屏过渡：设置页预览不演。
    /// 背景层负责「截图压暗 → 交叉淡入新背景」，这里负责让整层内容跟着一起淡入。
    /// 动画时长跟着设置里的「动画速率」走。
    /// </summary>
    private async Task PlayIntroAsync(Bitmap? priorScreenshot)
    {
        if (IsPreview || _introRunning) return;
        if (Plugin.Settings is not { } settings)
        {
            // 没有设置就演不了：别把内容层留在隐藏状态，直接显示
            ContentLayer.Transitions = null;
            ContentLayer.Opacity = 1;
            return;
        }

        _introRunning = true;
        try
        {
            var fadeMs = Math.Max(16, (int)Math.Round(520 / Math.Clamp(settings.AnimationSpeed, 0.5, 2.0)));

            // 清掉上一轮的淡入过渡，确保这里的 0 是瞬时的（否则会看到界面淡出而不是直接藏起来）
            ContentLayer.Transitions = null;
            ContentLayer.Opacity = 0;
            await BackLayer.PlayIntroAsync(settings, () =>
            {
                ContentLayer.Transitions = new Transitions
                {
                    new DoubleTransition
                    {
                        Property = Visual.OpacityProperty,
                        Duration = TimeSpan.FromMilliseconds(fadeMs),
                        Easing = new CubicEaseOut(),
                    },
                };
                ContentLayer.Opacity = 1;
            }, priorScreenshot);

            // 兜底：背景过渡被中途取消（窗口又 Hide 了等）时回调可能没跑到，
            // 别让内容层一直停在隐藏状态。
            if (ContentLayer.Opacity < 0.01)
            {
                ContentLayer.Transitions = null;
                ContentLayer.Opacity = 1;
            }
        }
        finally
        {
            _introRunning = false;
        }
    }

    // ===== 组件的显隐与分区内顺序 =====

    /// <summary>
    /// 每个组件最终的显隐 = 「设置里这个组件的启用开关」与「这一刻它该不该出现」（VM 的显示条件）的与。
    /// 两件事都落在外壳 <see cref="ComponentHost"/> 的 IsVisible 上（不能只隐藏内层：StackPanel 的
    /// Spacing 会把零宽的孩子也算进去，关掉一个会留下一条空档）。
    /// </summary>
    private void ApplyComponentVisibility()
    {
        var settings = Plugin.Settings;
        var vm = DataContext as FullScreenClockViewModel;

        void Set(ComponentHost host, Func<FullScreenClockViewModel, bool>? condition)
        {
            var enabled = settings is null || settings.GetComponentLayout(host.ComponentId).Enabled;
            var wanted = condition is null || (vm is not null && condition(vm));
            host.IsVisible = enabled && wanted;
        }

        // 时钟 / 日期 / 吵闹计数没有额外的显隐条件，只看启用开关
        Set(ClockHost, null);
        Set(DateHost, null);
        Set(CounterHost, null);

        Set(WeatherHost, v => v.ShowWeatherInFirstRow);
        Set(RainHost, v => v.ShowRainMergedInFirstRow);
        Set(CountdownHost, v => v.ShowCountdownMergedInFirstRow);
        Set(TextHost, v => v.ShowTextMergedInFirstRow);
        Set(AlertDetailHost, v => v.HasExpandedAlert);
        Set(EarthquakeHost, v => v.ShowEarthquakeAlert);

        // 下面这几个的「开不开」已经收口到组件自己的启用开关（旧配置在启动时迁移成 Enabled），
        // 大屏侧只负责按时隐藏，不再看旧的 ShowXxx 设置。
        Set(VolumeHost, null);
        Set(CourseHost, null);
        Set(ReminderPanelHost, null);
        Set(ProgressHost, null);
    }

    /// <summary>
    /// 同一分区内的先后：Order 小的在前（null 按组件清单顺序）。
    /// 现在只有「顶部第一行」的四块（天气/降雨/倒计时/文本）真的能换位——它们挤在同一个
    /// StackPanel 里，换个顺序就是换位置；预警详情与日期各自钉在中间/右边，主体、底部同理，
    /// 顺序对它们只影响设置页里排在列表第几位。
    /// </summary>
    private void ApplyComponentOrder()
    {
        if (Plugin.Settings is not { } settings) return;

        var ordered = new[] { WeatherHost, RainHost, CountdownHost, TextHost }
            .OrderBy(h => EffectiveOrder(settings, h.ComponentId))
            .ToArray();

        var flow = TopMergeFlow.Children;
        for (var target = 0; target < ordered.Length; target++)
        {
            var current = flow.IndexOf(ordered[target]);
            if (current != target) flow.Move(current, target);
        }

        // 时钟和音量条谁在上也看 Order（在 ApplyBodyPositions 里按它分支）
        ApplyBodyPositions();
    }

    /// <summary>组件的有效位次：设置里排过就用它，没排过按组件清单的顺序。</summary>
    private static int EffectiveOrder(PluginSettings settings, string componentId)
    {
        if (settings.GetComponentLayout(componentId).Order is { } order) return order;

        var all = ComponentCatalog.All;
        for (var i = 0; i < all.Count; i++)
            if (string.Equals(all[i].Id, componentId, StringComparison.Ordinal))
                return i;
        return int.MaxValue;
    }

    private void OnClockViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // 这些属性决定「这个组件这会儿该不该出现」（合并到顶部行/弹幕/提醒面板等），
        // 组件自己的启用开关由 ApplyComponentVisibility 一起算进去。
        if (e.PropertyName is nameof(FullScreenClockViewModel.ShowWeatherInFirstRow)
            or nameof(FullScreenClockViewModel.ShowRainMergedInFirstRow)
            or nameof(FullScreenClockViewModel.ShowCountdownMergedInFirstRow)
            or nameof(FullScreenClockViewModel.ShowTextMergedInFirstRow)
            or nameof(FullScreenClockViewModel.HasExpandedAlert)
            or nameof(FullScreenClockViewModel.ShowDecibelMeter)
            or nameof(FullScreenClockViewModel.ShowCourseInfo)
            or nameof(FullScreenClockViewModel.ShowReminderPanel)
            or nameof(FullScreenClockViewModel.ShowCourseProgress)
            or nameof(FullScreenClockViewModel.ShowEarthquakeAlert))
        {
            ApplyComponentVisibility();
            ApplyBodyPositions();
        }

        // 换主题 / 自动配色下换背景色 → 玻璃的底色、描边、留白色整批跟着变
        if (e.PropertyName is nameof(FullScreenClockViewModel.Chrome)
            or nameof(FullScreenClockViewModel.BackgroundColor))
        {
            ApplyButtonHalo();
            ApplyBackdrop();
        }

        // 预览占位内容跟着这些状态走（上课了就把假内容撤掉）
        if (e.PropertyName is nameof(FullScreenClockViewModel.CourseInfoText)
            or nameof(FullScreenClockViewModel.IsRecordingVisible)
            or nameof(FullScreenClockViewModel.ShowDecibelMeter))
        {
            UpdatePreviewPlaceholders();
        }
    }

    /// <summary>
    /// 设置页预览专供：补一份「假内容」。
    ///
    /// 预览不是另画的小样，而是把大屏这面墙原样缩小 —— 所以没在上课时，课程信息、左下角计数、
    /// 「正在记录」本来就是空的，想调这几项字号，预览里什么也看不到。这里在**预览那一份**里
    /// 补一行示意文字，只在真实内容为空时显示。
    /// 不碰 ViewModel：大屏与预览共用同一个实例，往 VM 里塞假数据会污染真实大屏。
    /// </summary>
    private void UpdatePreviewPlaceholders()
    {
        var vm = DataContext as FullScreenClockViewModel;
        var preview = IsPreview && vm is not null;

        var counterOn = Plugin.Settings?.ShowNoisyCounter ?? true;
        PreviewCounterPlaceholder.IsVisible = preview && counterOn && vm!.NoisyDisplaySegments.Count == 0;
        PreviewCoursePlaceholder.IsVisible = preview && string.IsNullOrWhiteSpace(vm!.CourseInfoText);
        PreviewRecordingPlaceholder.IsVisible = preview && vm!.ShowDecibelMeter && !vm.IsRecordingVisible;
    }

    /// <summary>
    /// 给「最小化 / 退出」两个按钮的字描一圈同色系的柔光。
    ///
    /// 这两个键以前靠一块很实的黑/白底片保证可读，底片一淡就糊、够实又成了一堆亮色里杵一块砖。
    /// 可读性改由这圈柔光扛：深色主题白字配黑晕、明亮主题黑字配白晕，
    /// 底片于是可以只留一点同色系的淡彩，看着是「同一家族里挑出来的一个」，而不是贴上去的黑块。
    /// </summary>
    private void ApplyButtonHalo()
    {
        if (DataContext is not FullScreenClockViewModel vm) return;

        // 两个控件不能共用一个 Effect 实例，各建一份
        foreach (var button in new[] { MinimizeButton, ExitButton })
        {
            if (button.Content is not Control content) continue;
            content.Effect = new DropShadowEffect
            {
                Color = vm.Chrome.IsLight ? Colors.White : Colors.Black,
                OffsetX = 0,
                OffsetY = 0,
                BlurRadius = 4,
                Opacity = 0.9,
            };
        }
    }

    private void OnBackdropSettingsChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginSettings.BackgroundKind)
            or nameof(PluginSettings.BackgroundImageFile)
            or nameof(PluginSettings.BackgroundImageStretch)
            or nameof(PluginSettings.BackgroundDim)
            or nameof(PluginSettings.BackgroundColor)
            or nameof(PluginSettings.WallpaperZoom)
            or nameof(PluginSettings.WallpaperBlurRadius)
            or nameof(PluginSettings.AdjustingLayout)
            or nameof(PluginSettings.BackgroundEnabled))
            ApplyBackdrop();
    }

    /// <summary>把当前的背景设置推给背景层。</summary>
    private void ApplyBackdrop()
    {
        if (DataContext is not FullScreenClockViewModel vm) return;
        if (Plugin.Settings is not { } settings) return;

        BackLayer.Apply(settings, IsPreview);
        ApplyQuakeFrost(vm);
    }

    /// <summary>
    /// 地震预警那层「整屏压色」：磨砂子系统已经删掉，这里改用一条半透明底色压暗/压亮整屏，
    /// 卡片自己的底色再叠一层，保证正中那张卡片跳得出来。
    /// QuakeVeil 是兜底：近乎不透明，把别的都盖住。
    /// </summary>
    private void ApplyQuakeFrost(FullScreenClockViewModel vm)
    {
        var light = vm.Chrome.IsLight;

        QuakeSheet.Background = new SolidColorBrush(light
            ? Color.FromArgb(0x3D, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x59, 0x00, 0x00, 0x00));

        EqFrost.Background = new SolidColorBrush(light
            ? Color.FromArgb(0xB8, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xC8, 0x12, 0x14, 0x1A));

        QuakeVeil.Background = new SolidColorBrush(light
            ? Color.FromArgb(0xE8, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE8, 0x08, 0x08, 0x0C));
    }

    private static Color ParseColorOrDefault(string? hex, Color fallback)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? fallback : Color.Parse(hex); }
        catch { return fallback; }
    }

    /// <summary>
    /// 随尺寸变化的几个宽度：提醒面板最大宽 = 屏幕宽 × 2/3；第一行提醒合并阈值 = 屏幕宽 × 1/3；
    /// 底部课程信息最大宽 = 整行宽（去掉页面左右各 40 的边距）× 1/2，超了由 Viewbox 缩字而不是裁掉。
    /// </summary>
    private void UpdateSizeDependentLayout()
    {
        if (DataContext is not FullScreenClockViewModel vm) return;
        if (Bounds.Width <= 0) return;  // 还没量出尺寸，别把课程信息压成 0 宽
        vm.ReminderPanelMaxWidth = Bounds.Width * 2.0 / 3.0;
        vm.MergeThreshold = Bounds.Width / 3.0;
        vm.CourseInfoMaxWidth = Math.Max(160, (Bounds.Width - 80) / 2.0);
        // 地震提醒块：整窗宽去掉页面左右各 40 的边距，再留出内边距与描边
        vm.EarthquakeAlertMaxWidth = Math.Max(240, Bounds.Width - 80 - 72);
        ApplyBodyPositions();
    }

    /// <summary>
    /// 地震提醒弹出动画：整块横向从中间向两侧绽开。
    /// 地震预警插件在 CI 里是往下摊开的，跟大屏这面墙不搭，这里改成左右展开。
    /// 用的是 Avalonia 对 RenderTransform 的原生插值（TransformOperations），缩放中心在正中。
    /// </summary>
    private static readonly Animation QuakeRevealAnimation = new()
    {
        Duration = TimeSpan.FromMilliseconds(380),
        Easing = new CubicEaseOut(),
        FillMode = FillMode.Forward,
        Children =
        {
            new KeyFrame { Cue = new Cue(0d), Setters = { new Setter(Visual.RenderTransformProperty, QuakeScale(0.04)) } },
            new KeyFrame { Cue = new Cue(1d), Setters = { new Setter(Visual.RenderTransformProperty, QuakeScale(1)) } },
        },
    };

    /// <summary>只做横向缩放的变换（1 = 原样，越接近 0 越窄）。</summary>
    private static TransformOperations QuakeScale(double scaleX)
    {
        var builder = new TransformOperations.Builder(1);
        builder.AppendScale(scaleX, 1);
        return builder.Build();
    }

    private void OnEarthquakeAlertPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (e.Property != Visual.IsVisibleProperty) return;

        if (!EarthquakeAlertLayer.IsVisible) return;

        EarthquakeAlertBlock.RenderTransform = QuakeScale(0.04);   // 先归零，免得动画起来前闪一帧整块
        try
        {
            _ = QuakeRevealAnimation.RunAsync(EarthquakeAlertBlock);
        }
        catch
        {
            EarthquakeAlertBlock.RenderTransform = QuakeScale(1);  // 动画起不来就直接显示，别把提醒吞了
        }
    }

    /// <summary>时钟与音量条之间至少留出的距离（音量条顶上来时的联动依据）。</summary>
    private const double ClockVolumeGap = 24;

    /// <summary>正在套用布局（也是往设置里写回实际位置的时候），用来挡住写回引发的重入。</summary>
    private bool _applyingLayout;

    /// <summary>
    /// 时钟、音量条各自的上下位置。设置里存的是「该块中心落在主体区高度的百分之几」（0 最上、100 最下），
    /// 这里按主体区的实测高度换算成上边距，再钳进这一轮算出来的可落范围（滑块的范围也是同一个）。
    ///
    /// 两块从上到下依次是：时钟、音量条（设置里把音量条挪到时钟前面时顺序反过来），谁都不许越界：
    /// - 时钟不出主体区（顶不出上沿、底不出下沿）；
    /// - 音量条上不了时钟头顶，下不出主体区——再往下就压到「最小化 / 退出」那一行了。
    ///
    /// 音量条的下限是固定的，时钟的下限跟着音量条走——所以音量条往上顶，时钟的可落范围就收紧、
    /// 被一并顶上去；反过来往下拖时钟，时钟停在音量条上沿，不会把音量条挤下去。
    /// 不用另记「这次拖的是哪一个」。
    ///
    /// 钳完的位置会写回设置，滑块范围也同步过去：滑块显示的、能拖的就是屏幕上的实际情况。
    /// 预览那一份（<see cref="IsPreview"/>）只照这套摆位，既不改设置、也不动滑块范围——
    /// 预览的尺寸和大屏一致，摆出来的位置本就一样，但它不该有「改设置」这份副作用。
    /// </summary>
    private void ApplyBodyPositions()
    {
        if (Plugin.Settings is not { } settings) return;
        if (_applyingLayout) return;                   // 写回设置引发的重入，一次就够
        var areaHeight = BodyArea.Bounds.Height;
        if (areaHeight <= 0) return;   // 布局还没跑，等下一轮 SizeChanged

        _applyingLayout = true;
        try
        {
            // 地震提醒是整屏正中的浮层，不参与这里的限位：它来去都不该把时钟顶走
            const double topLimit = 0.0;

            // 组件化以后 Margins 落在 ComponentHost 上（内层控件不再有自己的偏移），
            // 所以量高度、摆位置都对着外壳做；内层的高度（字号决定）仍从时钟/音量条取。
            var clockHeight = ClockHost.Bounds.Height;
            var volumeVisible = VolumeHost.IsVisible;
            var volumeHeight = VolumeHost.Bounds.Height;

            // 谁在上由组件顺序决定：默认（时钟排在音量条前面）与改造前逐像素一致；
            // 把音量条拖到时钟前面就镜像过来——音量条贴顶、时钟被挤到它下面。
            var clockFirst = EffectiveOrder(settings, "clock") <= EffectiveOrder(settings, "volume");

            double clockTop, clockLower, clockUpper;
            double volumeTop, volumeLower, volumeUpper;

            if (clockFirst)
            {
                clockLower = topLimit;                                       // 时钟不出主体区上沿
                volumeLower = topLimit + clockHeight + ClockVolumeGap;      // 音量条上不了时钟头顶
                volumeUpper = Math.Max(0, areaHeight - volumeHeight);       // 再往下就压到按钮那一行

                volumeTop = volumeVisible
                    ? Math.Min(Math.Max(TopFor(settings.VolumePositionPercent, areaHeight, volumeHeight), volumeLower), volumeUpper)
                    : 0.0;

                // 时钟：上不出主体区，下压不到音量条上。装不下时宁可两块挨上也别越界
                var clockRoomTop = volumeVisible ? volumeTop - ClockVolumeGap - clockHeight : areaHeight - clockHeight;
                clockUpper = Math.Max(clockLower, clockRoomTop);
                clockTop = Math.Clamp(TopFor(settings.ClockPositionPercent, areaHeight, clockHeight), clockLower, clockUpper);
            }
            else
            {
                // 音量条在上：先把它摆在主体区上半段（上不下邻居，下不出主体区），
                // 时钟再被压到它下面；两者仍各自守着自己的滑块位置。
                volumeLower = topLimit;
                volumeUpper = Math.Max(0, areaHeight - volumeHeight);
                volumeTop = volumeVisible
                    ? Math.Clamp(TopFor(settings.VolumePositionPercent, areaHeight, volumeHeight), volumeLower, volumeUpper)
                    : 0.0;

                clockLower = volumeVisible ? volumeTop + volumeHeight + ClockVolumeGap : topLimit;
                clockUpper = Math.Max(clockLower, areaHeight - clockHeight);
                clockTop = Math.Clamp(TopFor(settings.ClockPositionPercent, areaHeight, clockHeight), clockLower, clockUpper);
            }

            if (volumeVisible) SetTop(VolumeHost, volumeTop);
            SetTop(ClockHost, clockTop);

            // 只摆位置，不回写设置：
            // 位置由「组件的位置偏移」负责，这里只是按默认锚点排好；
            // 以前把钳过的实际位置写回 ClockPositionPercent / VolumePositionPercent，
            // 会让两个滑块互相顶（时钟被音量条上限钳住 → 写回 → 看起来像被改掉），
            // 而且「一键重置」刚恢复默认值就被下一轮布局写回去，表现为重置不生效。
        }
        finally
        {
            _applyingLayout = false;
        }
    }

    /// <summary>按「中心落在区域高度的百分之几」算出该块的上边距（未钳制，钳制在各调用处按优先级做）。</summary>
    private static double TopFor(double percent, double areaHeight, double height)
        => percent / 100.0 * areaHeight - height / 2.0;

    /// <summary>摆好一块的上边距。只在真的变了才写：写过边距还会再跑一轮 SizeChanged，不比较会来回抖。</summary>
    private static void SetTop(Control block, double top)
    {
        if (Math.Abs(block.Margin.Top - top) > 0.5)
            block.Margin = new Thickness(0, top, 0, 0);
    }

    /// <summary>「点击后滚完这一遍就收」已生效：这期间重复点击不生效。</summary>
    private bool _alertMarqueeOncePlaying;

    /// <summary>把正在循环的弹幕改成「接着当前进度滚，滚完这一遍就收」（点击触发）。
    /// 只在弹幕真的在滚时非空；null 表示没在播或已经在收尾状态。</summary>
    private Action? _marqueeStopAfterThisPass;

    /// <summary>触屏/触控笔按住中：这段时间按悬停算（一直循环），抬手才当「点了一下」收尾。</summary>
    private bool _touchPressActive;

    /// <summary>鼠标悬停某条预警：在顶部弹幕带显示其详情并循环滚动（自动切换，不需要先收起旧条）。</summary>
    private void AlertPointerEntered(object? sender, PointerEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not AlertDisplayItem item) return;
        if (DataContext is not FullScreenClockViewModel vm) return;

        if (e.Pointer.Type == PointerType.Mouse) _touchPressActive = false;   // 换成鼠标了，清掉触屏状态

        vm.ExpandedAlertDetail = item.Detail;
        // 顶部只有一个弹幕 ScrollViewer。先停旧弹幕再重新启动：悬停另一条时 Text 已变，
        // 不能靠 SizeChanged 触发，显式重启。悬停态 = 一直循环滚。
        StopMarquee(AlertDetailScroll);
        StartMarquee(AlertDetailScroll, once: false);
    }

    /// <summary>点击某条预警：详情弹幕滚完一整遍后自动收起。
    /// 一遍没滚完时重复点击不生效。</summary>
    private void AlertPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (sender is not Control c || c.DataContext is not AlertDisplayItem item) return;
        if (_alertMarqueeOncePlaying) return;   // 已经在收尾的那一遍里，忽略
        if (DataContext is not FullScreenClockViewModel vm) return;

        // 触屏/触控笔按下不是「点击」：按下时也会触发 PointerEntered，那是「手指/笔尖放在上面」，
        // 该按悬停处理（一直循环滚）。若当成点击，就会变成「按住不动只滚一遍」——
        // 教室大屏正是触屏，抬手时（PointerExited）再按点击收尾。
        if (e.Pointer.Type != PointerType.Mouse)
        {
            _touchPressActive = true;
            return;
        }

        vm.ExpandedAlertDetail = item.Detail;

        // 鼠标点之前必然已经悬停，弹幕早就在循环滚了：接着当前进度往下滚，滚完这一遍再收，
        // 不重头开始播（用户 2026-09-19 反馈：点头重播很出戏）。
        if (_marqueeStopAfterThisPass is { } stopAfterThisPass)
        {
            _alertMarqueeOncePlaying = true;
            stopAfterThisPass();
            return;
        }

        // 没在播（触屏上直接点，没有悬停态）：从头滚一遍。
        StopMarquee(AlertDetailScroll);
        StartMarquee(AlertDetailScroll, once: true);
    }

    /// <summary>鼠标移出该条预警：收起顶部弹幕。点击触发的单次播放不受影响——
    /// 触屏抬手也会产生 PointerExited，不能让它把「滚完一遍」打断。</summary>
    private void AlertPointerExited(object? sender, PointerEventArgs e)
    {
        if (_alertMarqueeOncePlaying) return;

        // 触屏抬手 = 轻点了一下：别在抬手瞬间把弹幕掐掉，让它滚完当前这一遍再收（等同鼠标点击）。
        if (_touchPressActive)
        {
            _touchPressActive = false;
            if (_marqueeStopAfterThisPass is { } stopAfterThisPass)
            {
                _alertMarqueeOncePlaying = true;
                stopAfterThisPass();
                return;
            }

            StopMarquee(AlertDetailScroll);
            StartMarquee(AlertDetailScroll, once: true);
            return;
        }

        if (DataContext is not FullScreenClockViewModel vm) return;
        vm.ExpandedAlertDetail = null;
        StopMarquee(AlertDetailScroll);
    }

    /// <summary>停止弹幕：停定时器、清平移、清启动标记（下次 StartMarquee 才能重新启动）。</summary>
    private void StopMarquee(ScrollViewer sv)
    {
        if (sv.Tag is DispatcherTimer timer) timer.Stop();
        if (sv.Content is Control content) content.RenderTransform = null;
        sv.Tag = null;
        _marqueeStopAfterThisPass = null;
        _alertMarqueeOncePlaying = false;
    }

    /// <summary>预警详情弹幕：文字超宽时自动横向滚动（从右向左），不用手动拖动滚动条。</summary>
    private void DetailScroll_SizeChanged(object? sender, SizeChangedEventArgs e)
    {
        // 组件化后宿主隐藏时内层 IsVisible 仍是 true，所以要按「实际有没有显示」来判断
        if (sender is ScrollViewer sv && sv.IsEffectivelyVisible)
            StartMarquee(sv, once: false);
    }

    private void StartMarquee(ScrollViewer sv, bool once)
    {
        // 预览那份不起弹幕：设置页里没必要为了看一眼排版常驻一个 16 毫秒的定时器
        if (IsPreview) return;

        if (!sv.IsEffectivelyVisible) return;   // 单行/双行只有当前布局那个可见，隐藏版跳过
        if (sv.Tag is not null) return;         // 已启动过，防反复触发重复动画

        _alertMarqueeOncePlaying = once;        // 先立标记：布局就绪前的这几帧也挡住重复点击
        // 同步挂平移、先把文字推到视口右侧外（布局未完成前 viewport 未知，用大偏移占位）：
        // 这样「重开弹幕的瞬间」不会有一帧把原文整段占满显示框（此前 translate.X 默认 0，
        // 首个 Tick 之前会闪一下完整开头）。
        var translate = new TranslateTransform { X = 1e6 };
        if (sv.Content is Control c0) c0.RenderTransform = translate;
        sv.Tag = translate;               // 占位标记；StopMarquee 会清掉，挂起的回调据此退出
        StartMarqueeWhenReady(sv, translate, 0, once);
    }

    /// <summary>等布局完成（ScrollViewer.Extent 就绪）后再初始化弹幕；未就绪则下一帧重试。</summary>
    private void StartMarqueeWhenReady(ScrollViewer sv, TranslateTransform translate, int attempt, bool once)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!ReferenceEquals(sv.Tag, translate)) return;   // 鼠标已移开被 StopMarquee 取消

            // 文字宽度取 TextBlock 在无限宽约束下量出的 DesiredSize（NoWrap → 就是文字真实宽度）。
            // 不能用 sv.Extent：ScrollViewer 的 Extent 有「不小于视口宽」的下限，短文字会被抬到视口宽，
            // 按它算一遍的行程就会在文字离场后多空跑 (视口宽 − 文字宽)/speed 秒——
            // 天气预警详情长（超过视口）所以看不出来，摄像头那条详情短，正好暴露出这个空档。
            var extent = sv.Extent.Width;
            var viewport = sv.Viewport.Width;  // 可见区宽度
            var tb = sv.Content as TextBlock;
            var hasText = tb != null && !string.IsNullOrEmpty(tb.Text);
            var desired = tb?.DesiredSize.Width ?? 0;   // 无限宽约束下量出的文字自然宽度
            var textWidth = desired > 0
                ? Math.Min(desired, extent)            // 兜底：量不出或量得比 Extent 还大就用 Extent
                : extent;

            // 文本已设但宽度还没量出来 → 布局尚未完成，下一帧再试（最多 5 次）。
            // Extent 有「不小于视口宽」的下限，短文字时它一上来就非 0，所以这里必须看 DesiredSize。
            if (hasText && (extent <= 0 || desired <= 0) && attempt < 5)
            {
                StartMarqueeWhenReady(sv, translate, attempt + 1, once);
                return;
            }

            // 经典弹幕（和 B 站一样）：文字整体从「视口右侧外」进场，一路匀速向左，滚出左侧后
            // 再从右侧重新进场，中途不停顿（行程 = 视口宽 + 文字宽，尾部一离场就立刻循环重播）。
            const double speed = 100.0;                        // 像素/秒
            var total = (textWidth + viewport) / speed;        // 滚一整遍的时长

            translate.X = viewport;                           // 起步位置：视口右侧外
            var start = DateTime.UtcNow;

            // 收尾截止时刻（相对 start 的秒数）：PositiveInfinity = 一直循环滚。
            // 点击预警时由 _marqueeStopAfterThisPass 改成「滚完当前这一遍就收」——
            // 取当前这一遍的结尾，所以是接着往下滚、不重头播。
            var stopAfter = once ? total : double.PositiveInfinity;
            _marqueeStopAfterThisPass = () =>
            {
                var e = (DateTime.UtcNow - start).TotalSeconds;
                var phase = e % total;                       // 当前这一遍已经走了多少秒
                // 距离这一遍结束不到 2 秒就再多滚一遍，免得点完立刻就收、像没反应
                stopAfter = e - phase + (total - phase < 2.0 ? 2 : 1) * total;
            };

            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
            timer.Tick += (_, _) =>
            {
                var elapsed = (DateTime.UtcNow - start).TotalSeconds;

                // 一遍滚完（文字从右侧外进场、滚到左侧外）就收起详情，并放开重复点击的限制
                if (elapsed >= stopAfter)
                {
                    timer.Stop();
                    if (sv.Content is Control cc) cc.RenderTransform = null;
                    sv.Tag = null;
                    _marqueeStopAfterThisPass = null;
                    _alertMarqueeOncePlaying = false;
                    if (DataContext is FullScreenClockViewModel vm) vm.ExpandedAlertDetail = null;
                    return;
                }

                // 用 elapsed 对总循环时长取模实现无限循环重播，
                // 这样最左边的字一开始也在视口外右侧，不会一开场就被裁掉。
                translate.X = viewport - (elapsed % total) * speed;
            };
            timer.Start();
            sv.Tag = timer;   // 持有引用防 GC，同时作为「已启动」标记
        });
    }

    /// <summary>右上角日期连点计数（间隔超过 3 秒视为中断）</summary>
    private int _dateClickCount;
    private DateTime _lastDateClick = DateTime.MinValue;

    private void DateText_PointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        var now = DateTime.Now;
        if ((now - _lastDateClick).TotalSeconds > 3) _dateClickCount = 0;
        _lastDateClick = now;
        _dateClickCount++;

        if (_dateClickCount >= 7)
        {
            _dateClickCount = 0;
            ShowAboutPopup();
        }
    }

    private AboutPopupWindow? _aboutWindow;

    private void ShowAboutPopup()
    {
        if (_aboutWindow != null && _aboutWindow.IsVisible) return;
        if (TopLevel.GetTopLevel(this) is not Window owner) return;   // 预览那份不在窗口里，点了也不弹

        var vm = DataContext as FullScreenClockViewModel;
        var settings = Plugin.Settings;

        // 玻璃子系统已删除：这里不再对整面墙做实时模糊，关于窗口直接浮在背景上。

        // 窗口底色跟**背景图明暗**走：暗图黑底白字、亮图白底黑字（不是跟界面主题走——
        // 暗色界面压在一张很亮的壁上时，深色方片会突兀得像贴上去的）。
        // 只有「自定义图片」这类能算出明暗的背景才这么判；壁纸/纯色仍跟随界面主题。
        var light = vm?.Chrome.IsLight ?? false;
        if (settings is not null
            && (BackgroundKind)settings.BackgroundKind == BackgroundKind.CustomImage
            && ClockThemeApplier.BackgroundIsDark(settings) is { } imageDark)
            light = !imageDark;

        _aboutWindow = new AboutPopupWindow(light);
        _aboutWindow.Closed += (_, _) => _aboutWindow = null;
        _aboutWindow.Show(owner); // 以全屏窗口为 Owner（置顶、随主窗口关闭）
    }

    private void ExitButton_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as FullScreenClockViewModel)?.ExitFullScreen();
    }

    private void MinimizeButton_Click(object? sender, RoutedEventArgs e)
    {
        (DataContext as FullScreenClockViewModel)?.Minimize();
    }

    /// <summary>鼠标进入计数区域：显示底部记录规则</summary>
    private void CounterArea_PointerEntered(object? sender, PointerEventArgs e)
    {
        if (DataContext is FullScreenClockViewModel vm) vm.ShowCountRules = true;
    }

    /// <summary>鼠标离开计数区域：隐藏记录规则</summary>
    private void CounterArea_PointerExited(object? sender, PointerEventArgs e)
    {
        if (DataContext is FullScreenClockViewModel vm) vm.ShowCountRules = false;
    }
}
