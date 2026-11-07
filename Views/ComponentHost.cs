using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Presenters;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using EveningSelfStudyClock.Models;

namespace EveningSelfStudyClock.Views;

/// <summary>
/// 一个「大屏组件」的通用外壳。
///
/// 大屏上每个能单独调整的元素（时钟、日期、天气、音量条……）都包一层这个控件，
/// 它按 <see cref="PluginSettings.GetComponentLayout"/> 里该组件的那一份
/// <see cref="ComponentLayout"/> 在自己身上叠加：
/// - 缩放（RenderTransform，不参与布局，所以拉大拉小不会把别的组件挤走）；
/// - 水平/垂直位移（同样是 RenderTransform，单位是大屏宽/高的百分比）。
///
/// 显不显示、排在分区里的第几位由 <see cref="ClockSurface"/> 统一处理（那两件事要读 VM 的
/// 显示条件、还要动兄弟控件的顺序，放在外壳里够不着），这里只管「画在哪、多大」。
///
/// 为什么用 RenderTransform 而不是改 Margin/LayoutTransform：这面墙是绝对定位的叠层，
/// 改布局会把邻居顶走；而 RenderTransform 只影响画在哪，自然布局（时钟/音量条那套上下限位）
/// 一个字节都不用动，默认值（1 / 0 / 0）下变换是单位矩阵，和没有这层壳完全一致。
/// 代价：玻璃是从背景层按各控件布局矩形整层画的，缩放后的组件玻璃不会跟着放大——
/// 这一点按用户要求先记着，后续单独修。
/// </summary>
public class ComponentHost : ContentControl
{
    /// <summary>对应的组件 id（见 <see cref="ComponentCatalog"/>），决定读哪一份布局。</summary>
    public static readonly StyledProperty<string> ComponentIdProperty =
        AvaloniaProperty.Register<ComponentHost, string>(nameof(ComponentId), string.Empty);

    private ComponentLayout? _layout;
    private ClockSurface? _surface;
    private bool _hooked;

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

    /// <summary>模板只有一层内容：外框/底色那两个开关已经按用户要求拿掉了。</summary>
    private static Control CreateTemplate(ComponentHost host, INameScope scope)
    {
        var presenter = new ContentPresenter { Name = "PART_ContentPresenter" };
        presenter[!ContentPresenter.ContentProperty] = host[!ContentProperty];
        presenter[!ContentPresenter.HorizontalContentAlignmentProperty] =
            host[!HorizontalContentAlignmentProperty];
        presenter[!ContentPresenter.VerticalContentAlignmentProperty] =
            host[!VerticalContentAlignmentProperty];
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

        _layout = settings.GetComponentLayout(ComponentId);
        _layout.PropertyChanged += OnLayoutPropertyChanged;
        _hooked = true;
        Apply();
    }

    private void UnhookLayout()
    {
        if (_layout is not null) _layout.PropertyChanged -= OnLayoutPropertyChanged;
        _layout = null;
        _hooked = false;
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e) => Apply();

    private void OnSurfaceSizeChanged(object? sender, SizeChangedEventArgs e) => Apply();

    /// <summary>把当前布局叠到这一层壳上（默认值 = 单位矩阵，与改造前逐像素一致）。</summary>
    private void Apply()
    {
        if (_layout is not { } layout) return;

        // 不透明度不是变换，单独作用在整块壳上；默认 1 = 等于没有这一层。
        Opacity = layout.Opacity;

        var surfaceWidth = _surface?.Bounds.Width ?? 0;
        var surfaceHeight = _surface?.Bounds.Height ?? 0;
        var scale = layout.Scale;
        var dx = layout.OffsetX / 100.0 * surfaceWidth;
        var dy = layout.OffsetY / 100.0 * surfaceHeight;

        if (Math.Abs(scale - 1.0) < 1e-6 && Math.Abs(dx) < 0.01 && Math.Abs(dy) < 0.01)
        {
            RenderTransform = null;
            return;
        }

        // 先按中心缩放，再按屏幕百分比平移（Avalonia 行向量约定：左乘先作用）。
        RenderTransform = new MatrixTransform(
            Matrix.CreateScale(scale, scale) * Matrix.CreateTranslation(dx, dy));
    }
}
