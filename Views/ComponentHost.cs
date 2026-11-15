using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LargeScreenClockRevived.Models;

namespace LargeScreenClockRevived.Views;

/// <summary>
/// 一个「大屏组件」的通用外壳。
///
/// 大屏上每个能单独调整的元素（时钟、日期、天气、音量条……）都包一层这个控件，
/// 它按 <see cref="PluginSettings.GetComponentLayout"/> 里该组件的那一份
/// <see cref="ComponentLayout"/> 在自己身上叠加：
/// - 缩放（RenderTransform，不参与布局，所以拉大拉小不会把别的组件挤走）；
/// - 水平/垂直位移（同样是 RenderTransform，单位是大屏宽/高的百分比）；
/// - 1.4.0.0 起再加两项：非等比拉伸（<see cref="ComponentLayout.StretchX"/>/<see cref="ComponentLayout.StretchY"/>，
///   即「长宽比」）与字体（族/字号/字重）、文字材质（磨砂 / 液态玻璃）。
///
/// 显不显示、排在分区里的第几位由 <see cref="ClockSurface"/> 统一处理（那两件事要读 VM 的
/// 显示条件、还要动兄弟控件的顺序，放在外壳里够不着），这里只管「画在哪、多大、字长什么样」。
///
/// 为什么用 RenderTransform 而不是改 Margin/LayoutTransform：这面墙是绝对定位的叠层，
/// 改布局会把邻居顶走；而 RenderTransform 只影响画在哪，自然布局（时钟/音量条那套上下限位）
/// 一个字节都不用动，默认值（1 / 0 / 0 / 1:1 / 无字体材质）下变换是单位矩阵、字体一个都不改、
/// 材质层根本不创建 —— 和 1.3.1.1 逐像素一致。
/// </summary>
public class ComponentHost : ContentControl
{
    /// <summary>对应的组件 id（见 <see cref="ComponentCatalog"/>），决定读哪一份布局。</summary>
    public static readonly StyledProperty<string> ComponentIdProperty =
        AvaloniaProperty.Register<ComponentHost, string>(nameof(ComponentId), string.Empty);

    private ComponentLayout? _layout;
    private ClockSurface? _surface;
    private bool _hooked;

    /// <summary>挂着的设置对象（用来听「一键重置把整张布局字典换掉」这种结构性变更）。</summary>
    private PluginSettings? _settings;

    /// <summary>内容呈现器：遍历里面的文字块、套字体与文字材质都以它为根。</summary>
    private ContentPresenter? _presenter;

    /// <summary>当前挂着的文字材质层；没有材质时为 null（无材质零开销）。</summary>
    private TextMaterialLayer? _materialLayer;

    /// <summary>
    /// 已经改过的文字块 → 它们被改之前的原始字体值。
    /// 记下来是为了「把字体改回默认」时能精确还原：绑定的（时钟字号/字体、课程信息字号……）
    /// 用 SetCurrentValue 只改当前值、不断绑定，还原后绑定照常工作。
    /// </summary>
    private readonly Dictionary<TextBlock, FontSnapshot> _textFonts = new();

    /// <summary>文字字体监听是否已挂上（只在真的设了字体时挂，默认值下不增加每帧成本）。</summary>
    private bool _fontsHooked;

    public ComponentHost()
    {
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        RenderTransformOrigin = RelativePoint.Center;
        Template = new FuncControlTemplate<ComponentHost>(CreateTemplate);
    }

    public string ComponentId
    {
        get => GetValue(ComponentIdProperty);
        set => SetValue(ComponentIdProperty, value);
    }

    /// <summary>当前挂着的文字材质层（null = 没有材质；调试/截图时可据此判断走了哪条路径）。</summary>
    public TextMaterialLayer? MaterialLayer => _materialLayer;

    /// <summary>
    /// 模板只有一层内容呈现器。
    /// 文字材质不在这里加任何控件：它是直接改文字字形填充（Foreground/Effect）的，不需要视觉节点，
    /// 更不会在文字背后垫一块外框薄片。
    /// </summary>
    private static Control CreateTemplate(ComponentHost host, INameScope scope)
    {
        var presenter = new ContentPresenter { Name = "PART_ContentPresenter" };
        presenter[!ContentPresenter.ContentProperty] = host[!ContentProperty];
        presenter[!ContentPresenter.HorizontalContentAlignmentProperty] =
            host[!HorizontalContentAlignmentProperty];
        presenter[!ContentPresenter.VerticalContentAlignmentProperty] =
            host[!VerticalContentAlignmentProperty];

        host._presenter = presenter;
        return presenter;
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        // 位移是「大屏尺寸的百分比」，所以要拿到所在 ClockSurface 的实测尺寸；
        // 预览那一份也走同一条路（预览的 ClockSurface 尺寸就是预览框的尺寸）。
        _surface = this.FindAncestorOfType<ClockSurface>();
        if (_surface is not null) _surface.SizeChanged += OnSurfaceSizeChanged;

        HookLayout();
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_surface is not null)
        {
            _surface.SizeChanged -= OnSurfaceSizeChanged;
            _surface = null;
        }

