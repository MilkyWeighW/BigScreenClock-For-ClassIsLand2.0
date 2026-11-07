using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Threading;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.Services;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 大屏背景层：壁纸 / 纯色 / 自定义图片三种类型都在这一层里画。
///
/// 为什么单独成层：这套背景要在「开屏」和「运行中换背景」两个时机演过渡——
/// 旧画面淡出、新画面淡入；壁纸类型还多一步：先给用户看一眼压暗的当前屏幕，
/// 再过渡到「放大 + 模糊」的壁纸，让人感觉镜头从屏幕外面推了进去。
/// 如果直接塞进窗口 Background，这些都没法做。
///
/// 分工：
/// - <see cref="Apply"/>：套静态值（类型、图、缩放、明暗），用于设置页预览和运行中改设置；
/// - <see cref="PlayIntroAsync"/>：开屏那一套完整过渡（压暗截图 → 背景 → 由调用方淡入组件）。
///
/// 已经不再有任何「液态玻璃 / 磨砂 / 折射」相关代码：那套整层删掉了，组件文字直接叠在背景上。
/// </summary>
public partial class BackgroundLayer : UserControl
{
    // 过渡时长（毫秒）
    private const int FadeMs = 520;             // 旧画面 → 新画面
    private const int VeilFadeMs = 360;         // 明暗遮罩变化
    private const int IntroDimFadeMs = 280;     // 开屏：「压暗」淡入的时长
    private const double IntroDimOpacity = 0.45; // 开屏时压暗截图的程度（与原截图压暗系数一致）
    private const double IntroOverlapRatio = 0.75; // 压暗演到 75% 就开始换背景，两段动画重叠

    /// <summary>背景层圆角：全屏时 0，设置页预览时是预览卡片的圆角。</summary>
    public new CornerRadius CornerRadius
    {
        get => ClipBorder.CornerRadius;
        set => ClipBorder.CornerRadius = value;
    }

    /// <summary>壁纸的景深放大用：挂在 TargetImage.RenderTransform 上（XAML 里不建，见 axaml 注释）。</summary>
    private readonly ScaleTransform _targetScale = new(1, 1);

    private bool _attached;
    private bool _appliedOnce;
    private bool _disposed;

    /// <summary>景深推近动画的取消令牌：换背景/换设置/离开视觉树时立刻停手。</summary>
    private CancellationTokenSource? _zoomCts;

    /// <summary>开屏时间线的取消令牌：中途换设置/离开视觉树时停掉旧的那一轮。</summary>
    private CancellationTokenSource? _introCts;

    private Bitmap? _targetBitmap;
    private Bitmap? _priorBitmap;
    private Bitmap? _fadingBitmap;

    // 「当前目标图是怎么来的」这几个值用来避免拖动滑块时重复解码/模糊整张壁纸
    private int _lastKind = -1;
    private string? _appliedWallpaperPath;
    private int _appliedWallpaperWidth = -1;
    private int _wallpaperRequestId;
    private double _appliedBlurRadius = double.NaN;
    private string? _appliedImageFile;

    public BackgroundLayer()
    {
        InitializeComponent();
        ClipToBounds = true;
        IsHitTestVisible = false;
        TargetImage.RenderTransform = _targetScale;
        TargetImage.RenderTransformOrigin = RelativePoint.Center;
    }

    /// <summary>
    /// 套用设置里的静态值。预览（<paramref name="isPreview"/>）下不截屏、不播动画，直接出结果。
    /// 运行中调用会做一次旧背景 → 新背景的交叉淡入。
    /// </summary>
    public void Apply(PluginSettings settings, bool isPreview)
    {
        if (_disposed) return;

        // 设置一变就停掉正在进行的推近动画与开屏时间线，静态值以这次 Apply 为准
        StopZoomAnimation();
        StopIntroTimeline();

        var kind = (BackgroundKind)settings.BackgroundKind;
        var animate = _appliedOnce && !isPreview && _attached;
        var oldSource = TargetImage.Source as Bitmap;

        switch (kind)
        {
            case BackgroundKind.Wallpaper:
                ApplyWallpaper(settings, isPreview);
                break;
            case BackgroundKind.CustomImage:
                ApplyCustomImage(settings);
                break;
            default:
                ApplySolidColor(settings);
                break;
        }

        ApplyVeil(settings.BackgroundDim, animate: animate);

        var sourceChanged = !ReferenceEquals(oldSource, TargetImage.Source)
            || kind != (BackgroundKind)_lastKind;
        if (animate && oldSource is not null && sourceChanged)
            CrossFadeFrom(oldSource);
        else
            FinishImmediately();

        _lastKind = (int)kind;
        _appliedOnce = true;
    }

