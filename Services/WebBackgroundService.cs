using System;
using System.ComponentModel;
using System.IO;
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

    /// <summary>
    /// 当前挂着的服务实例（同一时刻只有一个大屏窗口）。设置页面板改完本地网页/地址后
    /// 可以直接找它同步一次，不用为了「立刻生效」再加一层跨模块接线。
    /// </summary>
    public static WebBackgroundService? Active { get; private set; }

    private Window? _clockWindow;
    private PluginSettings? _settings;
    private PluginSettings? _hookedSettings;

    /// <summary>上一次真正导航到的地址：位置跟随/可见性同步都会反复调 Sync，地址没变不重载。</summary>
    private string? _currentNavigationUrl;
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
            Active = this;

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

        if (ReferenceEquals(Active, this)) Active = null;

        if (_hookedSettings is not null)
        {
            try { _hookedSettings.PropertyChanged -= OnSettingsPropertyChanged; } catch { }
            _hookedSettings = null;
        }

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
        HookSettings(settings);
        try
        {
            SyncCore(settings);
        }
        catch
        {
            // 同步失败最多是网页不显示
        }
    }

    /// <summary>
    /// 服务自己盯住设置：面板/窗口任何一处改了背景相关项，这里都能立刻重算导航目标。
    /// 这样「改本地网页 / 改地址」不依赖调用方记得再调一次 Sync，少一条容易漏的接线。
    /// </summary>
    private void HookSettings(PluginSettings settings)
    {
        if (ReferenceEquals(_hookedSettings, settings)) return;

        try
        {
            if (_hookedSettings is not null) _hookedSettings.PropertyChanged -= OnSettingsPropertyChanged;
        }
        catch
        {
            // 旧设置对象可能已销毁
        }

        _hookedSettings = settings;
        _hookedSettings.PropertyChanged += OnSettingsPropertyChanged;
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginSettings.BackgroundKind)
            or nameof(PluginSettings.BackgroundWebUrl)
            or nameof(PluginSettings.BackgroundWebFile)
            or nameof(PluginSettings.BackgroundColor))
        {
            if (_hookedSettings is { } settings) Sync(settings);
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
        NavigateIfNeeded(settings);
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
        // 窗口连 WebView2 一起销毁了：下次重建必须重新导航，不能因为地址没变就跳过
        _currentNavigationUrl = null;
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

    /// <summary>
    /// 解析「现在到底该导航到哪」：本地 HTML 优先（file:///），否则用网页地址。
    ///
    /// 本地文件优先的理由：用户既然明确挑了 html，就是要看它；文件被删/挪走后
    /// 再静默回退到上面的网页地址，两者都没了才留空白兜底色（不弹框）。
    /// </summary>
    private static string ResolveNavigationUrl(PluginSettings settings)
    {
        var localFile = (settings.BackgroundWebFile ?? "").Trim();
        if (localFile.Length > 0)
        {
            try
            {
                // 统一绝对化：面板存的是绝对路径，但手改 Settings.json 可能是相对路径，
                // 直接交给 Uri 会抛（相对地址不算 Absolute Uri）。
                var fullPath = Path.GetFullPath(localFile);
                if (File.Exists(fullPath))
                {
                    // Windows 绝对路径在这里转成 file:///C:/...（空格、中文会被正确转义）。
                    // 不要直接扔给 NormalizeUrl 让它猜协议：C: 开头的路径它认不出来。
                    return new Uri(fullPath).AbsoluteUri;
                }
            }
            catch
            {
                // 路径非法/过长（超 MAX_PATH 之类）：当作没有本地文件
            }
        }

        return WebViewHost.NormalizeUrl(settings.BackgroundWebUrl);
    }

    /// <summary>
    /// 地址变了才导航。Sync 会被位置跟随时钟、可见性变化、设置变更反复调用，
    /// 每次都 Navigation 会让网页整页重载（闪白 + 掉帧），所以按最终 URL 去重。
    /// </summary>
    private void NavigateIfNeeded(PluginSettings settings)
    {
        var url = ResolveNavigationUrl(settings);
        if (url.Length == 0)
        {
            // 没有任何可用源（本地文件不存在且地址为空/非法）：保持兜底纯色
            _currentNavigationUrl = null;
            return;
        }

        if (string.Equals(url, _currentNavigationUrl, StringComparison.Ordinal)) return;

        _currentNavigationUrl = url;
        _webWindow?.Navigate(url);
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
