using System;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;

namespace LargeScreenClockRevived.Helpers;

/// <summary>
/// 位图模糊：把任意 Avalonia <see cref="Bitmap"/> 拷成 BGRA 像素数组，
/// 交给 <see cref="StackBlur"/> 做一遍（CPU、与渲染后端无关），再写回新的 <see cref="WriteableBitmap"/>。
///
/// 为什么不用 Avalonia 的 <c>BlurEffect</c>：那是合成器效果，ClassIsland 可能跑在软件渲染后端上，
/// 而且每块面板挂一张全屏位图更重。CPU 烘一张共享底图，绘制时只是普通贴图。
///
/// 像素格式固定按 32bpp BGRA 处理（与 <see cref="PixelFormat.Bgra8888"/> 一致）。
/// 源图若是别的格式（Rgb565、Gray8 等），拷出来的字节布局对不上，颜色会错 ——
/// 调用方应当传解码出来的壁纸/截图位图，它们都是 Bgra8888。
/// 失败（尺寸非法、锁定失败、内存不足）返回 null。
/// </summary>
public static class ImageBlur
{
    /// <summary>
    /// 返回一张和 <paramref name="source"/> 同尺寸的模糊副本，alpha 通道原样保留。
    /// <paramref name="radius"/> 按像素算：小于 1 等于不模糊（仍返回副本），
    /// 大于 <see cref="StackBlur.MaxRadius"/> 按上限算。
    /// </summary>
    public static WriteableBitmap? Blur(Bitmap source, double radius)
    {
        if (source is null) return null;

        var size = source.PixelSize;
        var width = size.Width;
        var height = size.Height;
        // StackBlur 对小于 2 的边本来就什么都不做，这里提前挡掉，省一次完整拷贝
        if (width < 2 || height < 2) return null;

        // 这里不能用 AlphaFormat.Opaque：模糊只动颜色、保留原 alpha，
        // 源图若是半透明（PNG 壁纸），声明成 Opaque 会让透明度语义对不上。
        var result = new WriteableBitmap(size, new Vector(96, 96),
            PixelFormat.Bgra8888, AlphaFormat.Premul);

        try
        {
            using (var frame = result.Lock())
            {
                // 直接把源图拷进目标位图的 framebuffer（Avalonia 负责格式转换），
                // 再从 framebuffer 倒成连续数组：位图每行可能带对齐 padding，必须逐行拷。
                source.CopyPixels(frame, AlphaFormat.Premul);

                var pixels = ReadRows(frame, width, height);

                var blurRadius = Math.Clamp((int)Math.Round(radius), 0, StackBlur.MaxRadius);
                StackBlur.Apply(pixels, width, height, blurRadius);

                WriteRows(frame, pixels, width, height);
            }

            return result;
        }
        catch
        {
            // 大图（4K）可能因为内存不足失败：调用方退回清晰图即可，别把大屏拖垮
            result.Dispose();
            return null;
        }
    }

    /// <summary>framebuffer → 连续 uint[]（0xAARRGGBB）。按 RowBytes 逐行拷，忽略行尾 padding。</summary>
    private static unsafe uint[] ReadRows(ILockedFramebuffer frame, int width, int height)
    {
        var pixels = new uint[checked(width * height)];
        var stride = frame.RowBytes;
        var rowBytes = (long)width * 4;
        var origin = (byte*)frame.Address;

        fixed (uint* destination = pixels)
        {
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(origin + (long)y * stride, destination + (long)y * width,
                    rowBytes, rowBytes);
        }

        return pixels;
    }

    /// <summary>连续 uint[] → framebuffer，同样按 RowBytes 逐行写，保证不过界。</summary>
    private static unsafe void WriteRows(ILockedFramebuffer frame, uint[] pixels, int width, int height)
    {
        var stride = frame.RowBytes;
        var rowBytes = Math.Min((long)stride, (long)width * 4);
        var destination = (byte*)frame.Address;

        fixed (uint* source = pixels)
        {
            for (var y = 0; y < height; y++)
                Buffer.MemoryCopy(source + (long)y * width, destination + (long)y * stride,
                    stride, rowBytes);
        }
    }
}
