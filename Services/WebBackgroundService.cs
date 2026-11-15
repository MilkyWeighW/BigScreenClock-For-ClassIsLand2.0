using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using LargeScreenClockRevived.Models;
using LargeScreenClockRevived.Views;

namespace LargeScreenClockRevived.Services;

/// <summary>
/// 网页背景的总调度：按设置决定要不要显示那块下层网页窗口，让它跟着大屏窗口
/// 的位置、尺寸、可见性走，并在网页不可用时安静地关掉（大屏只剩透明 + 纯色兜底）。
///
/// 接线方式（由 Lead 在窗口侧调用，本类不自己找窗口）：
/// <code>
/// _webBackground.Attach(this);          // 大屏窗口构造/显示时
/// _webBackground.Sync(settings);        // 背景设置变更、窗口显示后
/// _webBackground.Detach();              // 大屏窗口关闭时
/// </code>
///
/// 内存策略：
/// - 大屏隐藏时立刻把网页窗口藏起来（不再出帧）；
/// - 隐藏超过 <see cref="LingerMs"/> 就连窗口带 WebView2 控制器一起释放，
///   不把 Edge 的渲染进程一直挂后台；下次需要时重建（重建耗时在开屏动画里，用户看不到）。
///
/// 线程约定：所有公开方法都应在 UI 线程调用；内部全程 try/catch，
/// 任何失败都只是「没有网页背景」，绝不影响大屏本身。
/// </summary>
public sealed class WebBackgroundService
{
    /// <summary>大屏隐藏后保留网页窗口的时间：短时间反复开关不用重建，长时间挂着就释放。</summary>
    private const int LingerMs = 12_000;

    /// <summary>跟着大屏窗口挪位置的轮询间隔。比逐帧便宜，拖动窗口时也够跟手。</summary>
    private static readonly TimeSpan FollowInterval = TimeSpan.FromMilliseconds(250);

    private Window? _clockWindow;
    private PluginSettings? _settings;
    private WebBackgroundWindow? _webWindow;
    private DispatcherTimer? _followTimer;
    private DispatcherTimer? _lingerTimer;
    private bool _attached;

