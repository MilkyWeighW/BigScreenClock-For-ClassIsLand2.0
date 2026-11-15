using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.VisualTree;
using LargeScreenClockRevived.Models;

namespace LargeScreenClockRevived.Views;

/// <summary>
/// 组件「文字材质」：直接改文字**字形填充**的观感（前景画刷 + 轻微效果），
/// 不是给文字背后垫一块外框薄片 —— 用户明确不要「背后一块玻璃」，要的是字本身像磨砂玻璃/液态玻璃。
///
/// 为什么这么做：
/// - Avalonia 没有 BackdropFilter，拿不到「文字下面已经画好的背景像素」。真要把背景磨砂等于每帧截屏送
///   D2D/Skia，4K 上几十毫秒一次，大屏扛不住。所以「玻璃感」只能落在文字自身：
///   用半透明渐变做出厚薄与高光、用小半径模糊/投影柔化字形边缘；
/// - DWM 原生透明窗口（<c>BackgroundKind.NativeBlur</c>）仍可以作为底层磨砂 —— 文字半透明，
///   透出来的就是窗口后面被 DWM 模糊过的背景。但**材质本身不吃背景像素**，只是把字做得像玻璃。
///
/// 实现约定：
/// - 本类不是控件、没有视觉节点：由 <see cref="ComponentHost"/> 在组件内容层上调用它；
/// - 对每个 <see cref="TextBlock"/> 快照 (Foreground, Effect)，用 SetCurrentValue 盖上材质画刷与效果
///   （保留绑定：换主题/改颜色时绑定仍会更新，本类检测到再按新颜色重建）；
/// - 原 Foreground 不是纯色的文字块（渐变/图片画刷）跳过，不硬改；
/// - Mode = None / Detach 时按快照恢复；
/// - 画刷按 (材质档位, 基准色) 缓存，布局回调里不 new；效果对象按文字块各建一份（Avalonia 的 Effect
///   实例不跨控件共用），但只在档位/基准色变化时创建，正常刷新不重建。
/// </summary>
public sealed class TextMaterialLayer
{
    /// <summary>磨砂档的字形边缘模糊半径：只柔化边缘，保证小字号仍读得清。</summary>
    private const double FrostedBlurRadius = 0.8;

    /// <summary>液态玻璃档的投影模糊半径：模拟玻璃厚度在字下形成的影子。</summary>
    private const double LiquidShadowBlur = 5;

    /// <summary>液态玻璃档的投影垂直偏移。</summary>
    private const double LiquidShadowOffsetY = 1;

    /// <summary>液态玻璃档投影颜色（半透明黑）。</summary>
    private static readonly Color LiquidShadowColor = Color.FromArgb(0x80, 0x00, 0x00, 0x00);

    /// <summary>基准色 → 渐变画刷（同色文字共用；换主题时新的基准色自然命中另一项）。</summary>
    private readonly Dictionary<(TextMaterialMode Mode, uint Argb), LinearGradientBrush> _brushCache = new();

    /// <summary>已经套过材质的文字块 → 快照与当前应用的画刷（用来判断是否需要重建/还原）。</summary>
    private readonly Dictionary<TextBlock, AppliedText> _applied = new();

    private Visual? _source;
    private TextMaterialMode _mode = TextMaterialMode.None;
    private bool _hooked;

    /// <summary>本类当前套的材质档位（0 无 / 1 磨砂 / 2 液态玻璃）。</summary>
    public TextMaterialMode Mode
    {
        get => _mode;
        set
        {
            if (_mode == value) return;
            // 换档位要先按旧快照还原，再按新档位重新套：快照里记的是原始 Foreground/Effect
            RestoreAll();
            _mode = value;
            ApplyAll();
        }
    }

    /// <summary>
    /// 接上要处理的文字所在的内容根（通常是组件的 ContentPresenter）。
    /// 文字会现生现灭（计数片段、音量五档、预警条目……），所以挂 LayoutUpdated 兜住新出现的文字块；
    /// 布局回调里只做「引用比较 + 必要时重套」，正常刷新不重建画刷。
    /// </summary>
    public void AttachSource(Visual source)
    {
        if (ReferenceEquals(_source, source))
        {
            ApplyAll();
            return;
        }

        DetachSource();
        _source = source;
        if (source is Control control)
        {
            control.LayoutUpdated += OnSourceLayoutUpdated;
            _hooked = true;
        }

        ApplyAll();
    }

    /// <summary>断开内容根并还原所有被改过的文字（组件摘掉材质 / 离开可视树时调用）。</summary>
    public void DetachSource()
    {
        if (_source is Control control && _hooked) control.LayoutUpdated -= OnSourceLayoutUpdated;
        _hooked = false;

        RestoreAll();
        _source = null;
    }

    private void OnSourceLayoutUpdated(object? sender, EventArgs e) => ApplyAll();

    /// <summary>
    /// 遍历内容层里的文字块，该套的套上、该按新颜色重建的重建。
    /// 已经套过且画刷还是我们那一份的，直接跳过（绝大多数刷新都走这条，不产生分配）。
    /// </summary>
    private void ApplyAll()
    {
        if (_mode == TextMaterialMode.None) return;
        if (_source is not { } source) return;

        // 文字块被数据模板销毁后，快照留着会攥住旧控件；不再挂树上的直接丢掉（控件已销毁，不必还原）
        if (_applied.Count > 0)
        {
            foreach (var stale in _applied.Keys.Where(t => !t.IsAttachedToVisualTree()).ToList())
                _applied.Remove(stale);
        }

        foreach (var text in source.GetVisualDescendants().OfType<TextBlock>())
            ApplyToText(text);
    }

