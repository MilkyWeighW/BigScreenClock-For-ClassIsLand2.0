using System;
using System.Runtime.InteropServices;

namespace LargeScreenClock.Services;

/// <summary>
/// 全屏兜底：Avalonia 复用窗口 Hide→Show 之后，<c>WindowState</c> 属性可能还报 FullScreen，
/// 而原生窗口其实被平台还原成了普通窗口——用户看到的就是「全屏大屏变窗口化」。
/// 这里直接问 Win32 要「窗口矩形」与「所在显示器矩形」，对不上就按显示器范围摆回去。
/// </summary>
internal static class NativeFullScreen
{
    private const uint MonitorDefaultToNearest = 2;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;
    private static readonly IntPtr HwndTopmost = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int cx, int cy,
        uint flags);

    /// <summary>窗口是否已经盖住所在显示器的完整范围。查不到时按「已全屏」处理，不去乱动。</summary>
    public static bool CoversMonitor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return true;
        if (!TryGetMonitorRect(hwnd, out var monitor)) return true;
        if (!GetWindowRect(hwnd, out var window)) return true;

        return window.Left <= monitor.Left
               && window.Top <= monitor.Top
               && window.Right >= monitor.Right
               && window.Bottom >= monitor.Bottom;
    }

    /// <summary>把窗口摆回显示器范围（置顶、不抢焦点），成功返回 true。</summary>
    public static bool ForceCoverMonitor(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            if (!TryGetMonitorRect(hwnd, out var monitor)) return false;

            return SetWindowPos(hwnd, HwndTopmost,
                monitor.Left, monitor.Top,
                monitor.Right - monitor.Left, monitor.Bottom - monitor.Top,
                SwpShowWindow | SwpNoActivate);
        }
        catch
        {
            return false;
        }
    }

    private static bool TryGetMonitorRect(IntPtr hwnd, out Rect rect)
    {
        rect = default;

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero) return false;

        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) return false;

        rect = info.Monitor;
        return true;
    }
}
