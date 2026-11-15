using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.VisualTree;
using LargeScreenClockRevived.Models;

namespace LargeScreenClockRevived.Views;

/// <summary>
/// 组件「文字材质」层：给组件里的每一段文字垫一块**贴合文字外框**的薄片（磨砂 / 液态玻璃），
/// 而不是把整块组件铺满。
///
/// 为什么不是全屏实时模糊：Avalonia 没有 BackdropFilter，拿不到「这块薄片下面已经画好的像素」，
/// 真要模糊背景只能逐帧截屏送 D2D/Skia，4K 上每次几十毫秒，大屏一秒刷新好几次，根本扛不住。
/// 所以分两条路径：
/// 1. **原生路径（首选）**：薄片只是一层半透明底色 + 描边，真正的「磨砂」交给 Windows 合成器
///    （DWM）去做 —— 全屏窗口整体透明、由 DWM 模糊时，这一层薄片就是一块真正磨砂玻璃。
///    这条路由 Lead 在窗口层接线（<c>BackgroundKind.NativeBlur</c>），本层不参与，也不探测；
/// 2. **静态兜底（永远可用）**：DWM 不可用（Win10 旧版本、透明被关掉、软件渲染）时，用 Skia
///    的半透明层叠 + 轻微 <see cref="BlurEffect"/> 近似出「毛玻璃」的观感：薄片边缘柔化、
///    底色透出一点背景色。半径固定得很小，且只在材质/文字布局变化时重建，不在 Render 里逐帧算。
///
/// 性能约定：
/// - 只有材质档位 &gt; 0 时 <see cref="ComponentHost"/> 才会创建本控件，无材质时零开销；
/// - 画刷、描边、模糊效果都缓存在字段里，<see cref="Render"/> 里只画不 new；
/// - 只有文字外框真的变了才 <see cref="InvalidateVisual"/>。
/// </summary>
public sealed class TextMaterialLayer : Control
{
    /// <summary>薄片比文字外框左右各多出的留白（像素）：薄片要像「垫在字下面的一张纸」，不能贴着字形边。</summary>
    private const double PadX = 9;

    /// <summary>薄片比文字外框上下各多出的留白（像素）。</summary>
    private const double PadY = 4;

    /// <summary>薄片圆角上限：够圆润但不会把短文字（如「晴」）糊成一个圆。</summary>
    private const double MaxCornerRadius = 10;

    /// <summary>磨砂档的模糊半径：只用来柔化薄片边缘，太大就糊成一片。</summary>
    private const double FrostedBlurRadius = 5;

    /// <summary>液态玻璃档的模糊半径：比磨砂更「水」，但同样克制。</summary>
    private const double LiquidBlurRadius = 7;

    private Visual? _source;
    private TextMaterialMode _mode = TextMaterialMode.None;
    private readonly List<Plate> _plates = new();

    // ===== 缓存：亮字（垫深色薄片）与暗字（垫浅色薄片）各一套，切主题时也只重建一次 =====
    private PlateBrushes? _darkPlate;    // 给亮色文字用的深色薄片
    private PlateBrushes? _lightPlate;   // 给暗色文字用的浅色薄片

