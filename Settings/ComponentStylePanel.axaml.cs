using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using LargeScreenClockRevived.Models;

namespace LargeScreenClockRevived.Settings;

/// <summary>
/// 「组件外观」面板：长宽比（StretchX/StretchY）、字体（族/字号/字重）、文字材质。
///
/// 为什么自己找宿主页订阅选中项：这个面板是自包含的 UserControl（由 SettingsPage.axaml 直接摆放），
/// 页面那边只要放了它就能用，不用再写一行接线代码；选中项变化时面板自己跟着换绑到新组件的布局。
///
/// 绑定数据放在 <see cref="ComponentStyleViewModel"/> 里（而不是面板自己身上）：
/// 面板是 AvaloniaObject，自己实现 INotifyPropertyChanged 会和基类那个同名事件打架；
/// 单独一个视图模型更干净，绑定也确定落在视图模型的通知上。
/// </summary>
public partial class ComponentStylePanel : UserControl
{
    private readonly ComponentStyleViewModel _viewModel = new();
    private SettingsPage? _page;

    public ComponentStylePanel()
    {
        InitializeComponent();

        // 只把**内层**绑定上下文指到视图模型：面板整体仍继承设置页的 DataContext，
        // 这样 SettingsPage.axaml 挂在面板元素本身的绑定（如 IsVisible）不会被顶掉。
        PanelRoot.DataContext = _viewModel;
    }

    /// <summary>显示哪个组件的外观。传空表示没有选中项。设置页可以主动调用。</summary>
    public void SelectComponent(string? componentId) => _viewModel.SelectComponent(componentId);

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);

        _page = this.FindAncestorOfType<SettingsPage>();
        if (_page is not null)
        {
            _page.PropertyChanged -= OnPagePropertyChanged;
            _page.PropertyChanged += OnPagePropertyChanged;
        }

        _viewModel.SelectComponent(_page?.SelectedComponent?.Descriptor.Id);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);

        if (_page is not null) _page.PropertyChanged -= OnPagePropertyChanged;
        _page = null;

        _viewModel.SelectComponent(null);
    }

    /// <summary>设置页换了选中组件 → 换绑到新组件的那一份布局。</summary>
    private void OnPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not (nameof(SettingsPage.SelectedComponent) or null or "")) return;
        _viewModel.SelectComponent(_page?.SelectedComponent?.Descriptor.Id);
    }

    private void Reset_Click(object? sender, RoutedEventArgs e) => _viewModel.Reset();
}

/// <summary>
/// 组件外观面板的视图模型：转发到当前选中组件的那一份 <see cref="ComponentLayout"/>。
/// 所有值都直接写进设置对象，所以大屏与设置页预览立刻一起变（两者读同一份）。
/// </summary>
public sealed class ComponentStyleViewModel : INotifyPropertyChanged
{
    /// <summary>字体下拉里表示「跟随大屏默认字体」的那一项（存盘时对应空字符串）。</summary>
    private const string DefaultFontLabel = "默认（跟随大屏）";

    /// <summary>
    /// 常用字体候选。为什么不列全部系统字体：大屏场景只有几台机器，列全会让下拉长得没法用；
    /// 要别的字体可以把这项留空（跟随大屏默认字体），在「外观」里改大屏字体。
    /// </summary>
    private static readonly string[] CommonFonts =
    {
        DefaultFontLabel,
        "Microsoft YaHei", "Microsoft YaHei UI", "SimHei", "SimSun", "KaiTi", "FangSong",
        "DengXian", "Segoe UI", "Arial", "Times New Roman", "Consolas",
    };

    /// <summary>字重下拉的下标 → 实际字重（0 = 默认，不改）。</summary>
    private static readonly int[] FontWeights = { 0, 300, 400, 500, 600, 700, 800, 900 };

    private ComponentLayout? _layout;
    private bool _refreshing;

    public ComponentStyleViewModel()
    {
        FontFamilyOptions = CommonFonts;
    }

    /// <summary>下拉里的字体候选（第一项是「默认（跟随大屏）」）。</summary>
    public IReadOnlyList<string> FontFamilyOptions { get; }

    /// <summary>当前显示哪个组件的布局；null = 没有选中项。</summary>
    public void SelectComponent(string? componentId)
    {
        var layout = string.IsNullOrWhiteSpace(componentId) || Plugin.Settings is not { } settings
            ? null
            : settings.GetComponentLayout(componentId);

        if (ReferenceEquals(_layout, layout)) return;

        if (_layout is not null) _layout.PropertyChanged -= OnLayoutPropertyChanged;
        _layout = layout;
        if (_layout is not null) _layout.PropertyChanged += OnLayoutPropertyChanged;

        RaiseAll();
    }

