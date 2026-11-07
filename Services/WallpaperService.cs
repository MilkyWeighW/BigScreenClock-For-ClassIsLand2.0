using System;
using System.IO;
using Avalonia.Media.Imaging;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 读当前桌面壁纸，并把壁纸解码成可直接当背景用的位图。
///
/// 两条来源，按可靠性排序：
/// 1. <c>SystemParametersInfo(SPI_GETDESKWALLPAPER)</c>：系统自己记的「静态壁纸路径」。
///    动态壁纸（Wallpaper Engine 之类）改的是桌面窗口，不改这个值，所以读到的仍是静态图 ——
///    这也是我们要的：大屏背景拿一张静态图就够，不去碰别人的窗口。
///    注意这个 API 不保证成功（远程桌面、某些精简系统、壁纸是纯色时返回空串）。
/// 2. 注册表 <c>HKCU\Control Panel\Desktop</c> 的 <c>Wallpaper</c> 值：API 失败时的兜底。
///    这里可能存的是带引号的路径或含 <c>%USERPROFILE%</c> 的路径，要清一遍。
///
/// 两个都拿不到（纯色桌面 / 权限问题 / 系统精简）就返回 null，由调用方退回纯色背景。
/// 全部方法只读系统状态，不改任何东西；失败一律 catch 后返回 null，不往上抛。
/// </summary>
public static class WallpaperService
{
    /// <summary>
    /// <c>SPI_GETDESKWALLPAPER</c> 的缓冲区字符数。文档建议 MAX_PATH(260)，
    /// 但长路径（\\?\ 前缀、超长目录）在 260 之外仍可能合法，这里给 1024 一次到位。
    /// </summary>
    private const int WallpaperPathCapacity = 1024;

    private const string DesktopKeyPath = @"Control Panel\Desktop";
    private const string WallpaperValueName = "Wallpaper";

    /// <summary>
    /// 当前静态壁纸的完整路径；取不到（纯色桌面、读取失败、空串）返回 null。
    /// 返回值不保证文件仍存在，调用方解码时自己兜底。
    /// </summary>
    public static string? GetWallpaperPath()
    {
        try
        {
            var fromSpi = ReadFromSystemParameters();
            if (!string.IsNullOrWhiteSpace(fromSpi)) return fromSpi;
        }
        catch
        {
            // 读系统参数失败不致命，继续走注册表兜底
        }

        try
        {
            return ReadFromRegistry();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 读 <c>SPI_GETDESKWALLPAPER</c>：成功时把 UTF-16 路径写进我们给的缓冲区（uiParam 是字符数）。
    /// </summary>
    private static unsafe string? ReadFromSystemParameters()
    {
        var buffer = new char[WallpaperPathCapacity];
        fixed (char* p = buffer)
        {
            // 最后一个参数是「要不要广播设置变更」，读操作传默认值即可。
            // BOOL 有到 bool 的隐式转换，取反即可判断失败。
            if (!PInvoke.SystemParametersInfo(
                    SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETDESKWALLPAPER,
                    (uint)buffer.Length,
                    p,
                    default(SYSTEM_PARAMETERS_INFO_UPDATE_FLAGS)))
                return null;
        }

        // 缓冲区尾部是一串 '\0'，new string 会把它们都带上，必须从第一个 '\0' 截断。
        var end = Array.IndexOf(buffer, '\0');
        var text = end < 0 ? new string(buffer) : new string(buffer, 0, end);
        return NormalizePath(text);
    }

    /// <summary>注册表兜底：值可能是带引号的路径，或 <c>%USERPROFILE%</c> 这种环境变量占位。</summary>
    private static string? ReadFromRegistry()
    {
        using var key = Registry.CurrentUser.OpenSubKey(DesktopKeyPath);
        if (key?.GetValue(WallpaperValueName) is not string raw) return null;
        return NormalizePath(raw);
    }

    /// <summary>去掉首尾空白与成对引号、展开环境变量；结果为空返回 null。</summary>
    private static string? NormalizePath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;

        var text = path.Trim().Trim('"');
        if (text.Length == 0) return null;

        // 注册表里偶尔会存 %USERPROFILE%\... 这种形式；展开后才是真实路径。
        try
        {
            text = Environment.ExpandEnvironmentVariables(text);
        }
        catch
        {
            // 展开失败就按原样用，别因此丢掉一个本来可用的路径
        }

        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    /// <summary>
    /// 解码壁纸并等比缩放到 <paramref name="targetWidth"/> 宽（高按原比例）。
    ///
    /// 用 <see cref="Bitmap.DecodeToWidth(Stream, int, BitmapInterpolationMode)"/>：
    /// 解码器直接按目标宽度出图，不在托管堆里先摆一张原图，省内存也省一次缩放。
    /// 注意它对**小于目标宽度的图也会放大**，调用方传进来的宽度应当是屏幕物理宽度，
    /// 且只在「铺满」这种确实需要放大的场景用。
    /// 文件不存在 / 格式不支持 / 解码失败一律返回 null。
    /// </summary>
    public static Bitmap? LoadWallpaper(string path, int targetWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || targetWidth < 1) return null;

        try
        {
            using var stream = File.OpenRead(path);
            return Bitmap.DecodeToWidth(stream, targetWidth, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            // 文件被删 / 磁盘权限 / 格式不认识（SVG、HEIC 改名成 .png 之类）：当作没有壁纸
            return null;
        }
    }
}
