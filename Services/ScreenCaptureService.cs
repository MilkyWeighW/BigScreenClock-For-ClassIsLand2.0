using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace LargeScreenClockRevived.Services;

/// <summary>
/// 把主屏当前画面截成一张位图，同时整体压暗，用来给「纯色/半透明」这类背景当底图
/// （让白色文字在任何桌面上都能看清）。
///
/// 走 GDI 而不是渲染器：<c>RenderTargetBitmap</c> 抓的是本进程自己能渲染的东西，
/// 抓不到别的窗口；而大屏是全屏窗口，用 BitBlt 从屏幕 DC 拷一份才是「用户此刻看到的样子」。
/// 也不走 <c>Graphics.CopyFromScreen</c>（WinForms），项目里不想为一个截图拖进 WinForms。
///
/// 流程：GetDC(NULL) 拿屏幕 DC → 建内存 DC + 兼容位图 → BitBlt 拷过去 →
/// GetDIBits 取 32bpp BGRA（bitmapinfo 的 biHeight 取负 = top-down，行序才和 Avalonia 一致）→
/// 逐像素乘 (1-darken) → 写进 Bgra8888/Opaque 的 WriteableBitmap。
/// 所有 GDI 句柄都在 finally 里按「先还原选入的对象、再删位图、删 DC、最后 ReleaseDC」的顺序释放。
/// 任何一步失败（远程桌面、会话锁屏、显存不足）都返回 null，绝不抛给 UI。
/// </summary>
public static class ScreenCaptureService
{
    /// <summary>
    /// 截主屏并压暗。<paramref name="darken"/> 是压暗比例（0 = 原样，1 = 全黑），默认 0.45。
    /// 返回的位图是 Bgra8888 + AlphaFormat.Opaque（截图本来就没有透明度），DPI 记 96。
    /// 失败返回 null。
    /// </summary>
    public static WriteableBitmap? CapturePrimaryScreen(double darken = 0.45)
    {
        var width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        var height = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN);
        if (width <= 0 || height <= 0) return null;

        // 压暗系数夹到 [0,1]：传 1 是纯黑，传负数/超过 1 都不能让乘法反号或溢出。
        var factor = 1.0 - Math.Clamp(darken, 0.0, 1.0);

        HDC screenDc = default;
        HDC memoryDc = default;
        HBITMAP bitmap = default;
        HGDIOBJ previousObject = default;
        var bitmapSelected = false;

        try
        {
            screenDc = PInvoke.GetDC(HWND.Null);
            if (screenDc == default) return null;

            memoryDc = PInvoke.CreateCompatibleDC(screenDc);
            if (memoryDc == default) return null;

            bitmap = PInvoke.CreateCompatibleBitmap(screenDc, width, height);
            if (bitmap.IsNull) return null;

            previousObject = PInvoke.SelectObject(memoryDc, bitmap);
            // SelectObject 失败返回 NULL 或 HGDI_ERROR(-1)：只有拿到真正的前一个对象才需要还原。
            bitmapSelected = previousObject != HGDIOBJ.Null
                             && (nint)previousObject != -1;
            if (!bitmapSelected) return null;

            // CAPTUREBLT 把分层窗口（部分游戏/播放器/输入法的浮层）也带上；
            // 少了它，有些全屏程序会截成一片黑。
            if (!PInvoke.BitBlt(memoryDc, 0, 0, width, height, screenDc, 0, 0,
                    ROP_CODE.SRCCOPY | ROP_CODE.CAPTUREBLT))
                return null;

            var pixels = ReadPixels(memoryDc, bitmap, width, height);
            if (pixels is null) return null;

            Darken(pixels, factor);
            return Encode(pixels, width, height);
        }
        catch
        {
            // 截图失败不该让大屏挂掉：调用方拿 null 退回纯色
            return null;
        }
        finally
        {
            // 顺序不能乱：位图还选在 DC 里时 DeleteObject 会失败，必须先还原
            if (memoryDc != default && bitmapSelected)
                PInvoke.SelectObject(memoryDc, previousObject);
            if (!bitmap.IsNull)
                PInvoke.DeleteObject(bitmap);
            if (memoryDc != default)
                PInvoke.DeleteDC(memoryDc);
            if (screenDc != default)
                PInvoke.ReleaseDC(HWND.Null, screenDc);
        }
    }

    /// <summary>
    /// 用 GetDIBits 把兼容位图读成 0xAARRGGBB 的托管数组（每像素 4 字节，行间无 padding 差异）。
    ///
    /// 关键在 <c>biHeight</c> 取负：正数表示 bottom-up，读出来的行序是倒的，
    /// 直接塞给 Avalonia 会得到上下颠倒的背景。取负即 top-down，DIB 行序与位图行序一致。
    /// </summary>
    private static unsafe uint[]? ReadPixels(HDC deviceContext, HBITMAP bitmap, int width, int height)
    {
        var pixels = new uint[checked(width * height)];

        var info = default(BITMAPINFO);
        info.bmiHeader.biSize = (uint)sizeof(BITMAPINFOHEADER);
        info.bmiHeader.biWidth = width;
        info.bmiHeader.biHeight = -height;   // 负数 = top-down
        info.bmiHeader.biPlanes = 1;
        info.bmiHeader.biBitCount = 32;
        info.bmiHeader.biCompression = 0;    // BI_RGB：不压缩，32bpp 下就是 BGRA(高位字节不用)
        info.bmiHeader.biSizeImage = (uint)(width * height * 4);

        fixed (uint* buffer = pixels)
        {
            var lines = PInvoke.GetDIBits(deviceContext, bitmap, 0, (uint)height, buffer, &info,
                DIB_USAGE.DIB_RGB_COLORS);
            if (lines < height) return null;
        }

        return pixels;
    }

    /// <summary>
    /// 逐像素乘压暗系数。GetDIBits 的 32bpp BI_RGB 高位字节是「未定义」的，
    /// 这里顺手写成 0xFF：后面若被当成带 alpha 的图（例如再走一遍模糊）不至于整张透明。
    /// </summary>
    private static void Darken(uint[] pixels, double factor)
    {
        for (var i = 0; i < pixels.Length; i++)
        {
            var pixel = pixels[i];
            var blue = (byte)((pixel & 0xFF) * factor);
            var green = (byte)(((pixel >> 8) & 0xFF) * factor);
            var red = (byte)(((pixel >> 16) & 0xFF) * factor);
            pixels[i] = 0xFF000000u | ((uint)red << 16) | ((uint)green << 8) | blue;
        }
    }

    /// <summary>
    /// 把连续像素数组写进 WriteableBitmap。不能假设位图内部 stride == width*4
    /// （Avalonia 的 framebuffer 可能有行对齐填充），所以按 RowBytes 逐行拷。
    /// </summary>
    private static unsafe WriteableBitmap? Encode(uint[] pixels, int width, int height)
    {
        var result = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Opaque);

        try
        {
            using var frame = result.Lock();
            var stride = frame.RowBytes;
            var rowBytes = Math.Min((long)stride, (long)width * 4);
            var destination = (byte*)frame.Address;

            fixed (uint* source = pixels)
            {
                for (var y = 0; y < height; y++)
                    Buffer.MemoryCopy(source + (long)y * width, destination + (long)y * stride,
                        stride, rowBytes);
            }

            return result;
        }
        catch
        {
            // 锁/拷贝失败时把半成品位图释放掉，别留一块显存等 GC
            result.Dispose();
            return null;
        }
    }
}
