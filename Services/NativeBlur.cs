using System;
using System.Runtime.InteropServices;

namespace LargeScreenClockRevived.Services;

/// <summary>
/// Windows 原生「窗口背后实时模糊」（DWM blur-behind / acrylic）。
///
/// 为什么自己 P/Invoke 而不是走 CsWin32：SetWindowCompositionAttribute 是未公开 API，
/// 不在 Win32 元数据里，CsWin32 生成不出来；而它又是 Win10/11 上唯一「不自己抓屏」就能
/// 做实时模糊的路子（抓屏 + 每帧模糊的开销对常驻大屏来说不可接受）。
///
/// 约定：这里只负责开关一个窗口的模糊，失败一律返回 false，绝不抛异常——
/// 调用方（大屏窗口 / BackgroundLayer）拿到 false 就当这个功能不存在，退回纯色背景。
/// 也就是说：任何系统版本、任何 DWM 状态下，最坏结果都是「没模糊」，不会是大屏崩掉。
/// </summary>
public static class NativeBlur
{
    /// <summary>WCA_ACCENT_POLICY：SetWindowCompositionAttribute 里唯一被我们用到的一种属性。</summary>
    private const int WcaAccentPolicy = 19;

    // ACCENT_STATE 取值（Win10 1803+ 的公开枚举值，API 本身未公开但取值长期稳定）
    private const int AccentDisabled = 0;
    private const int AccentEnableBlurBehind = 3;
    private const int AccentEnableAcrylicBlurBehind = 4;

    /// <summary>
    /// 「画边框」标志位。为了让模糊只出现在窗口内容里、不在边缘再多一圈描边，
    /// 这里传 0；留这个常量只是说明为什么没有别的标志位。
    /// </summary>
    private const int AccentFlagNone = 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        /// <summary>ABGR 格式的着色（A 在最高 8 位）。0 = 完全跟随系统。</summary>
        public uint GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    /// <summary>未公开 API；找不到入口点时这里会抛 DllNotFoundException，由外层 catch 兜住。</summary>
    [DllImport("user32.dll", SetLastError = true)]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    /// <summary>
    /// 打开（<paramref name="enabled"/> = true）或关掉窗口背后的实时模糊。
    ///
    /// 先试 acrylic（Win10 1803+ 观感最好，带一点磨砂色调），失败再退到普通的 blur-behind
    /// （Win7/老 Win10 也有）。两种都失败就返回 false——调用方据此退回纯色背景。
    /// <paramref name="abgrTint"/> 只在 acrylic 下生效，是 ABGR 顺序，默认 0 表示不额外着色。
    /// </summary>
    public static bool TryEnable(IntPtr hwnd, bool enabled, uint abgrTint = 0x00000000)
    {
        if (hwnd == IntPtr.Zero) return false;

        try
        {
            if (!enabled)
                return Apply(hwnd, AccentDisabled, 0, 0);

            if (Apply(hwnd, AccentEnableAcrylicBlurBehind, AccentFlagNone, abgrTint))
                return true;

            // acrylic 在部分 Win11 版本/远程桌面里不生效，退回老式 blur-behind
            return Apply(hwnd, AccentEnableBlurBehind, AccentFlagNone, abgrTint);
        }
        catch
        {
            // 老系统没有这个入口点、DWM 没运行、结构体不认：都当作「没有原生模糊」
            return false;
        }
    }

    private static bool Apply(IntPtr hwnd, int accentState, int accentFlags, uint gradientColor)
    {
        var policy = new AccentPolicy
        {
            AccentState = accentState,
            AccentFlags = accentFlags,
            GradientColor = gradientColor,
            AnimationId = 0,
        };

        var size = Marshal.SizeOf<AccentPolicy>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WcaAccentPolicy,
                Data = buffer,
                SizeOfData = size,
            };
            // 返回值是 BOOL：0 表示这次设置没被接受
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