    /// <summary>
    /// 窗口显示之前调用：把三层背景都瞬时收掉（包括上一轮留下的过渡），
    /// 让第一帧只看到窗口底色，随后再由 <see cref="PlayIntroAsync"/> 从压暗截图开始演。
    /// 不清过渡的话，第二次开屏时「设为 0」会变成慢慢淡出，看起来像背景先闪一下。
    /// </summary>
    public void PrepareForIntro()
    {
        StopIntroTimeline();
        TargetImage.Transitions = null;
        SolidFill.Transitions = null;
        PriorImage.Transitions = null;
        IntroVeil.Transitions = null;
        TargetImage.Opacity = 0;
        SolidFill.Opacity = 0;
        PriorImage.Opacity = 0;
        IntroVeil.Opacity = 0;
    }

    /// <summary>
    /// 开屏过渡。
    /// <list type="bullet">
    /// <item>纯色 / 自定义图片：压暗截图淡出，背景同时淡入（交叉淡入）；</item>
    /// <item>壁纸：先停在压暗截图上，再交叉淡入「放大 + 模糊」好的壁纸。</item>
    /// </list>
    /// <paramref name="onTargetReveal"/> 在「新背景开始淡入」那一刻回调，调用方用它让组件跟着淡入。
    /// <paramref name="priorScreenshot"/> 是「大屏还没出现时」的屏幕截图（调用方在 Show 窗口之前拍好），
    /// 传空则在这里补拍——那会拍到大屏自己，只做兜底。
    /// </summary>
    public async Task PlayIntroAsync(PluginSettings settings, Action? onTargetReveal, Bitmap? priorScreenshot = null)
    {
        if (_disposed) return;

        // 窗口刚 Show 时视觉树可能还没接上（_attached 要等 layout），
        // 这里最多等约半秒，避免「第一次调用直接 return」导致动画被跳过。
        for (var i = 0; i < 30 && !_attached && !_disposed; i++)
            await Task.Delay(16);

        if (_disposed || !_attached) return;

        var kind = (BackgroundKind)settings.BackgroundKind;
        var speed = Math.Clamp(settings.AnimationSpeed, 0.5, 2.0);
        var dimMs = Scaled(IntroDimFadeMs, speed);
        var fadeMs = Scaled(FadeMs, speed);
        var zoom = Math.Clamp(settings.WallpaperZoom, 1.0, 1.6);

        // 先套静态值（保证目标图已经烘好），但暂时不显示。
        // 和内容层同理：上一轮留下的淡入过渡会让「设为 0」变成慢慢淡出，所以先摘掉过渡再置 0。
        Apply(settings, isPreview: false);
        StopIntroTimeline();
        TargetImage.Transitions = null;
        SolidFill.Transitions = null;
        TargetImage.Opacity = 0;
        SolidFill.Opacity = 0;
        PriorImage.Opacity = 0;
        PriorImage.Transitions = null;
        IntroVeil.Transitions = null;
        IntroVeil.Opacity = 0;

        // 开屏遮罩之前若还有正在淡出的旧图，先收掉
        DisposeFadingFrame();

        // ① 当前屏幕截图原样垫在最底下（优先用调用方在 Show 之前拍的干净截图）。
        //    这里不再预先乘压暗系数：压暗交给 IntroVeil 做动画，第一帧才不会直接黑一下。
        var shot = priorScreenshot ?? ScreenCaptureService.CapturePrimaryScreen(0.0);
        if (shot is null)
        {
            // 截屏失败（远程桌面/受保护内容）：退化成直接显示背景 + 组件淡入
            FinishImmediately();
            onTargetReveal?.Invoke();
            return;
        }

        _priorBitmap = shot;
        PriorImage.Source = shot;
        PriorImage.Opacity = 1;

        // ② 压暗与后续渲染重叠执行：压暗演到 75% 就开始换背景 / 淡入组件，
        //    两段动画叠在一起跑，总时长更短，也不会出现「压暗演完才动」的卡顿感。
        var revealAt = Math.Max(1, (int)Math.Round(dimMs * IntroOverlapRatio));
        var total = revealAt + fadeMs;

        RunIntroTimeline(kind, dimMs, revealAt, fadeMs, zoom, onTargetReveal);

        await Task.Delay(total);
    }

