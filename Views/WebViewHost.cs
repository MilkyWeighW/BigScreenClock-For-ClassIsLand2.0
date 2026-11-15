using System;
using System.Drawing;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Web.WebView2.Core;

namespace LargeScreenClockRevived.Views;

/// <summary>
/// 真正承载 CoreWebView2 的宿主：环境创建、控制器挂到父窗口、导航、尺寸与释放都收在这里。
///
/// 为什么不用现成的 Avalonia WebView 控件：项目约定不引第三方封装（见 csproj 注释），
/// 而网页背景只要「一块填满下层窗口的画面 + 能导航 + 能拿掉」，Core API 已经够用。
///
/// 线程与生命周期：
/// - 环境/控制器的创建是异步的，必须在有消息泵的 UI 线程上发起；所有入口都 try/catch，
///   失败就 <see cref="IsReady"/> 保持 false，由调用方静默退回纯色，绝不抛到大屏。
/// - 控制器是 COM 对象，<see cref="Dispose"/> 里先 Close 再放引用；环境本身不手工释放
///   （WebView2 自己管理浏览器进程，Close 后它会自行回收）。
/// </summary>
public sealed class WebViewHost : IDisposable
{
    private CoreWebView2Environment? _environment;
    private CoreWebView2Controller? _controller;
    private IntPtr _parentHwnd;
    private bool _initializing;
    private bool _disposed;
    private string? _pendingUrl;

    /// <summary>最近一次真正导航过的地址：跟着窗口可见性/状态同步时不要重复导航（否则最小化恢复会重载网页、闪白）。</summary>
    private string? _currentUrl;

    /// <summary>环境与控制器都建好、可以导航了。</summary>
    public bool IsReady => !_disposed && _controller?.CoreWebView2 is not null;

    /// <summary>最近一次导航/初始化失败的原因（仅用于设置页提示，不往上抛）。</summary>
    public string? LastError { get; private set; }

    /// <summary>创建完成（可能失败后再也不会成功）。调用方用它做一次性收尾。</summary>
    public event EventHandler? Ready;

    /// <summary>
    /// 把 WebView2 挂到 <paramref name="parentHwnd"/> 下。重复调用是幂等的：
    /// 已经就绪就直接返回 true，正在初始化则返回 false（调用方下次同步再来）。
    /// </summary>
    public async Task<bool> EnsureInitializedAsync(IntPtr parentHwnd)
    {
        if (_disposed || parentHwnd == IntPtr.Zero) return false;
        if (IsReady) return true;
        if (_initializing) return false;

        _initializing = true;
        try
        {
            var userDataFolder = ResolveUserDataFolder();
            _environment = await CoreWebView2Environment.CreateAsync(
                browserExecutableFolder: null, userDataFolder: userDataFolder, options: null);

            if (_disposed) return false;

            var controller = await _environment.CreateCoreWebView2ControllerAsync(parentHwnd);
            if (_disposed)
            {
                controller.Close();
                return false;
            }

            _controller = controller;

            // 网页背景只是装饰：右键菜单、开发者工具、状态栏、快捷键一律不要，
            // 免得用户在大屏上误触弹出浏览器 UI。
            var settings = controller.CoreWebView2.Settings;
            settings.AreDefaultContextMenusEnabled = false;
            settings.AreDevToolsEnabled = false;
            settings.IsStatusBarEnabled = false;
            settings.AreBrowserAcceleratorKeysEnabled = false;
            settings.IsZoomControlEnabled = false;
            settings.IsBuiltInErrorPageEnabled = false;

            // 透明底色：网页自己没画满时露出下层窗口的纯色兜底，而不是一块死白
            try
            {
                controller.DefaultBackgroundColor = Color.Transparent;
            }
            catch
            {
                // 个别旧运行时没有这个属性：白底也能接受
            }

            controller.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            controller.IsVisible = true;
            _parentHwnd = parentHwnd;

            if (!string.IsNullOrWhiteSpace(_pendingUrl))
            {
                var url = _pendingUrl;
                _pendingUrl = null;
                NavigateCore(url);
            }

            LastError = null;
            Ready?.Invoke(this, EventArgs.Empty);
            return true;
        }
        catch (Exception ex)
        {
            // 运行时缺失/被策略禁用/父窗口提前销毁……都走这里：静默失败，网页背景退回纯色
            LastError = ex.Message;
            try { _controller?.Close(); } catch { }   // 别让半初始化的控制器挂在父窗口上
            _controller = null;
            _environment = null;
            return false;
        }
        finally
        {
            _initializing = false;
        }
    }

