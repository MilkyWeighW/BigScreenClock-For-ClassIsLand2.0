using System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace EveningSelfStudyClock.Services;

/// <summary>
/// 让 ClassIsland 的「顶部课表栏」（也就是 ClassIsland 主界面）始终压在大屏时钟上面。
///
/// 背景：大屏时钟是一个全屏置顶（topmost）窗口。ClassIsland 的主界面默认是顶部停靠、
/// 置顶显示的一条窄课表栏——这一条必须保持可见并且排在大屏之上，否则上课时课表就看不见了。
/// 而 ClassIsland 的设置页、托盘菜单之类的窗口不需要被我们主动抬高（用户明确说过那不算）。
///
/// Windows 的规则是「后设置 topmost 的窗口排在先设置的上面」。所以这里只做两件事：
/// 先把大屏时钟置顶，再把 ClassIsland 主界面窗口置顶——于是主界面永远在大屏上面。
/// 任何一步失败都静默忽略，最坏情况只是课表栏暂时被大屏盖住。
/// </summary>
public static class CiTopmostGuard
{
    /// <summary>
    /// 排好 Z 序：大屏时钟在下、ClassIsland 主界面（课表栏）在上。
    /// 大屏显示/重新显示/从最小化恢复时调用，另外窗口可见期间还有定时器周期性重排一次。
    /// </summary>
    /// <param name="clockHwnd">大屏时钟窗口句柄。</param>
    /// <param name="classIslandHwnd">ClassIsland 主界面窗口句柄；拿不到时传 <see cref="IntPtr.Zero"/>（只置顶大屏）。</param>
    public static void Arrange(IntPtr clockHwnd, IntPtr classIslandHwnd)
    {
        // 大屏自己先置顶。失败也继续走后面的提升：课表栏照样压在大屏上面。
        TrySetTopmost(clockHwnd, show: false);

        // 课表栏最后设置，于是排在大屏之上；SWP_SHOWWINDOW 让它即使在显示动画中也保持可见。
        TrySetTopmost(classIslandHwnd, show: true);
    }

    private static void TrySetTopmost(IntPtr hwnd, bool show)
    {
        if (hwnd == IntPtr.Zero) return;

        try
        {
            var flags = SET_WINDOW_POS_FLAGS.SWP_NOMOVE
                        | SET_WINDOW_POS_FLAGS.SWP_NOSIZE
                        | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE;
            if (show) flags |= SET_WINDOW_POS_FLAGS.SWP_SHOWWINDOW;

            PInvoke.SetWindowPos((HWND)hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, flags);
        }
        catch
        {
            // 静默：单个窗口置顶失败不影响另一层。
        }
    }
}
