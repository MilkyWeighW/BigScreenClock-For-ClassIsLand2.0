using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace LargeScreenClockRevived.Views;

/// <summary>
/// 网页背景用的「下层窗口」：一个非 topmost、无边框、不抢焦点的全屏窗口，
/// 尺寸/位置/可见性都跟着大屏窗口走（见 <see cref="Services.WebBackgroundService"/>）。
///
/// 为什么必须单独开一个窗口：WebView2 会创建一个原生子窗口来画网页，
/// 子窗口天然压在同窗口的 Avalonia 内容之上；直接把它挂进大屏窗口会把组件全遮住。
/// 放到下层窗口后，大屏窗口保持透明，组件浮在网页上面，这才是「网页当背景」。
///
/// 焦点：ShowActivated=False 保证显示时不去抢 ClassIsland/大屏的输入焦点。
/// </summary>
public partial class WebBackgroundWindow : Window
{
    /// <summary>承载 CoreWebView2 的宿主，窗口关闭时一起释放。</summary>
    public WebViewHost Host { get; } = new();

    public WebBackgroundWindow()
    {
        InitializeComponent();

        // 窗口尺寸变化时把网页重新铺满（设置页拖窗口/分辨率切换都会走这里）
        SizeChanged += (_, _) => UpdateHostBounds();

        // 窗口句柄只有 Show 之后才有：Opened 里再挂 WebView2 控制器
        Opened += async (_, _) =>
        {
            try
            {
                var hwnd = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
                if (hwnd == IntPtr.Zero) return;
                await Host.EnsureInitializedAsync(hwnd);
                UpdateHostBounds();
            }
            catch
            {
                // 初始化失败保持兜底纯色，绝不影响大屏
            }
        };

        Closed += (_, _) =>
        {
            try
            {
                Host.Dispose();
            }
            catch
            {
                // 释放失败不影响进程退出
            }
        };
    }

    /// <summary>兜底纯色（WebView2 不可用/网页空白时露出的底色），由服务按设置刷新。</summary>
    public void SetFallbackColor(Color color)
    {
        try
        {
            var brush = new SolidColorBrush(color);
            FallbackFill.Background = brush;
            Background = brush;
        }
        catch
        {
            // 颜色解析失败保持默认黑底
        }
    }

    /// <summary>导航到地址；还没初始化好时由宿主记下来、初始化完成后补导航。</summary>
    public void Navigate(string? url)
    {
        try
        {
            Host.Navigate(url);
        }
        catch
        {
            // 导航异常不影响大屏
        }
    }

    /// <summary>
    /// 把网页画面按当前客户区铺满。
    ///
    /// 注意单位：Avalonia 的 ClientSize 是 DIP，而 CoreWebView2Controller.Bounds 要的是
    /// 父窗口客户区里的**物理像素**。多屏缩放下两者差一个 RenderScaling（4K@150% 时差 1.5 倍），
    /// 忘了乘就会只盖住左上角一大半屏幕。
    /// </summary>
    public void UpdateHostBounds()
    {
        try
        {
            var scaling = RenderScaling > 0 ? RenderScaling : 1.0;
            Host.SetBounds(ClientSize.Width * scaling, ClientSize.Height * scaling, scaling);
        }
        catch
        {
            // 窗口正在销毁：忽略
        }
    }
}
