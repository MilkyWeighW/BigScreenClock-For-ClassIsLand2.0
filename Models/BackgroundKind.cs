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
}
