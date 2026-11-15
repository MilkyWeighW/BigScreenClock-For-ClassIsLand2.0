namespace LargeScreenClockRevived.Models;

/// <summary>
/// 组件「文字材质」的三种档位（<see cref="ComponentLayout.TextMaterial"/>）。
///
/// 材质只作用在组件里的文字（以及文字脚下那块贴着文字外框的薄片），
/// 不铺满组件整块渲染范围——所以它改的是「字读不读得清」，不是「组件长什么样」。
///
/// 渲染路径：Windows 上优先走 DWM/合成层的实时模糊；拿不到合成能力时退到
/// D2D/CPU 侧的静态磨砂（见 <c>Views/TextMaterialLayer.cs</c>）。
/// </summary>
public enum TextMaterialMode
{
    /// <summary>不叠材质，文字直接用主题色画在背景上（默认，零开销）。</summary>
    None = 0,

    /// <summary>磨砂：文字下面叠一层半透明的模糊薄片，边缘按圆角收口。</summary>
    Frosted = 1,

    /// <summary>液态玻璃：磨砂薄片 + 边缘高光/折射色散，只覆盖文字外框。</summary>
    LiquidGlass = 2,
}
