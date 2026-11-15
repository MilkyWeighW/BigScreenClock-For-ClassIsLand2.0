namespace LargeScreenClockRevived.Models;

/// <summary>
/// 大屏背景类型。取值与 <see cref="PluginSettings.BackgroundKind"/> 的整数值一致。
///
/// 三种类型背后是同一层 <c>Views.BackgroundLayer</c>，只是它往上铺的东西不同：
/// 壁纸 = 当前桌面壁纸（景深放大 + 模糊），纯色 = 一条底色，自定义图片 = 用户导入的那张图。
/// </summary>
public enum BackgroundKind
{
    /// <summary>提取当前桌面壁纸（默认）。动态壁纸也按它的静态图处理。</summary>
    Wallpaper = 0,

    /// <summary>纯色背景，颜色取 <see cref="PluginSettings.BackgroundColor"/>。</summary>
    SolidColor = 1,

    /// <summary>自定义图片，文件是 <see cref="PluginSettings.BackgroundImageFile"/>。</summary>
    CustomImage = 2,

    /// <summary>
    /// 实时模糊：不画自己的底，而是让 Windows 原生把窗口后面的画面实时模糊掉
    /// （DWM 的 blur-behind / acrylic）。窗口内容保持透明，组件直接叠在这层模糊之上。
    /// </summary>
    NativeBlur = 3,

    /// <summary>
    /// 网页背景：用 Edge WebView2 在独立的下层窗口里显示 <see cref="PluginSettings.BackgroundWebUrl"/>，
    /// 大屏窗口保持透明，组件叠在网页之上。WebView2 运行时不在了就自动退回纯色。
    /// </summary>
    WebPage = 4,
}