    /// <summary>
    /// 开屏动画时间线：手写 16ms 定时器插值，不用 Transitions——
    /// 之前用 Transitions 时「压暗」在部分环境下根本不演（只看得到硬切），
    /// 改成手写后节奏完全可控，而且能把「压暗 → 换背景」这两段重叠起来。
    /// </summary>
    private void RunIntroTimeline(BackgroundKind kind, int dimMs, int revealAt, int fadeMs, double zoom,
        Action? onTargetReveal)
    {
        StopIntroTimeline();

        var cts = new CancellationTokenSource();
        _introCts = cts;

        var wallpaper = kind == BackgroundKind.Wallpaper;
        var fromZoom = wallpaper ? Math.Max(1.0, zoom - 0.15) : 1.0;
        _targetScale.ScaleX = fromZoom;
        _targetScale.ScaleY = fromZoom;

        var revealed = false;
        var total = revealAt + fadeMs;
        var sw = Stopwatch.StartNew();

        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(TimeSpan.FromMilliseconds(16), DispatcherPriority.Render, (_, _) =>
        {
            if (cts.IsCancellationRequested)
            {
                timer?.Stop();
                return;
            }

            var elapsed = sw.Elapsed.TotalMilliseconds;

            // 压暗淡入（CubicEaseOut）：0 → IntroDimOpacity
            var dimProgress = Math.Clamp(elapsed / Math.Max(1.0, dimMs), 0, 1);
            var dimOpacity = IntroDimOpacity * (1 - Math.Pow(1 - dimProgress, 3));

            if (elapsed < revealAt)
            {
                IntroVeil.Opacity = dimOpacity;
                return;
            }

            // 压暗走到 75%：不再等它演完，立刻开始换背景 + 淡入组件（两段动画重叠）
            if (!revealed)
            {
                revealed = true;
                onTargetReveal?.Invoke();
            }

            var fadeProgress = Math.Clamp((elapsed - revealAt) / Math.Max(1.0, fadeMs), 0, 1);
            var eased = 1 - Math.Pow(1 - fadeProgress, 3);

            IntroVeil.Opacity = dimOpacity * (1 - eased);
            PriorImage.Opacity = 1 - eased;

            if (wallpaper)
            {
                var currentZoom = fromZoom + (zoom - fromZoom) * eased;
                _targetScale.ScaleX = currentZoom;
                _targetScale.ScaleY = currentZoom;
                TargetImage.Opacity = eased;
            }
            else if (kind == BackgroundKind.CustomImage)
            {
                TargetImage.Opacity = eased;
            }
            else
            {
                SolidFill.Opacity = eased;
            }

            if (elapsed < total) return;

            timer?.Stop();
            if (ReferenceEquals(_introCts, cts)) _introCts = null;
            cts.Dispose();
            CompleteIntro(kind, zoom);
        });

        timer.Start();
    }

    /// <summary>开屏时间线落到最终状态，并释放垫底的截图。</summary>
    private void CompleteIntro(BackgroundKind kind, double zoom)
    {
        PriorImage.Transitions = null;
        TargetImage.Transitions = null;
        SolidFill.Transitions = null;
        IntroVeil.Transitions = null;

        PriorImage.Opacity = 0;
        IntroVeil.Opacity = 0;
        TargetImage.Opacity = kind == BackgroundKind.SolidColor ? 0 : 1;
        SolidFill.Opacity = kind == BackgroundKind.SolidColor ? 1 : 0;
        var finalZoom = kind == BackgroundKind.Wallpaper ? zoom : 1;
        _targetScale.ScaleX = finalZoom;
        _targetScale.ScaleY = finalZoom;

        PriorImage.Source = null;
        _priorBitmap?.Dispose();
        _priorBitmap = null;
    }