    private void ApplyToText(TextBlock text)
    {
        if (_applied.TryGetValue(text, out var state))
        {
            // 画刷还是我们放的那一份 → 没动过，跳过
            if (ReferenceEquals(text.GetValue(TextBlock.ForegroundProperty), state.Brush)) return;

            // 外部改了 Foreground（换主题、改文字颜色、绑定更新）：
            // 还是纯色就以新颜色为基准重建；变成非纯色就还原并放弃这个文字块。
            if (text.GetValue(TextBlock.ForegroundProperty) is ISolidColorBrush { Color: var changed })
            {
                var rebuilt = Build(text, changed);
                SetMaterial(text, rebuilt);
                return;
            }

            Restore(text, state);
            _applied.Remove(text);
            return;
        }

        // 第一次处理：只认纯色文字。渐变/图片画刷做不出「同色系玻璃」，硬改反而更丑，直接跳过。
        if (text.GetValue(TextBlock.ForegroundProperty) is not ISolidColorBrush { Color: var baseColor }) return;

        var applied = Build(text, baseColor);
        SetMaterial(text, applied);
    }

    /// <summary>生成并套上这一份材质，同时记下原始值以便还原。</summary>
    private void SetMaterial(TextBlock text, AppliedText applied)
    {
        try { text.SetCurrentValue(Visual.EffectProperty, applied.Effect); } catch { /* 控件正在销毁 */ }
        try { text.SetCurrentValue(TextBlock.ForegroundProperty, applied.Brush); } catch { /* 控件正在销毁 */ }
        _applied[text] = applied;
    }

    /// <summary>
    /// 按当前档位与基准色造一套「字形材质」：半透明渐变前景 + 边缘模糊/投影效果。
    /// 效果对象每个文字块各一份（Avalonia 里 Effect 实例不跨控件共用），但只在档位/基准色变化时新建。
    /// </summary>
    private AppliedText Build(TextBlock text, Color baseColor)
    {
        var originalForeground = text.GetValue(TextBlock.ForegroundProperty);
        var originalEffect = text.GetValue(Visual.EffectProperty);

        var brush = GetBrush(baseColor);
        IEffect? effect = _mode switch
        {
            // 磨砂字：极轻的模糊只柔化字形边缘，看起来像蒙了一层细砂，又不至于糊掉笔画
            TextMaterialMode.Frosted => new BlurEffect { Radius = FrostedBlurRadius },
            // 液态玻璃字：字下一点半透明投影，像玻璃有厚度；渐变高光负责「表面反光」
            TextMaterialMode.LiquidGlass => new DropShadowEffect
            {
                Color = LiquidShadowColor,
                BlurRadius = LiquidShadowBlur,
                OffsetY = LiquidShadowOffsetY,
            },
            _ => null,
        };

        return new AppliedText(baseColor, brush, effect, originalForeground, originalEffect);
    }

    /// <summary>取（并缓存）以基准色为基调的渐变画刷。</summary>
    private LinearGradientBrush GetBrush(Color baseColor)
    {
        var key = (_mode, baseColor.ToUInt32());
        if (_brushCache.TryGetValue(key, out var cached)) return cached;

        var brush = BuildBrush(baseColor);
        _brushCache[key] = brush;
        return brush;
    }

    private LinearGradientBrush BuildBrush(Color baseColor)
    {
        var stops = new GradientStops();

        if (_mode == TextMaterialMode.LiquidGlass)
        {
            // 液态玻璃：顶部白色高光（像玻璃表面反光）→ 中段原色 → 底部略透，形成厚度感
            stops.Add(new GradientStop(WithAlpha(Colors.White, 0x8C), 0.0));
            stops.Add(new GradientStop(WithAlpha(baseColor, 0xE6), 0.42));
            stops.Add(new GradientStop(WithAlpha(baseColor, 0xB3), 1.0));
        }
        else
        {
            // 磨砂：顶部更实、底部略淡，整体半透明 —— 叠在 DWM 模糊背景上就是磨砂玻璃上的字
            stops.Add(new GradientStop(WithAlpha(baseColor, 0xF2), 0.0));
            stops.Add(new GradientStop(WithAlpha(baseColor, 0xBF), 1.0));
        }

        return new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
            GradientStops = stops,
        };
    }

    private static Color WithAlpha(Color color, byte alpha) => Color.FromArgb(alpha, color.R, color.G, color.B);

    /// <summary>还原所有套过材质的文字块（Mode=None / 换档位 / 断开内容根时）。</summary>
    private void RestoreAll()
    {
        if (_applied.Count == 0) return;

        foreach (var (text, state) in _applied)
            Restore(text, state);

        _applied.Clear();
    }

    /// <summary>把一块文字还原成材质之前的 Foreground / Effect（用 SetCurrentValue，绑定不会丢）。</summary>
    private static void Restore(TextBlock text, AppliedText state)
    {
        try { text.SetCurrentValue(Visual.EffectProperty, state.OriginalEffect); } catch { /* 控件已销毁 */ }
        try { text.SetCurrentValue(TextBlock.ForegroundProperty, state.OriginalForeground); } catch { /* 控件已销毁 */ }
    }

    /// <summary>一块文字被材质改之前的样子 + 当前套上去的画刷（判断是否被外部改过）。</summary>
    private sealed record AppliedText(
        Color BaseColor,
        IBrush Brush,
        IEffect? Effect,
        IBrush? OriginalForeground,
        IEffect? OriginalEffect);
}
