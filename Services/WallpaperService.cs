using System;
using System.IO;
using Avalonia.Media.Imaging;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LargeScreenClockRevived.Services;

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
    /// **绝不放大**：源图比 targetWidth 窄时按源宽解码（顺便也满足「1.25× 像素预算」——
    /// 解码出来的像素数不会超过目标尺寸），铺满的事交给 Image 的 Stretch 与背景色。
    /// 文件不存在 / 格式不支持 / 解码失败一律返回 null。
    /// </summary>
    public static Bitmap? LoadWallpaper(string path, int targetWidth)
    {
        if (string.IsNullOrWhiteSpace(path) || targetWidth < 1) return null;

        try
        {
            using var stream = File.OpenRead(path);

            var decodeWidth = targetWidth;
            if (TryGetPixelSize(path, out var sourceWidth, out _) && sourceWidth > 0 && sourceWidth < decodeWidth)
                decodeWidth = sourceWidth;

            return Bitmap.DecodeToWidth(stream, decodeWidth, BitmapInterpolationMode.HighQuality);
        }
        catch
        {
            // 文件被删 / 磁盘权限 / 格式不认识（SVG、HEIC 改名成 .png 之类）：当作没有壁纸
            return null;
        }
    }

    /// <summary>
    /// 只读图片头部，取像素尺寸（不解码整张图）。支持 PNG / JPEG / BMP / GIF / WebP。
    ///
    /// 为什么需要它：<see cref="Bitmap.DecodeToWidth(Stream, int, BitmapInterpolationMode)"/>
    /// 对小于目标宽度的图会**放大**，而放大既费内存又不会变清晰；先知道源尺寸就能「绝不放大」。
    /// 解析失败返回 false，调用方回退到原来的解码宽度即可。
    /// </summary>
    public static bool TryGetPixelSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (string.IsNullOrWhiteSpace(path)) return false;

        try
        {
            using var stream = File.OpenRead(path);
            return TryReadPixelSize(stream, out width, out height);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>头部最多读这么多字节：JPEG 的 SOF 一般在前几百字节，EXIF/ICC 再大也很少超过这个量级。</summary>
    private const int HeaderProbeBytes = 256 * 1024;

    private static bool TryReadPixelSize(Stream stream, out int width, out int height)
    {
        width = 0;
        height = 0;

        try
        {
            var buffer = new byte[HeaderProbeBytes];
            var read = 0;
            while (read < buffer.Length)
            {
                var chunk = stream.Read(buffer, read, buffer.Length - read);
                if (chunk <= 0) break;
                read += chunk;
            }

            if (read < 16) return false;

            // PNG：固定签名 + IHDR 里的宽高（大端）
            if (buffer[0] == 0x89 && buffer[1] == 0x50 && buffer[2] == 0x4E && buffer[3] == 0x47)
            {
                if (read < 24) return false;
                width = ReadBigEndian32(buffer, 16);
                height = ReadBigEndian32(buffer, 20);
                return width > 0 && height > 0;
            }

            // GIF：宽高是小端 16 位
            if (buffer[0] == (byte)'G' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F')
            {
                width = buffer[6] | (buffer[7] << 8);
                height = buffer[8] | (buffer[9] << 8);
                return width > 0 && height > 0;
            }

            // BMP：BITMAPINFOHEADER 的宽高；高度可能为负（top-down）
            if (buffer[0] == (byte)'B' && buffer[1] == (byte)'M')
            {
                if (read < 26) return false;
                width = Math.Abs(BitConverter.ToInt32(buffer, 18));
                height = Math.Abs(BitConverter.ToInt32(buffer, 22));
                return width > 0 && height > 0;
            }

            // WebP：RIFF....WEBP，具体尺寸看 VP8X / VP8 / VP8L 块
            if (buffer[0] == (byte)'R' && buffer[1] == (byte)'I' && buffer[2] == (byte)'F' && buffer[3] == (byte)'F'
                && buffer[8] == (byte)'W' && buffer[9] == (byte)'E' && buffer[10] == (byte)'B' && buffer[11] == (byte)'P')
                return TryReadWebpSize(buffer, read, out width, out height);

            // JPEG：扫描 SOFn 段
            if (buffer[0] == 0xFF && buffer[1] == 0xD8)
                return TryReadJpegSize(buffer, read, out width, out height);

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static int ReadBigEndian32(byte[] buffer, int offset)
        => (buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3];

    private static bool TryReadJpegSize(byte[] buffer, int length, out int width, out int height)
    {
        width = 0;
        height = 0;

        // 从 SOI 之后开始找 SOF0..SOF15（排除 DHT/JPG/DAC 这几个非 SOF 标记）
        var index = 2;
        while (index + 8 < length)
        {
            if (buffer[index] != 0xFF)
            {
                index++;
                continue;
            }

            var marker = buffer[index + 1];
            if (marker == 0xFF)
            {
                index++;
                continue;
            }

            if (marker == 0xD8 || marker == 0x01 || (marker >= 0xD0 && marker <= 0xD7))
            {
                index += 2;
                continue;
            }

            if (marker == 0xD9 || marker == 0xDA) return false; // EOI / SOS 前还没找到

            var segmentLength = (buffer[index + 2] << 8) | buffer[index + 3];
            if (segmentLength < 2) return false;

            var isStartOfFrame = marker >= 0xC0 && marker <= 0xCF
                                 && marker != 0xC4 && marker != 0xC8 && marker != 0xCC;
            if (isStartOfFrame)
            {
                height = (buffer[index + 5] << 8) | buffer[index + 6];
                width = (buffer[index + 7] << 8) | buffer[index + 8];
                return width > 0 && height > 0;
            }

            index += 2 + segmentLength;
        }

        return false;
    }

    private static bool TryReadWebpSize(byte[] buffer, int length, out int width, out int height)
    {
        width = 0;
        height = 0;
        if (length < 30) return false;

        // 扩展格式：canvas 宽高是 24 位小端（值 - 1）
        if (buffer[12] == (byte)'V' && buffer[13] == (byte)'P' && buffer[14] == (byte)'8' && buffer[15] == (byte)'X')
        {
            width = 1 + (buffer[24] | (buffer[25] << 8) | (buffer[26] << 16));
            height = 1 + (buffer[27] | (buffer[28] << 8) | (buffer[29] << 16));
            return width > 0 && height > 0;
        }

        // 有损：帧头后面是 0x9D 0x01 0x2A 起始码，再后面两个 14 位宽高
        if (buffer[12] == (byte)'V' && buffer[13] == (byte)'P' && buffer[14] == (byte)'8' && buffer[15] == (byte)' ')
        {
            if (length < 30 || buffer[23] != 0x9D || buffer[24] != 0x01 || buffer[25] != 0x2A) return false;
            width = (buffer[26] | (buffer[27] << 8)) & 0x3FFF;
            height = (buffer[28] | (buffer[29] << 8)) & 0x3FFF;
            return width > 0 && height > 0;
        }

        // 无损：0x2F 签名后是 14 位宽高
        if (buffer[12] == (byte)'V' && buffer[13] == (byte)'P' && buffer[14] == (byte)'8' && buffer[15] == (byte)'L')
        {
            if (buffer[20] != 0x2F) return false;
            var bits = (uint)(buffer[21] | (buffer[22] << 8) | (buffer[23] << 16) | (buffer[24] << 24));
            width = (int)(bits & 0x3FFF) + 1;
            height = (int)((bits >> 14) & 0x3FFF) + 1;
            return width > 0 && height > 0;
        }

        return false;
    }
}