    private void StopIntroTimeline()
    {
        var cts = _introCts;
        _introCts = null;
        if (cts is null) return;
        cts.Cancel();
        cts.Dispose();
    }

    /// <summary>把基准时长按用户设定的动画速率换算成实际毫秒（速率越大越快）。</summary>
    private static int Scaled(int baseMs, double speed)
        => Math.Max(16, (int)Math.Round(baseMs / Math.Clamp(speed, 0.5, 2.0)));

    /// <summary>壁纸：读当前桌面壁纸 → 按屏幕宽度等比加载 → 模糊 → 放大倍率交给 ScaleTransform。</summary>
    private void ApplyWallpaper(PluginSettings settings, bool isPreview)
    {
        var screenWidth = GetScreenPixelWidth();
        var targetWidth = isPreview ? Math.Min(960, screenWidth) : screenWidth;

        var path = WallpaperService.GetWallpaperPath();
        if (path is null)
        {
            // 取不到壁纸（锁屏/动态壁纸/权限）：退回纯色底，至少不是一片黑
            ApplySolidColor(settings);
            return;
        }

        // 用户拖「景深放大」滑块会连续触发 Apply：路径、目标宽度、模糊半径都没变时，
        // 直接复用已经烘好的位图，只改缩放——否则每拖一格就解码 + 模糊一整张壁纸。
        var reusable = _targetBitmap is not null
            && _lastKind == (int)BackgroundKind.Wallpaper
            && string.Equals(path, _appliedWallpaperPath, StringComparison.OrdinalIgnoreCase)
            && targetWidth == _appliedWallpaperWidth
            && Math.Abs(settings.WallpaperBlurRadius - _appliedBlurRadius) < 0.01;

        // 模糊半径按「屏幕像素 → 图片像素」换算：图片随后会被放大铺满屏幕，
        // 在图片里糊 r 个像素，屏幕上看起来就是 r × 屏幕宽/图片宽。
        if (!reusable)
        {
            var radius = settings.WallpaperBlurRadius * targetWidth / Math.Max(1.0, screenWidth);
            _ = LoadWallpaperAsync(path, targetWidth, radius, settings.WallpaperBlurRadius);
        }

        ApplyWallpaperZoom(settings);
    }

    /// <summary>
    /// 异步取「解码 + 模糊」后的壁纸：先走 <see cref="WallpaperCacheService"/> 的内存 / 磁盘缓存，
    /// 只有真正没缓存时才在线程池里解码与模糊。这样开屏那一下与拖动「景深放大」时都不会卡 UI。
    /// 用递增的请求号丢弃过期结果（用户快速改设置时只认最后一次）。
    /// </summary>
    private async Task LoadWallpaperAsync(string path, int targetWidth, double radiusInImagePixels, double blurRadiusSetting)
    {
        var requestId = ++_wallpaperRequestId;
        Bitmap? bitmap;
        try
        {
            bitmap = await WallpaperCacheService.GetBlurredWallpaperAsync(path, targetWidth, radiusInImagePixels);
        }
        catch
        {
            bitmap = null;
        }

        if (requestId != _wallpaperRequestId || _disposed)
        {
            bitmap?.Dispose();
            return;
        }

        if (bitmap is null)
        {
            // 解码/模糊失败：退回纯色底，至少不是一片黑
            if (Plugin.Settings is { } settings) ApplySolidColor(settings);
            return;
        }

        ReplaceTarget(bitmap);
        _appliedWallpaperPath = path;
        _appliedWallpaperWidth = targetWidth;
        _appliedBlurRadius = blurRadiusSetting;
        _appliedImageFile = null;
        _lastKind = (int)BackgroundKind.Wallpaper;
    }

    /// <summary>套用景深放大倍率（缩放只影响绘制，不重解码位图）。</summary>
    private void ApplyWallpaperZoom(PluginSettings settings)
    {
        var zoom = Math.Clamp(settings.WallpaperZoom, 1.0, 1.6);
        _targetScale.ScaleX = zoom;
        _targetScale.ScaleY = zoom;
        TargetImage.Stretch = Stretch.UniformToFill;
        SolidFill.Opacity = 0;
        TargetImage.Opacity = 1;
    }

