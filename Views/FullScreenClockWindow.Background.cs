using System;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using LargeScreenClockRevived.Models;
using LargeScreenClockRevived.Services;

namespace LargeScreenClockRevived.Views;

/// <summary>
/// 窗口的「背景模式」接线（1.4.0.0）：背景与组件分两条渲染路径之后，
/// 背景的一部分能力不在 Avalonia 的可视树里——实时模糊走 DWM、网页背景走独立的 WebView2 下层窗口。
///
/// 这里只做三件事：
/// 1. 让大屏窗口始终是透明窗口（底色由 <see cref="BackgroundLayer"/> 自己铺，留白处才是背景色）；
/// 2. 背景类型是「实时模糊」时给窗口句柄打开 DWM 的 blur-behind（原生实时模糊）；
/// 3. 背景类型是「网页」时让 <see cref="WebBackgroundService"/> 把下层网页窗口挂到大屏窗口下面。
///
/// 任何一步失败都只回退到纯色背景，绝不让大屏起不来。
/// </summary>
public partial class FullScreenClockWindow
{
    private WebBackgroundService? _webBackground;
    private PluginSettings? _backgroundSettings;
    private bool _backgroundHooked;

    /// <summary>
    /// 挂上背景模式的全部钩子。构造末尾调用一次（窗口是复用实例，不需要每次显示都重挂）。
    /// </summary>
    private void HookBackgroundModes()
    {
        if (_backgroundHooked) return;
        _backgroundHooked = true;

        // 透明窗口必须在第一次显示前就定下来：平台侧可能要按这个重建窗口。
        // 底色不靠窗口画——BackgroundLayer 永远铺一层 BackgroundColor 作为底，
        // 所以这里透明不会变成「看桌面」。
        TransparencyLevelHint = new[] { WindowTransparencyLevel.Transparent };
        Background = Brushes.Transparent;

        Opened += (_, _) => SyncBackgroundMode();
        Closed += (_, _) => ReleaseBackgroundMode();

        PropertyChanged += (_, e) =>
        {
            if (e.Property == IsVisibleProperty)
            {
                if (IsVisible) SyncBackgroundMode();
                else ReleaseBackgroundBitmaps();
            }
            else if (e.Property == WindowStateProperty)
            {
                SyncBackgroundMode();
            }
        };

        _backgroundSettings = Plugin.Settings;
        if (_backgroundSettings is not null)
            _backgroundSettings.PropertyChanged += OnBackgroundSettingChanged;
    }

    private void OnBackgroundSettingChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginSettings.BackgroundKind)
            or nameof(PluginSettings.BackgroundWebUrl)
            or nameof(PluginSettings.BackgroundColor))
        {
            SyncBackgroundMode();
        }
    }

    /// <summary>按当前背景类型把窗口切成「纯 Avalonia / DWM 模糊 / 网页下层窗口」三种模式之一。</summary>
    private void SyncBackgroundMode()
    {
        if (!IsVisible) return;

        var kind = (BackgroundKind)(Plugin.Settings?.BackgroundKind ?? (int)BackgroundKind.Wallpaper);
        var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;

        // 0) 透明窗口不一定每个平台/主题都能真的生效（远程桌面、禁用 DWM、某些显卡驱动会回退）。
        //    这两档的「背景」不在 Avalonia 里画：拿不到真透明就只剩一片黑，所以必须自己兜底成背景色。
        var wantsPassthrough = kind is BackgroundKind.NativeBlur or BackgroundKind.WebPage;
        var transparent = ActualTransparencyLevel == WindowTransparencyLevel.Transparent;
        if (wantsPassthrough && !transparent)
        {
            Background = new SolidColorBrush(ParseBackgroundColor());
            try { _webBackground?.Detach(); } catch { }
            _webBackground = null;
            return;
        }

        Background = Brushes.Transparent;

        // 1) DWM 实时模糊：只有这一档需要开；其它档必须关掉，否则整窗会一直糊着桌面。
        var wantBlur = kind == BackgroundKind.NativeBlur;
        if (handle != IntPtr.Zero)
        {
            try
            {
                NativeBlur.TryEnable(handle, wantBlur, 0x99000000);
            }
            catch
            {
                // 原生模糊失败就退纯色，不影响大屏
            }
        }

        // 2) 网页背景：下面挂一个独立的 WebView2 窗口；其它档一律收掉。
        try
        {
            if (kind == BackgroundKind.WebPage && Plugin.Settings is { } settings)
            {
                _webBackground ??= new WebBackgroundService();
                _webBackground.Attach(this);
                _webBackground.Sync(settings);
            }
            else
            {
                _webBackground?.Detach();
                _webBackground = null;
            }
        }
        catch
        {
            // 同步网页失败：把可能半建好的下层窗口收掉，别留一个孤儿 Edge 窗口
            try { _webBackground?.Detach(); } catch { }
            _webBackground = null;
        }
    }

    /// <summary>
    /// 大屏隐藏（不是关闭）时把背景占着的整屏位图交掉：壁纸缓存降到弱引用层、截图/模糊中间图释放。
    /// 这是「长时运行不涨内存」的关键一步——隐藏后可能一整夜不再显示，没必要把几十 MB 位图攥着。
    /// 下次显示时 <c>ApplyBackdrop</c> 会重新解码（缓存还在，通常不用重新读盘）。
    /// </summary>
    private void ReleaseBackgroundBitmaps()
    {
        try
        {
            Surface?.Background?.ReleaseBitmaps();
        }
        catch
        {
            // 释放失败不影响下次重建
        }
    }

    /// <summary>兜底底色：解析设置里的 BackgroundColor，失败就用黑色（与改造前默认一致）。</summary>
    private Color ParseBackgroundColor()
    {
        try
        {
            return Avalonia.Media.Color.Parse(Plugin.Settings?.BackgroundColor ?? "#000000");
        }
        catch
        {
            return Avalonia.Media.Colors.Black;
        }
    }

    /// <summary>窗口关闭/退出时把原生模糊和网页窗口都收掉（背景资源不跟着窗口一起漏）。</summary>
    private void ReleaseBackgroundMode()
    {
        try
        {
            var handle = TryGetPlatformHandle()?.Handle ?? IntPtr.Zero;
            if (handle != IntPtr.Zero) NativeBlur.TryEnable(handle, false);
        }
        catch
        {
        }

        try
        {
            _webBackground?.Detach();
        }
        catch
        {
        }

        _webBackground = null;

        if (_backgroundSettings is not null)
        {
            _backgroundSettings.PropertyChanged -= OnBackgroundSettingChanged;
            _backgroundSettings = null;
        }
    }
}