        UnhookLayout();
    }

    private void HookLayout()
    {
        if (_hooked) return;
        if (string.IsNullOrEmpty(ComponentId)) return;
        if (Plugin.Settings is not { } settings) return;

        _settings = settings;
        settings.PropertyChanged -= OnSettingsPropertyChanged;
        settings.PropertyChanged += OnSettingsPropertyChanged;

        _layout = settings.GetComponentLayout(ComponentId);
        _layout.PropertyChanged += OnLayoutPropertyChanged;
        _hooked = true;
        Apply();
    }

    private void UnhookLayout()
    {
        if (_settings is not null)
            _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _settings = null;

        if (_layout is not null) _layout.PropertyChanged -= OnLayoutPropertyChanged;
        _layout = null;
        _hooked = false;

        RestoreTextFonts();
        RemoveMaterialLayer();
    }

    /// <summary>
    /// 「一键恢复默认设置」会把整张 ComponentLayouts 字典换成新的一份，
    /// 此时挂在旧 ComponentLayout 上的本控件收不到任何通知（外观看着没重置）。
    /// 这里只在字典里这一项的实例真的换了时才重挂；普通改值（同一实例）不触发重挂，
    /// 因为 <see cref="PluginSettings"/> 也会用同名属性通知冒泡每一次组件改动。
    /// </summary>
    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(PluginSettings.ComponentLayouts)) return;
        if (_settings is not { } settings || string.IsNullOrEmpty(ComponentId)) return;
        if (ReferenceEquals(settings.GetComponentLayout(ComponentId), _layout)) return;

        UnhookLayout();
        HookLayout();
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e) => Apply();

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => Apply();

    /// <summary>把当前布局叠到这一层壳上（默认值 = 单位矩阵、不动字体、无材质，与改造前逐像素一致）。</summary>
    private void Apply()
    {
        if (_layout is not { } layout) return;

        // 模板可能还没应用（OnAttachedToVisualTree 早于模板套用），此时 _root/_presenter 都是 null，
        // 文字材质会被误判成「没地方挂」。先逼一次模板，保证第一次 Apply 就能把材质层建出来。
        ApplyTemplate();

        // 不透明度不是变换，单独作用在整块壳上；默认 1 = 等于没有这一层。
        Opacity = layout.Opacity;

        ApplyTransform(layout);
        ApplyTextFonts(layout);
        ApplyTextMaterial(layout);
    }

    /// <summary>
    /// 缩放 + 长宽比 + 位移合成一个矩阵。三者都以**组件中心**为原点（RenderTransformOrigin = Center），
    /// 所以默认值（1,1,1,1 / 0,0）拼出来就是单位矩阵，与没有这一层完全一致。
    /// 位移仍按屏幕百分比算，换分辨率也还是同一个相对位置。
    /// </summary>
    private void ApplyTransform(ComponentLayout layout)
    {
        var surfaceWidth = _surface?.Bounds.Width ?? 0;
        var surfaceHeight = _surface?.Bounds.Height ?? 0;

        var scaleX = layout.Scale * layout.StretchX;
        var scaleY = layout.Scale * layout.StretchY;
        var dx = layout.OffsetX / 100.0 * surfaceWidth;
        var dy = layout.OffsetY / 100.0 * surfaceHeight;

        if (Math.Abs(scaleX - 1.0) < 1e-6 && Math.Abs(scaleY - 1.0) < 1e-6
            && Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            RenderTransform = null;
            return;
        }

        // 先按中心缩放/拉伸，再按屏幕百分比平移（Avalonia 行向量约定：左乘先作用）。
        RenderTransform = new MatrixTransform(
            Matrix.CreateScale(scaleX, scaleY) * Matrix.CreateTranslation(dx, dy));
    }

    // ===== 字体 =====

    /// <summary>
    /// 把长宽比之外的三项字体设置作用到组件里的每一段文字。
    /// 默认值（空字体 / 字号 0 / 字重 0）时**一个文字块都不碰**，并还原之前改过的那些。
    /// </summary>
    private void ApplyTextFonts(ComponentLayout layout)
    {
        var useFamily = !string.IsNullOrWhiteSpace(layout.FontFamily);
        var useSize = layout.FontSize > 0;
        var useWeight = layout.FontWeight > 0;

        if (!useFamily && !useSize && !useWeight)
        {
            RestoreTextFonts();
            return;
        }

        HookFonts();
        ApplyTextFontsToTree(layout);
    }

    private void HookFonts()
    {
        if (_fontsHooked) return;
        _fontsHooked = true;
        LayoutUpdated += OnLayoutUpdatedForFonts;
    }

    /// <summary>
    /// 文字块是数据模板里现生现灭的（计数片段、音量五档、预警条目……），
    /// 所以布局一跑完就补一次：新出现的文字块照样能套上字体，已经套过的不会重复记快照。
    /// </summary>
    private void OnLayoutUpdatedForFonts(object? sender, EventArgs e)
    {
        if (_layout is not { } layout) return;
        ApplyTextFontsToTree(layout);
    }

    private void ApplyTextFontsToTree(ComponentLayout layout)
    {
        if (_presenter is null) return;
        if (!_fontsHooked) return;

        var texts = _presenter.GetVisualDescendants().OfType<TextBlock>().ToList();

        // 先摘掉已经不在树上的文字块：它们的快照留着会攥住旧控件（数据模板滚动/重建时）
        if (_textFonts.Count > 0)
        {
            foreach (var stale in _textFonts.Keys.Where(t => !t.IsAttachedToVisualTree()).ToList())
                _textFonts.Remove(stale);
        }

        foreach (var text in texts)
        {
            if (!_textFonts.ContainsKey(text))
            {
                _textFonts[text] = new FontSnapshot(
                    text.GetValue(TextBlock.FontFamilyProperty),
                    text.GetValue(TextBlock.FontSizeProperty),
                    text.GetValue(TextBlock.FontWeightProperty));
            }

            if (!string.IsNullOrWhiteSpace(layout.FontFamily))
            {
                try
                {
                    var family = FontFamily.Parse(layout.FontFamily);
                    text.SetCurrentValue(TextBlock.FontFamilyProperty, family);
                }
                catch
                {
                    // 字体名写错不该让组件崩：保持默认字体
                }
            }

            if (layout.FontSize > 0)
            {
                // SetCurrentValue 只改当前生效值、不断开绑定：
                // 时钟字号、课程信息字号这类仍有自己的绑定（用户调大屏字号时照常生效）
                text.SetCurrentValue(TextBlock.FontSizeProperty, layout.FontSize);
            }

            if (layout.FontWeight > 0)
                text.SetCurrentValue(TextBlock.FontWeightProperty, ToFontWeight(layout.FontWeight));
        }
    }

    /// <summary>把 100~900 的整数映射到 Avalonia 的具名字重；范围已经被 <see cref="ComponentLayout"/> 钳过。</summary>
    private static FontWeight ToFontWeight(int weight)
    {
        if (weight >= 900) return FontWeight.Black;
        if (weight >= 800) return FontWeight.ExtraBold;
        if (weight >= 700) return FontWeight.Bold;
        if (weight >= 600) return FontWeight.SemiBold;
        if (weight >= 500) return FontWeight.Medium;
        if (weight >= 400) return FontWeight.Normal;
        if (weight >= 300) return FontWeight.Light;
        return FontWeight.ExtraLight;
    }

    /// <summary>把改过的文字块还原成被改之前的样子（字体改回默认、或组件离开可视树时调用）。</summary>
    private void RestoreTextFonts()
    {
        if (_fontsHooked)
        {
            LayoutUpdated -= OnLayoutUpdatedForFonts;
            _fontsHooked = false;
        }

        if (_textFonts.Count == 0) return;

        foreach (var (text, snapshot) in _textFonts)
        {
            // 用 SetCurrentValue 还原：绑定的文字块（时钟/课程/地震）不会因为还原而丢绑定
            try { text.SetCurrentValue(TextBlock.FontFamilyProperty, snapshot.FontFamily); } catch { /* 控件已销毁 */ }
            try { text.SetCurrentValue(TextBlock.FontSizeProperty, snapshot.FontSize); } catch { /* 控件已销毁 */ }
            try { text.SetCurrentValue(TextBlock.FontWeightProperty, snapshot.FontWeight); } catch { /* 控件已销毁 */ }
        }

        _textFonts.Clear();
    }

    // ===== 文字材质 =====

    /// <summary>
    /// 按 <see cref="ComponentLayout.TextMaterial"/> 给组件里的文字套「字形材质」。
    /// <see cref="TextMaterialLayer"/> 不是控件、不加任何视觉节点：它直接改每块文字的
    /// Foreground（半透明渐变）与 Effect（边缘模糊/投影），所以文字本身像磨砂/液态玻璃，
    /// 而不是在背后垫一块玻璃。无材质时连这个对象都不建，开销为零。
    /// </summary>
    private void ApplyTextMaterial(ComponentLayout layout)
    {
        var mode = (TextMaterialMode)layout.TextMaterial;
        if (mode == TextMaterialMode.None || _presenter is null)
        {
            RemoveMaterialLayer();
            return;
        }

        _materialLayer ??= new TextMaterialLayer();
        _materialLayer.Mode = mode;
        _materialLayer.AttachSource(_presenter);
    }

    private void RemoveMaterialLayer()
    {
        if (_materialLayer is null) return;
        _materialLayer.DetachSource();
        _materialLayer = null;
    }

    /// <summary>文字块被改之前的字体值（还原用）。</summary>
    private readonly record struct FontSnapshot(FontFamily FontFamily, double FontSize, FontWeight FontWeight);
}