    /// <summary>绑定大屏窗口（幂等：重复 Attach 会先解绑旧窗口）。</summary>
    public void Attach(Window clockWindow)
    {
        // 幂等：已经绑在同一块窗口上就别重来。以前这里无条件 Detach→重建，
        // 于是每次背景色/窗口状态变化都会销毁并重建 Edge 窗口（闪黑 + 进程反复启停）。
        if (_attached && ReferenceEquals(_clockWindow, clockWindow)) return;

        try
        {
            Detach();
            _clockWindow = clockWindow;
            _attached = true;

            clockWindow.PropertyChanged += OnClockWindowPropertyChanged;
            clockWindow.Closed += OnClockWindowClosed;

            _followTimer = new DispatcherTimer { Interval = FollowInterval };
            _followTimer.Tick += (_, _) => SyncBounds();

            _lingerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(LingerMs) };
            _lingerTimer.Tick += (_, _) => ReleaseWindowIfHidden();
        }
        catch
        {
            // 绑定失败 = 没有网页背景，不影响大屏
        }
    }

    /// <summary>解绑并释放网页窗口/WebView2（大屏窗口关闭时调用）。</summary>
    public void Detach()
    {
        _attached = false;

        try
        {
            if (_clockWindow is not null)
            {
                _clockWindow.PropertyChanged -= OnClockWindowPropertyChanged;
                _clockWindow.Closed -= OnClockWindowClosed;
            }
        }
        catch
        {
            // 窗口可能已经销毁
        }

        _clockWindow = null;
        _settings = null;
        ReleaseWindow();
        _followTimer?.Stop();
        _lingerTimer?.Stop();
        _followTimer = null;
        _lingerTimer = null;
    }

    /// <summary>
    /// 按设置同步一次：网页背景且运行时可用、大屏可见 → 显示下层窗口并导航；
    /// 其它情况一律隐藏（延迟释放）。
    /// 地址解析失败/网页加载失败都保持兜底纯色，不弹任何框。
    /// </summary>
    public void Sync(PluginSettings settings)
    {
        _settings = settings;
        try
        {
            SyncCore(settings);
        }
        catch
        {
            // 同步失败最多是网页不显示
        }
    }

    private void SyncCore(PluginSettings settings)
    {
        if (!_attached || _clockWindow is null)
            return;

        // 最小化时 IsVisible 仍是 true，但窗口位置会被摆到 -32000 —— 网页窗口不能跟过去，
        // 否则会跑到多屏角落甚至把 Edge 的合成层拖到屏幕外。
        var wantWeb = (BackgroundKind)settings.BackgroundKind == BackgroundKind.WebPage
                      && WebView2Availability.IsAvailable
                      && _clockWindow.IsVisible
                      && _clockWindow.WindowState != WindowState.Minimized;

        if (!wantWeb)
        {
            HideWindow();
            return;
        }

        if (!EnsureWindow(settings))
            return;

        SyncBounds();
        ShowWindow();
        _webWindow?.Navigate(settings.BackgroundWebUrl);
        StartFollow();
    }

    private bool EnsureWindow(PluginSettings settings)
    {
        if (_webWindow is not null) return true;
        if (!WebView2Availability.IsAvailable) return false;

        try
        {
            var window = new WebBackgroundWindow
            {
                Width = Math.Max(1, _clockWindow?.ClientSize.Width ?? 1),
                Height = Math.Max(1, _clockWindow?.ClientSize.Height ?? 1),
            };

            if (_clockWindow is not null)
                window.Position = _clockWindow.Position;

            window.SetFallbackColor(ParseColorOrDefault(settings.BackgroundColor));
            window.Show();
            _webWindow = window;
            return true;
        }
        catch
        {
            _webWindow = null;
            return false;
        }
    }

    private void ShowWindow()
    {
        if (_webWindow is null) return;
        try
        {
            _lingerTimer?.Stop();
            if (!_webWindow.IsVisible) _webWindow.Show();

            // 大屏窗口是 topmost，本窗口不是：只要大屏可见，它天然落在下面。
            // 这里仍主动把网页窗口压到底（NOACTIVATE 不改焦点），避免它盖住 ClassIsland 的窗口。
            BringBelowClockWindow();

            _webWindow.Host.SetVisible(true);
            _webWindow.UpdateHostBounds();
        }
        catch
        {
            // 显示失败就当没网页背景
        }
    }

    private void HideWindow()
    {
        if (_webWindow is null) return;
        try
        {
            _webWindow.Host.SetVisible(false);
            if (_webWindow.IsVisible) _webWindow.Hide();

            // 只有「本来还在显示」时才重新计时。跟随定时器每 250ms 调一次 HideWindow，
            // 每次重置计时会让 12s 的释放窗口永远到不了，Edge 进程就一直在后台挂着。
            if (_lingerTimer is { IsEnabled: false }) _lingerTimer.Start();
        }
        catch
        {
            // 隐藏失败可以忽略
        }
    }

    private void ReleaseWindowIfHidden()
    {
        _lingerTimer?.Stop();
        if (_webWindow is null) return;
        if (_webWindow.IsVisible) return;
        ReleaseWindow();
    }

    private void ReleaseWindow()
    {
        var window = _webWindow;
        _webWindow = null;
        if (window is null) return;

        try
        {
            window.Host.Dispose();
        }
        catch
        {
            // 忽略
        }

        try
        {
            window.Close();
        }
        catch
        {
            // 窗口可能已被平台销毁
        }
    }

    private void StartFollow()
    {
        if (_followTimer is { IsEnabled: false }) _followTimer.Start();
    }

    /// <summary>把网页窗口的位置/尺寸对齐到大屏窗口（Position 是物理像素，尺寸用 DIP）。</summary>
    private void SyncBounds()
    {
        if (_webWindow is null || _clockWindow is null) return;
        try
        {
            if (!_clockWindow.IsVisible)
            {
                HideWindow();
                return;
            }

            var position = _clockWindow.Position;
            if (_webWindow.Position != position)
                _webWindow.Position = position;

            var width = Math.Max(1, _clockWindow.ClientSize.Width);
            var height = Math.Max(1, _clockWindow.ClientSize.Height);
            if (Math.Abs(_webWindow.Width - width) > 0.5) _webWindow.Width = width;
            if (Math.Abs(_webWindow.Height - height) > 0.5) _webWindow.Height = height;

            _webWindow.UpdateHostBounds();
            BringBelowClockWindow();
        }
        catch
        {
            // 跟随时窗口销毁属正常
        }
    }

    private void OnClockWindowPropertyChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        // 只有可见性/窗口状态变化值得立刻同步一次；位置与客户区尺寸在 Avalonia 里
        // 不是 AvaloniaProperty（不会触发这里），由 250ms 的跟随定时器兜住。
        if (e.Property != Visual.IsVisibleProperty
            && e.Property != Visual.BoundsProperty
            && e.Property != Window.WindowStateProperty)
            return;

        if (_settings is { } settings) Sync(settings);
    }

    private void OnClockWindowClosed(object? sender, EventArgs e)
    {
        Detach();
    }

    /// <summary>
    /// 把网页窗口压到大屏窗口下面。大屏窗口是 topmost 时其实不需要这一步，
    /// 但大屏在切换全屏/窗口化的一瞬间可能短暂不是 topmost，主动排一次更稳。
    /// </summary>
    private void BringBelowClockWindow()
    {
        try
        {
            if (_webWindow is null || _clockWindow is null) return;
            var webHandle = _webWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            var clockHandle = _clockWindow.TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (webHandle == IntPtr.Zero || clockHandle == IntPtr.Zero) return;

            // SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER = 0x0001|0x0002|0x0010|0x0200
            // 把网页窗口插到「大屏窗口之后」，即紧贴大屏下面
            NativeWindowOrder.PlaceBehind(webHandle, clockHandle);
        }
        catch
        {
            // Z 序调整失败不影响显示（topmost 关系已经保证大屏在上）
        }
    }

    private static Color ParseColorOrDefault(string? hex)
    {
        try
        {
            return string.IsNullOrWhiteSpace(hex) ? Colors.Black : Color.Parse(hex);
        }
        catch
        {
            return Colors.Black;
        }
    }
}
