using System;
using System.Runtime.InteropServices;

namespace LargeScreenClockRevived.Services;

/// <summary>
/// 纯 Z 序小工具：把一个窗口排到另一个窗口「正下方」。
///
/// 只包了一处需要的行为，独立成类是为了不让 WebBackgroundService 里混进 P/Invoke 细节；
/// 失败（句柄无效、窗口已销毁）不抛异常，调用方继续按 topmost 关系工作即可。
/// </summary>
internal static class NativeWindowOrder
{
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoOwnerZOrder = 0x0200;
    private const uint SwpNoSendChanging = 0x0400;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint flags);

    /// <summary>把 <paramref name="hwnd"/> 插到 <paramref name="insertAfter"/> 之下（Z 序紧邻）。</summary>
    public static void PlaceBehind(IntPtr hwnd, IntPtr insertAfter)
    {
        if (hwnd == IntPtr.Zero || insertAfter == IntPtr.Zero) return;
        try
        {
            SetWindowPos(hwnd, insertAfter, 0, 0, 0, 0,
                SwpNoSize | SwpNoMove | SwpNoActivate | SwpNoOwnerZOrder | SwpNoSendChanging);
        }
        catch
        {
            // Win32 调用失败无副作用
        }
    }
}