    /// <summary>「恢复默认外观」：大小/位置/长宽比/字体/材质全部回默认（不动启用与顺序）。</summary>
    public void Reset()
    {
        _layout?.Reset();
        RaiseAll();
    }

    public double StretchX
    {
        get => _layout?.StretchX ?? 1.0;
        set
        {
            if (_layout is null) return;
            var snapped = Math.Round(Math.Clamp(value, ComponentLayout.MinStretch, ComponentLayout.MaxStretch), 2);
            if (Math.Abs(_layout.StretchX - snapped) < 1e-6) return;
            _layout.StretchX = snapped;
            OnPropertyChanged(nameof(StretchXText));
        }
    }

    public string StretchXText => FormatRatio(StretchX);

    public double StretchY
    {
        get => _layout?.StretchY ?? 1.0;
        set
        {
            if (_layout is null) return;
            var snapped = Math.Round(Math.Clamp(value, ComponentLayout.MinStretch, ComponentLayout.MaxStretch), 2);
            if (Math.Abs(_layout.StretchY - snapped) < 1e-6) return;
            _layout.StretchY = snapped;
            OnPropertyChanged(nameof(StretchYText));
        }
    }

    public string StretchYText => FormatRatio(StretchY);

    /// <summary>字体下拉的选中项（「默认（跟随大屏）」对应空字体）。</summary>
    public string FontFamilyOption
    {
        get => string.IsNullOrWhiteSpace(_layout?.FontFamily) ? DefaultFontLabel : _layout!.FontFamily;
        set
        {
            if (_layout is null) return;
            var family = string.Equals(value, DefaultFontLabel, StringComparison.Ordinal) ? "" : (value ?? "");
            if (string.Equals(_layout.FontFamily, family, StringComparison.Ordinal)) return;
            _layout.FontFamily = family;
            OnPropertyChanged(nameof(FontFamilyOption));
        }
    }

    public double ComponentFontSize
    {
        get => _layout?.FontSize ?? 0;
        set
        {
            if (_layout is null) return;
            var snapped = Math.Round(Math.Clamp(value, 0, 400));
            if (Math.Abs(_layout.FontSize - snapped) < 1e-6) return;
            _layout.FontSize = snapped;
            OnPropertyChanged(nameof(ComponentFontSizeText));
        }
    }

    public string ComponentFontSizeText => ComponentFontSize <= 0
        ? "默认"
        : ComponentFontSize.ToString("0", System.Globalization.CultureInfo.InvariantCulture);

    public int FontWeightIndex
    {
        get
        {
            var weight = _layout?.FontWeight ?? 0;
            var index = Array.IndexOf(FontWeights, weight);
            return index < 0 ? 0 : index;
        }
        set
        {
            if (_layout is null) return;
            if (value < 0 || value >= FontWeights.Length) return;
            var weight = FontWeights[value];
            if (_layout.FontWeight == weight) return;
            _layout.FontWeight = weight;
            OnPropertyChanged(nameof(FontWeightIndex));
        }
    }

    /// <summary>文字材质下标（0 无 / 1 磨砂 / 2 液态玻璃）。</summary>
    public int TextMaterialIndex
    {
        get => _layout?.TextMaterial ?? 0;
        set
        {
            if (_layout is null) return;
            var clamped = Math.Clamp(value, ComponentLayout.MinTextMaterial, ComponentLayout.MaxTextMaterial);
            if (_layout.TextMaterial == clamped) return;
            _layout.TextMaterial = clamped;
            OnPropertyChanged(nameof(TextMaterialIndex));
        }
    }

    private static string FormatRatio(double value)
        => value.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_refreshing) return;
        RaiseAll();
    }

    /// <summary>把所有绑定属性通知一遍（换组件 / 恢复默认 / 外部改布局都走这里）。</summary>
    private void RaiseAll()
    {
        _refreshing = true;
        try
        {
            OnPropertyChanged(nameof(StretchX));
            OnPropertyChanged(nameof(StretchXText));
            OnPropertyChanged(nameof(StretchY));
            OnPropertyChanged(nameof(StretchYText));
            OnPropertyChanged(nameof(FontFamilyOption));
            OnPropertyChanged(nameof(ComponentFontSize));
            OnPropertyChanged(nameof(ComponentFontSizeText));
            OnPropertyChanged(nameof(FontWeightIndex));
            OnPropertyChanged(nameof(TextMaterialIndex));
        }
        finally
        {
            _refreshing = false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