    /// <summary>
    /// 把网页画面铺满父窗口客户区。
    /// 参数单位是物理像素（Bounds 用像素），<paramref name="rasterizationScale"/> 是
    /// 物理像素 / DIP 的缩放比：不设它，4K@150% 下网页会按 100% 画，字和控件明显偏小。
    /// </summary>
    public void SetBounds(double width, double height, double rasterizationScale = 1.0)
    {
        if (_disposed || _controller is null) return;
        try
        {
            var w = Math.Max(1, (int)Math.Round(width));
            var h = Math.Max(1, (int)Math.Round(height));
            _controller.Bounds = new Rectangle(0, 0, w, h);

            var scale = Math.Clamp(rasterizationScale, 0.5, 8.0);
            if (Math.Abs(_controller.RasterizationScale - scale) > 0.001)
                _controller.RasterizationScale = scale;
        }
        catch
        {
            // 控制器已关闭/窗口正在销毁：忽略
        }
    }

    /// <summary>整体显示/隐藏网页画面（窗口隐藏时顺手停掉渲染，省 GPU/CPU）。</summary>
    public void SetVisible(bool visible)
    {
        if (_disposed || _controller is null) return;
        try
        {
            _controller.IsVisible = visible;
        }
        catch
        {
            // 同上：控制器状态不可用时忽略
        }
    }

    /// <summary>
    /// 导航到用户填的地址。地址为空/不合法就什么都不做（保持窗口兜底色）；
    /// 还没初始化好就先记下来，初始化完成时补导航。
    /// </summary>
    public void Navigate(string? url)
    {
        if (_disposed) return;
        var normalized = NormalizeUrl(url);
        if (normalized.Length == 0) return;

        if (!IsReady)
        {
            _pendingUrl = normalized;
            return;
        }

        NavigateCore(normalized);
    }

    private void NavigateCore(string url)
    {
        // 地址没变就不重导航：Sync 会在可见性/窗口状态变化时被反复调用，
        // 每次都 Navigate 会让网页在最小化恢复、切全屏时重新加载（白闪一下）。
        if (string.Equals(_currentUrl, url, StringComparison.OrdinalIgnoreCase)) return;

        try
        {
            _currentUrl = url;
            _controller?.CoreWebView2?.Navigate(url);
        }
        catch (Exception ex)
        {
            // 导航失败不弹框：窗口保持空白/兜底色，与「静默降级」的整体约定一致
            LastError = ex.Message;
            _currentUrl = null;   // 允许下一次同步重试
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        try
        {
            if (!e.IsSuccess)
            {
                LastError = "网页加载失败（" + e.WebErrorStatus + "），已保持空白背景";
                _currentUrl = null;   // 加载失败也允许下一次同步重试
                // 失败页本身已经被 IsBuiltInErrorPageEnabled=false 关掉，
                // 这里再导航到一张透明空页，避免上一次成功的画面残留成误导。
                _controller?.CoreWebView2?.NavigateToString(
                    "<html><body style=\"margin:0;background:transparent\"></body></html>");
            }
        }
        catch
        {
            // 事件里抛异常会打断 WebView2 的消息泵，必须吞掉
        }
    }

    /// <summary>
    /// 地址清洗：允许 http/https/file/about；没写协议时补 https://。
    /// 不合法返回空串，调用方当作「没有地址」，保持兜底纯色。
    /// </summary>
    public static string NormalizeUrl(string? raw)
    {
        try
        {
            var text = (raw ?? "").Trim();
            if (text.Length == 0) return "";

            if (text.StartsWith("//", StringComparison.Ordinal))
                text = "https:" + text;
            else if (!text.Contains("://", StringComparison.Ordinal))
                text = "https://" + text;

            if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return "";
            return uri.Scheme switch
            {
                "http" or "https" or "file" or "about" => text,
                _ => "",
            };
        }
        catch
        {
            return "";
        }
    }

    /// <summary>用户数据目录放在插件配置目录下；拿不到可写目录就退回临时目录。</summary>
    private static string? ResolveUserDataFolder()
    {
        try
        {
            var baseDirectory = Plugin.ConfigFolder
                                ?? Path.Combine(Path.GetTempPath(), "LargeScreenClockRevived");
            var directory = Path.Combine(baseDirectory, "webview2");
            Directory.CreateDirectory(directory);
            return directory;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        try
        {
            if (_controller?.CoreWebView2 is { } core)
                core.NavigationCompleted -= OnNavigationCompleted;
        }
        catch
        {
            // 已经断开：忽略
        }

        try
        {
            _controller?.Close();
        }
        catch
        {
            // 控制器可能已随父窗口销毁
        }

        _controller = null;
        _environment = null;
        _pendingUrl = null;
    }
}
