using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using LargeScreenClockRevived.Services;
using LargeScreenClockRevived.ViewModels;

namespace LargeScreenClockRevived.Views;

/// <summary>
/// 全屏窗口。窗口本身只管窗口的事（全屏、躲鼠标、Esc/F11 退出），
/// 画面全在 <see cref="ClockSurface"/> 里 —— 设置页的预览放的也是它。
///
/// 置顶：窗口自己 Topmost；ClassIsland 的「顶部课表栏」（主界面）也要显示并且压在这块大屏之上，
/// 所以显示/激活时、以及可见期间每 1.5 秒都会重排一次 Z 序（见 <see cref="CiTopmostGuard"/>）。
/// 只提升主界面这一个窗口，不碰设置页等其它窗口。
/// </summary>
public partial class FullScreenClockWindow : Window
{
    private const int TopmostGuardIntervalMs = 1500;

    /// <summary>
    /// 躲光标的延迟取消源：鼠标一动就作废上一次的延迟，换一个新的。
    /// 每次 PointerMoved 都 new 一个却从不 Dispose 会一路漏 CTS 与它的 timer 注册（旧代码的问题），
    /// 这里改成「取消并释放旧的、换新的」，窗口关闭时统一清掉最后一个。
    /// </summary>
    private CancellationTokenSource? _hideCursorCts;
    private DispatcherTimer? _fadeOutTimer;
    private bool _reassertPosted;
    private readonly DispatcherTimer _topmostGuardTimer =
        new() { Interval = TimeSpan.FromMilliseconds(TopmostGuardIntervalMs) };

    /// <summary>
    /// 全屏看门狗：设置窗口交互 / 系统切前台后，全屏窗口偶尔会被落回 Normal（表现为「窗口化」）。
    /// 光靠 Opened/Activated 兜不住，这里在可见期间低频检查并把状态拉回 FullScreen。
    /// </summary>
    private readonly DispatcherTimer _fullScreenGuardTimer =
        new() { Interval = TimeSpan.FromMilliseconds(700) };

    public FullScreenClockWindow()
    {
        InitializeComponent();
        HookBackgroundModes();
        KeyDown += OnKeyDown;
        PointerMoved += OnPointerMoved;

        // 窗口收起时没有限位在起作用了，把滑块范围放回整段（不然上次地震留下的下限会一直卡着设置页）
        PropertyChanged += (_, e) =>
        {
            if (e.Property == Visual.IsVisibleProperty)
            {
                if (!IsVisible) (DataContext as FullScreenClockViewModel)?.ResetPositionLimits();
                UpdateTopmostGuardTimer();
            }
            else if (e.Property == WindowStateProperty && IsVisible && WindowState == WindowState.Normal)
            {
                // 从全屏被落回 Normal（点设置页、系统切前台等）：下一拍立刻拉回全屏
                ReassertFullScreenSoon();
            }
        };
        Closed += (_, _) =>
        {
            _topmostGuardTimer.Stop();
            _hideCursorCts?.Cancel();
            _hideCursorCts?.Dispose();
            _hideCursorCts = null;
            (DataContext as FullScreenClockViewModel)?.ResetPositionLimits();
        };

        _topmostGuardTimer.Tick += (_, _) => ArrangeClassIslandTopmost();
        _fullScreenGuardTimer.Tick += (_, _) => EnsureFullScreenState();

        // 显示后、每次被激活时都重排一次 Z 序：CI 课表栏始终压在大屏上面
        Opened += (_, _) =>
        {
            // 复用的窗口 Hide→Show 时，平台侧有概率把状态落回 Normal（表现为「窗口化」）。
            // 每次真正显示出来都兜一次全屏；最小化恢复交给 Activated→EnsureFullScreen。
            if (WindowState is not (WindowState.FullScreen or WindowState.Minimized))
            {
                WindowState = WindowState.FullScreen;
            }

            ArrangeClassIslandTopmost();
            UpdateTopmostGuardTimer();
        };
        Activated += (_, _) =>
        {
            EnsureFullScreenState();
            ArrangeClassIslandTopmost();
        };
    }

    /// <summary>可见期间周期性重排 Z 序，并兜底全屏状态；不可见时两支定时器都停掉。</summary>
    private void UpdateTopmostGuardTimer()
    {
        if (IsVisible)
        {
            if (!_topmostGuardTimer.IsEnabled) _topmostGuardTimer.Start();
            if (!_fullScreenGuardTimer.IsEnabled) _fullScreenGuardTimer.Start();
        }
        else
        {
            _topmostGuardTimer.Stop();
            _fullScreenGuardTimer.Stop();
        }
    }

    /// <summary>
    /// 把窗口拉回全屏：先纠 Avalonia 的状态，再按 Win32 的窗口矩形兜一次底。
    /// 「窗口化」有两种成因——状态属性真的变回 Normal，或属性还报 FullScreen 但原生窗口已被平台缩小，
    /// 所以两步都要做；最小化状态不去打扰。
    /// </summary>
    private void EnsureFullScreenState()
    {
        if (!IsVisible || WindowState == WindowState.Minimized) return;

        if (WindowState != WindowState.FullScreen)
            WindowState = WindowState.FullScreen;

        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
        if (handle == IntPtr.Zero || NativeFullScreen.CoversMonitor(handle)) return;

        // SetWindowPos 会把自己顶到最上面，摆回去之后再把课表栏抬回来
        if (NativeFullScreen.ForceCoverMonitor(handle)) ArrangeClassIslandTopmost();
    }