    /// <summary>自定义图片：按设置里的填充方式显示；文件缺失时退回纯色底。</summary>
    private void ApplyCustomImage(PluginSettings settings)
    {
        var fileChanged = !string.Equals(settings.BackgroundImageFile, _appliedImageFile,
            StringComparison.OrdinalIgnoreCase);
        var reusable = _targetBitmap is not null
            && _lastKind == (int)BackgroundKind.CustomImage
            && !fileChanged;

        if (!reusable)
        {
            var path = BackgroundImageStore.ResolvePath(settings.BackgroundImageFile);
            Bitmap? bitmap = null;
            if (!string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    using var stream = File.OpenRead(path);
                    bitmap = Bitmap.DecodeToWidth(stream, GetScreenPixelWidth(),
                        BitmapInterpolationMode.HighQuality);
                }
                catch
                {
                    bitmap = null;
                }
            }

            if (bitmap is null)
            {
                ApplySolidColor(settings);
                return;
            }

            ReplaceTarget(bitmap);
            _appliedImageFile = settings.BackgroundImageFile;
            _appliedWallpaperPath = null;
        }

        _targetScale.ScaleX = 1;
        _targetScale.ScaleY = 1;
        TargetImage.Stretch = settings.BackgroundImageStretch switch
        {
            1 => Stretch.Uniform,       // 完整显示（留白）
            2 => Stretch.Fill,          // 拉伸
            _ => Stretch.UniformToFill, // 铺满裁切
        };
        SolidFill.Opacity = 0;
        TargetImage.Opacity = 1;
    }

    /// <summary>纯色背景：一条底色，颜色取设置里的 BackgroundColor。</summary>
    private void ApplySolidColor(PluginSettings settings)
    {
        ReplaceTarget(null);
        SolidFill.Background = new SolidColorBrush(ParseColorOrDefault(settings.BackgroundColor, Colors.Black));
        TargetImage.Opacity = 0;
        SolidFill.Opacity = 1;
        _targetScale.ScaleX = 1;
        _targetScale.ScaleY = 1;
        _appliedWallpaperPath = null;
        _appliedImageFile = null;
    }

    /// <summary>明暗遮罩：BackgroundDim 负值压暗、正值提亮；0 = 完全透明。</summary>
    private void ApplyVeil(double dim, bool animate)
    {
        var (color, opacity) = dim switch
        {
            < -0.5 => (Colors.Black, Math.Min(0.85, -dim / 100.0 * 0.85)),
            > 0.5 => (Colors.White, Math.Min(0.55, dim / 100.0 * 0.55)),
            _ => (Colors.Transparent, 0.0),
        };
        Veil.Background = new SolidColorBrush(color);
        if (animate) SetFade(Veil, VeilFadeMs);
        Veil.Opacity = opacity;
    }

    /// <summary>
    /// 换目标图：上一张挪到 PriorImage，留作交叉淡入的「旧画面」。
    /// 注意不能立刻 Dispose：PriorImage 还在显示它，提前释放会让正在绘制的位图失效。
    /// </summary>
    private void ReplaceTarget(Bitmap? next)
    {
        var previous = _targetBitmap;
        if (previous is not null && !ReferenceEquals(previous, next))
        {
            DisposeFadingFrame();
            _fadingBitmap = previous;
            PriorImage.Source = previous;
            PriorImage.Opacity = 1;
        }

        TargetImage.Source = next;
        _targetBitmap = next;
    }

    private void CrossFadeFrom(IImage oldSource)
    {
        PriorImage.Source = oldSource;
        PriorImage.Opacity = 1;
        SetFade(PriorImage, FadeMs);
        SetFade(TargetImage, FadeMs);
        SetFade(SolidFill, FadeMs);
        PriorImage.Opacity = 0;
        _ = DisposeAfterFadeAsync(FadeMs + 60);
    }

    private async Task DisposeAfterFadeAsync(int ms)
    {
        try
        {
            await Task.Delay(ms);
        }
        catch
        {
            // 忽略取消/关闭时的异常
        }

        if (_disposed) return;
        DisposeFadingFrame();
    }

    private void DisposeFadingFrame()
    {
        if (_fadingBitmap is null) return;
        if (ReferenceEquals(PriorImage.Source, _fadingBitmap))
            PriorImage.Source = null;
        _fadingBitmap.Dispose();
        _fadingBitmap = null;
    }

    /// <summary>立即落到最终状态（无动画）。</summary>
    private void FinishImmediately()
    {
        PriorImage.Transitions = null;
        TargetImage.Transitions = null;
        SolidFill.Transitions = null;
        Veil.Transitions = null;
        IntroVeil.Transitions = null;
        PriorImage.Opacity = 0;
        IntroVeil.Opacity = 0;
        DisposeFadingFrame();
    }

    private static void SetFade(Control control, int ms)
        => control.Transitions = new Transitions
        {
            new DoubleTransition
            {
                Property = Visual.OpacityProperty,
                Duration = TimeSpan.FromMilliseconds(ms),
                Easing = new CubicEaseOut(),
            },
        };

    /// <summary>
    /// 景深推近：手写 16ms 定时器插值，不用 Transitions。
    /// ScaleTransform 是 RenderTransform，不在可视树里、拿不到动画时钟，
    /// 给它挂 DoubleTransition 只会「瞬间跳到位」——这正是上一版开屏看不到推近动画的原因。
    /// </summary>
    private void AnimateZoomTo(double zoom, int ms)
    {
        StopZoomAnimation();

        var cts = new CancellationTokenSource();
        _zoomCts = cts;

        var from = _targetScale.ScaleX;
        var duration = Math.Max(1, ms);
        var sw = Stopwatch.StartNew();

        // 定时器要在 UI 线程上建：BackgroundLayer 的 Apply/PlayIntro 都由 UI 线程发起
        DispatcherTimer? timer = null;
        timer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(16),
            DispatcherPriority.Render,
            (_, _) =>
            {
                if (cts.IsCancellationRequested)
                {
                    timer?.Stop();
                    return;
                }

                var t = Math.Clamp(sw.Elapsed.TotalMilliseconds / duration, 0, 1);
                var eased = 1 - Math.Pow(1 - t, 3); // CubicEaseOut
                var value = from + (zoom - from) * eased;
                _targetScale.ScaleX = value;
                _targetScale.ScaleY = value;

                if (t < 1) return;
                _targetScale.ScaleX = zoom;
                _targetScale.ScaleY = zoom;
                timer?.Stop();
            });

        timer?.Start();
    }

    private void StopZoomAnimation()
    {
        var cts = _zoomCts;
        _zoomCts = null;
        if (cts is null) return;

        cts.Cancel();
        cts.Dispose();
        _targetScale.Transitions = null;
    }

    /// <summary>屏幕物理宽度（拿不到时给 1920 兜底）。</summary>
    private int GetScreenPixelWidth()
    {
        var top = TopLevel.GetTopLevel(this);
        var screen = top?.Screens?.Primary;
        if (screen is not null && screen.Bounds.Width > 0)
            return (int)Math.Round(screen.Bounds.Width * screen.Scaling);
        return 1920;
    }

    private static Color ParseColorOrDefault(string? hex, Color fallback)
    {
        try { return string.IsNullOrWhiteSpace(hex) ? fallback : Color.Parse(hex); }
        catch { return fallback; }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        // 窗口 Hide 再 Show 时是同一棵视觉树重新挂回来：把「已释放」复位，
        // 否则 Apply/PlayIntro 会在第一行就 return，背景再也不刷新。
        _disposed = false;
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _attached = false;
        ReleaseBitmaps();
    }

    /// <summary>离开视觉树时把整屏位图释放掉（大屏关了不占内存）。</summary>
    public void ReleaseBitmaps()
    {
        _disposed = true;
        StopZoomAnimation();
        TargetImage.Source = null;
        PriorImage.Source = null;
        _targetBitmap?.Dispose();
        _targetBitmap = null;
        _priorBitmap?.Dispose();
        _priorBitmap = null;
        _fadingBitmap?.Dispose();
        _fadingBitmap = null;
    }
}