    /// <summary>本层当前套的材质档位。</summary>
    public TextMaterialMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            _mode = value;
            RebuildEffect();
            _darkPlate = null;
            _lightPlate = null;
            InvalidateVisual();
        }
    }

    public TextMaterialLayer()
    {
        IsHitTestVisible = false;
        IsVisible = false;   // 找到文字外框之前不占地方、不画东西
    }

    /// <summary>
    /// 接上要测量的内容根（通常是组件的 ContentPresenter）。
    /// 只在布局完成（LayoutUpdated）时重新量文字外框：文字位置一变，薄片跟着挪；
    /// 不挂在 Render 里算，免得每帧都做一遍 FormattedText。
    /// </summary>
    public void AttachSource(Visual source)
    {
        if (ReferenceEquals(_source, source))
        {
            UpdatePlates();
            return;
        }

        DetachSource();
        _source = source;
        if (source is Control control) control.LayoutUpdated += OnSourceLayoutUpdated;
        UpdatePlates();
    }

    /// <summary>断开内容根（组件摘掉材质层或离开可视树时调用）。</summary>
    public void DetachSource()
    {
        if (_source is Control control) control.LayoutUpdated -= OnSourceLayoutUpdated;
        _source = null;
        _plates.Clear();
        IsVisible = false;
    }

    private void OnSourceLayoutUpdated(object? sender, EventArgs e) => UpdatePlates();

    /// <summary>
    /// 重新量一遍文字外框。所有 TextBlock 都**按它自己的 FormattedText 外框**取，不取 Bounds：
    /// Bounds 是布局槽，左对齐的一行短字在槽里偏左、槽本身却可能铺满整行，按槽画就会变成一条长条。
    /// </summary>
    private void UpdatePlates()
    {
        if (_source is not { } source)
        {
            return;
        }

        var next = new List<Plate>(_plates.Count);

        foreach (var text in source.GetVisualDescendants().OfType<TextBlock>())
        {
            if (string.IsNullOrWhiteSpace(text.Text)) continue;
            if (!text.IsEffectivelyVisible) continue;

            var bounds = text.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) continue;

            // 文字块相对本层的位置（本层与文字块在同一个 ComponentHost 里，布局变化时这里已经能取到）
            var origin = text.TranslatePoint(default, this);
            if (origin is not { } point) continue;

            var size = MeasureText(text, bounds);
            if (size.Width <= 0 || size.Height <= 0) continue;

            var left = bounds.X + point.X;
            var top = bounds.Y + point.Y;
            left += text.TextAlignment switch
            {
                TextAlignment.Center => (bounds.Width - size.Width) / 2,
                TextAlignment.Right => bounds.Width - size.Width,
                _ => 0,
            };
            top += text.VerticalAlignment switch
            {
                VerticalAlignment.Center => (bounds.Height - size.Height) / 2,
                VerticalAlignment.Bottom => bounds.Height - size.Height,
                _ => 0,
            };

            next.Add(new Plate(left, top, size.Width, size.Height, IsLight(text.Foreground)));
        }

        if (SamePlates(_plates, next))
        {
            return;
        }

        _plates.Clear();
        _plates.AddRange(next);
        IsVisible = _plates.Count > 0;
        InvalidateVisual();
    }

    /// <summary>用 FormattedText 取文字真实宽高；量不出来（字体缺失等）就退回布局槽尺寸，宁可大一点也不要空着。</summary>
    private static Size MeasureText(TextBlock text, Rect bounds)
    {
        try
        {
            var typeface = new Typeface(text.FontFamily, text.FontStyle, text.FontWeight, FontStretch.Normal);
            var formatted = new FormattedText(
                text.Text ?? string.Empty,
                CultureInfo.CurrentCulture,
                text.FlowDirection,
                typeface,
                text.FontSize,
                text.Foreground);

            // 换行的长文本 FormattedText 只按单行量：夹回布局槽，避免薄片横向冲出组件。
            var width = Math.Min(formatted.Width, bounds.Width);
            var height = Math.Min(formatted.Height, bounds.Height);
            return new Size(Math.Max(0, width), Math.Max(0, height));
        }
        catch
        {
            return bounds.Size;
        }
    }

    private static bool SamePlates(List<Plate> current, List<Plate> next)
    {
        if (current.Count != next.Count) return false;
        for (var i = 0; i < current.Count; i++)
        {
            var a = current[i];
            var b = next[i];
            if (a.Light != b.Light) return false;
            if (Math.Abs(a.Left - b.Left) > 0.5) return false;
            if (Math.Abs(a.Top - b.Top) > 0.5) return false;
            if (Math.Abs(a.Width - b.Width) > 0.5) return false;
            if (Math.Abs(a.Height - b.Height) > 0.5) return false;
        }
        return true;
    }

    /// <summary>
    /// 文字是亮色（压在暗背景上）就垫深色薄片，反过来垫浅色薄片：
    /// 薄片的作用是「把字从背景里托出来」，同色系的薄片在明暗两头都读得清。
    /// </summary>
    private static bool IsLight(IBrush? foreground)
    {
        if (foreground is ISolidColorBrush { Color: var color })
            return Luminance(color) > 0.55;
        return true;   // 拿不到颜色（渐变/主题画刷）时按深色主题的浅色字处理
    }

    private static double Luminance(Color color)
        => (0.2126 * color.R + 0.7152 * color.G + 0.0722 * color.B) / 255.0;

    private void RebuildEffect()
    {
        // 静态兜底路径：轻微柔化薄片边缘，近似磨砂玻璃。真正的背景磨砂由 DWM 原生合成负责。
        Effect = _mode switch
        {
            TextMaterialMode.Frosted => new BlurEffect { Radius = FrostedBlurRadius },
            TextMaterialMode.LiquidGlass => new BlurEffect { Radius = LiquidBlurRadius },
            _ => null,
        };
    }

    private PlateBrushes GetBrushes(bool light)
    {
        if (light)
            return _darkPlate ??= BuildBrushes(darkPlate: true);
        return _lightPlate ??= BuildBrushes(darkPlate: false);
    }

    /// <param name="darkPlate">true = 深色薄片（给亮色文字）；false = 浅色薄片（给暗色文字）。</param>
    private PlateBrushes BuildBrushes(bool darkPlate)
    {
        var liquid = _mode == TextMaterialMode.LiquidGlass;
        if (darkPlate)
        {
            return new PlateBrushes(
                Fill: new SolidColorBrush(Color.FromArgb(liquid ? (byte)0x4A : (byte)0x38, 0x0C, 0x10, 0x1A)),
                Stroke: new Pen(new SolidColorBrush(Color.FromArgb(0x50, 0xFF, 0xFF, 0xFF)), 1),
                Highlight: new LinearGradientBrush
                {
                    StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                    EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                    GradientStops =
                    {
                        new GradientStop(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF), 0),
                        new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55),
                    },
                },
                Glow: new Pen(new SolidColorBrush(Color.FromArgb(0x38, 0x9F, 0xE8, 0xFF)), 1.6));
        }

        return new PlateBrushes(
            Fill: new SolidColorBrush(Color.FromArgb(liquid ? (byte)0x5A : (byte)0x46, 0xFF, 0xFF, 0xFF)),
            Stroke: new Pen(new SolidColorBrush(Color.FromArgb(0x40, 0x00, 0x00, 0x00)), 1),
            Highlight: new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
                EndPoint = new RelativePoint(0, 1, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 0),
                    new GradientStop(Color.FromArgb(0x00, 0xFF, 0xFF, 0xFF), 0.55),
                },
            },
            Glow: new Pen(new SolidColorBrush(Color.FromArgb(0x48, 0x6F, 0xB7, 0xE8)), 1.6));
    }

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_plates.Count == 0) return;

        foreach (var plate in _plates)
        {
            var rect = new Rect(
                plate.Left - PadX,
                plate.Top - PadY,
                plate.Width + PadX * 2,
                plate.Height + PadY * 2);
            if (rect.Width <= 0 || rect.Height <= 0) continue;

            var radius = Math.Min(rect.Height / 2, MaxCornerRadius);
            var brushes = GetBrushes(plate.Light);

            if (_mode == TextMaterialMode.Frosted)
            {
                context.DrawRectangle(brushes.Fill, brushes.Stroke, rect, radius, radius);
                continue;
            }

            // 液态玻璃：由外到内三层薄片 —— 外圈色散描边、主体半透明底、顶部高光。
            // 不做逐像素色散（那要自定义 shader / 逐帧截图），三层已经能读出「玻璃」的体积感。
            var outer = rect.Inflate(2);
            context.DrawRectangle(null, brushes.Glow, outer, radius + 1, radius + 1);
            context.DrawRectangle(brushes.Fill, brushes.Stroke, rect, radius, radius);

            // 顶部高光只占薄片上沿一条，避免整块泛白把字吃掉
            var highlightHeight = Math.Min(rect.Height * 0.45, 12);
            var highlight = new Rect(rect.X + 2, rect.Y + 1, Math.Max(0, rect.Width - 4), highlightHeight);
            context.DrawRectangle(brushes.Highlight, null, highlight, radius, radius);
        }
    }

    /// <summary>一套薄片画刷（按文字明暗各建一套，Render 里按每块薄片的明暗挑）。</summary>
    private sealed record PlateBrushes(IBrush Fill, IPen Stroke, IBrush Highlight, IPen Glow);

    /// <summary>一块薄片的几何信息（本层坐标）。</summary>
    private readonly record struct Plate(double Left, double Top, double Width, double Height, bool Light);
}