    /// <summary>供 ViewModel 在 Show 之后补几拍用：不依赖定时器，立刻做一次全屏兜底。</summary>
    public void ReassertFullScreen()
    {
        try
        {
            EnsureFullScreenState();
        }
        catch
        {
            // 兜底失败不影响大屏本身
        }
    }

    /// <summary>下一拍（等平台把 Normal 应用完）再拉一次全屏，避免和平台状态互相打架。</summary>
    private void ReassertFullScreenSoon()
    {
        if (_reassertPosted) return;
        _reassertPosted = true;
        Dispatcher.UIThread.Post(() =>
        {
            _reassertPosted = false;
            EnsureFullScreenState();
        }, DispatcherPriority.Background);
    }

    /// <summary>把大屏与 CI 课表栏的 Z 序重排一遍（课表栏在上）；拿不到窗口句柄时静默跳过。</summary>
    private void ArrangeClassIslandTopmost()
    {
        try
        {
            var clockHandle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (clockHandle == IntPtr.Zero) return;

            CiTopmostGuard.Arrange(clockHandle, FindClassIslandMainWindowHandle());
        }
        catch
        {
            // 置顶失败不影响大屏本身
        }
    }

    /// <summary>取消正在进行的退出淡出（淡出途中用户又把大屏叫回来时用），并把透明度复位。</summary>
    public void CancelHideFade()
    {
        _fadeOutTimer?.Stop();
        _fadeOutTimer = null;
        Opacity = 1;
    }

    /// <summary>
    /// 退出前的准备：立刻停止置顶守卫，别在窗口淡出的这半秒里还去抬 CI 课表栏——
    /// 那会表现为课表栏在退出瞬间闪一下。
    /// </summary>
    public void PrepareForExit()
    {
        _topmostGuardTimer.Stop();
        _fullScreenGuardTimer.Stop();
    }

    /// <summary>
    /// 窗口显示前调用：先把大屏内容层收起来，避免第一帧把完整界面闪出来。
    /// 真正开屏过渡还是 <see cref="BeginBackgroundIntro"/> 在 Show 之后触发。
    /// </summary>
    public void PrepareForIntro()
    {
        Surface?.PrepareIntro();
    }

    /// <summary>
    /// 让画面重演一遍开屏过渡。由 ViewModel 在窗口 <see cref="Window.Show()"/> 之后调用；
    /// <paramref name="priorScreenshot"/> 是 Show 之前拍的干净屏幕截图。
    /// </summary>
    public void BeginBackgroundIntro(Bitmap? priorScreenshot)
    {
        Surface?.BeginIntro(priorScreenshot);
    }

    /// <summary>
    /// 退出大屏：整窗淡出后再真正隐藏，避免背景与组件「啪」地消失。
    /// 动画结束把 Opacity 复位成 1，下次显示时开屏从全不透明开始。时长由调用方按「动画速率」换算。
    /// </summary>
    public void HideWithFade(int durationMs)
    {
        _fadeOutTimer?.Stop();
        _fadeOutTimer = null;

        if (durationMs <= 0)
        {
            Opacity = 1;
            Hide();
            return;
        }

        var steps = Math.Max(1, durationMs / 16);
        var interval = Math.Max(1, durationMs / steps);
        var start = DateTime.UtcNow;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(interval) };
        _fadeOutTimer = timer;
        timer.Tick += (_, _) =>
        {
            var elapsed = (DateTime.UtcNow - start).TotalMilliseconds;
            var t = Math.Clamp(elapsed / durationMs, 0, 1);
            var eased = t * t * (3 - 2 * t);   // smoothstep：两端慢、中间快，退出不突兀
            Opacity = 1 - eased;

            if (t < 1) return;

            timer.Stop();
            if (ReferenceEquals(_fadeOutTimer, timer)) _fadeOutTimer = null;
            Opacity = 1;   // 复位：下次 Show 时开屏动画从透明层叠开始
            Hide();
        };
        timer.Start();
    }

    /// <summary>
    /// 找 ClassIsland 主界面（顶部课表栏）的窗口句柄。
    /// 不按枚举所有同进程窗口来猜——那样会把设置页、对话框也一起抬起来。
    /// </summary>
    private static IntPtr FindClassIslandMainWindowHandle()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return IntPtr.Zero;

        foreach (var window in desktop.Windows)
        {
            var typeName = window.GetType().FullName;
            if (typeName is null || !typeName.Contains("MainWindow", StringComparison.Ordinal)) continue;
            if (!window.IsVisible) continue;

            var handle = window.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero) return handle;
        }

        return IntPtr.Zero;
    }

    private void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key is Key.Escape or Key.F11)
        {
            (DataContext as FullScreenClockViewModel)?.ExitFullScreen();
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        Cursor = Cursor.Default;

        // 取消并释放上一次的延迟，再换一个新的（见字段注释）。
        var previous = _hideCursorCts;
        _hideCursorCts = new CancellationTokenSource();
        previous?.Cancel();
        previous?.Dispose();

        _ = HideCursorAfterDelayAsync(_hideCursorCts.Token);
    }

    /// <summary>鼠标静止 10 秒后把光标藏起来；被下一次移动取消时不做事。</summary>
    private async Task HideCursorAfterDelayAsync(CancellationToken token)
    {
        try
        {
            await Task.Delay(10000, token).ConfigureAwait(false);
            Dispatcher.UIThread.Post(() => Cursor = null);
        }
        catch (OperationCanceledException)
        {
            // 鼠标又动了：这一次延迟作废
        }
    }
}
