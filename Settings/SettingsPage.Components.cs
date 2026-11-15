// ===== SettingsPage 拆分之一：组件页签（组件条 / 启用与排序 / 每个组件的大小与位置）=====
// 职责：仿 ClassIsland「主界面 > 组件」的交互——
//       顶部一条横向组件条，只列「已启用」的组件，点谁就配谁；
//       右边的「＋ 添加组件」把关掉的组件加回来；
//       条下面显示选中组件的大小 / 水平位置 / 垂直位置 / 顺序。
//       数值本身不在这里：真正的设置在 PluginSettings.ComponentLayouts（id → ComponentLayout），
//       大屏与预览读的是同一份，所以设置页拖滑块时两边一起动。

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using ClassIsland.Core.Controls;
using LargeScreenClockRevived.Models;
using FluentAvalonia.UI.Controls;

namespace LargeScreenClockRevived.Settings;

public partial class SettingsPage
{
    /// <summary>
    /// 「组件」页签里的全部静态组件（13 个，顺序 = <see cref="ComponentCatalog"/> 清单顺序；贴纸实例另外追加）。
    /// 页面每次打开都新建，行也是一次性建好；之后只有 Layout 里的属性会变。
    /// </summary>
    public ObservableCollection<ComponentSettingItem> ComponentItems { get; } = new();

    /// <summary>组件条上显示的组件：只含已启用的，按用户在设置里排好的顺序。</summary>
    public ObservableCollection<ComponentSettingItem> EnabledItems { get; } = new();

    /// <summary>「＋ 添加组件」里列出的组件：当前关掉的那些。</summary>
    public ObservableCollection<ComponentSettingItem> DisabledItems { get; } = new();

    private ComponentSettingItem? _selectedComponent;

    /// <summary>组件条上选中的组件；条下面那块设置只显示它的。</summary>
    public ComponentSettingItem? SelectedComponent
    {
        get => _selectedComponent;
        set
        {
            if (ReferenceEquals(_selectedComponent, value)) return;
            _selectedComponent = value;
            OnPropertyChanged(nameof(SelectedComponent));
            OnPropertyChanged(nameof(HasSelectedComponent));
            OnPropertyChanged(nameof(SelectedOrderText));
            RaiseSelectionFlags();
        }
    }

    /// <summary>
    /// 选中项是不是某一个组件。设置页里「只属于这个组件」的那些行用它控制显隐
    /// （字号、颜色、精确打铃、摄像头列表……），这样它们能直接长在组件设置里，
    /// 而不必在「外观」「显示内容」里重复一份。
    /// </summary>
    public bool IsClockSelected => IsComponentSelected("clock");
    public bool IsDateSelected => IsComponentSelected("date");
    public bool IsWeatherSelected => IsComponentSelected("weather");
    public bool IsRainSelected => IsComponentSelected("rainMerge");
    public bool IsCountdownSelected => IsComponentSelected("countdownMerge");
    public bool IsTextSelected => IsComponentSelected("textMerge");
    public bool IsAlertDetailSelected => IsComponentSelected("alertDetail");
    public bool IsReminderSelected => IsComponentSelected("reminderPanel");
    public bool IsVolumeSelected => IsComponentSelected("volume");
    public bool IsCounterSelected => IsComponentSelected("counter");
    public bool IsCourseSelected => IsComponentSelected("course");
    public bool IsProgressSelected => IsComponentSelected("progress");
    public bool IsEarthquakeSelected => IsComponentSelected("earthquake");
    public bool IsImageSelected => IsComponentSelected("image");

    private bool IsComponentSelected(string componentId) =>
        string.Equals(SelectedComponent?.Descriptor.Id, componentId, StringComparison.Ordinal);

    private void RaiseSelectionFlags()
    {
        OnPropertyChanged(nameof(IsClockSelected));
        OnPropertyChanged(nameof(IsDateSelected));
        OnPropertyChanged(nameof(IsWeatherSelected));
        OnPropertyChanged(nameof(IsRainSelected));
        OnPropertyChanged(nameof(IsCountdownSelected));
        OnPropertyChanged(nameof(IsTextSelected));
        OnPropertyChanged(nameof(IsAlertDetailSelected));
        OnPropertyChanged(nameof(IsReminderSelected));
        OnPropertyChanged(nameof(IsVolumeSelected));
        OnPropertyChanged(nameof(IsCounterSelected));
        OnPropertyChanged(nameof(IsCourseSelected));
        OnPropertyChanged(nameof(IsProgressSelected));
        OnPropertyChanged(nameof(IsEarthquakeSelected));
        OnPropertyChanged(nameof(IsImageSelected));
    }

    /// <summary>有没有选中组件（没有就不显示下面那块设置）。</summary>
    public bool HasSelectedComponent => SelectedComponent is not null;

    /// <summary>组件条是不是空的（一个都没启用时给句提示）。</summary>
    public bool HasEnabledItems => EnabledItems.Count > 0;

    /// <summary>还有没有没启用的组件（「添加组件」里为空时给句提示）。</summary>
    public bool HasDisabledItems => DisabledItems.Count > 0;

    /// <summary>选中组件在组件条里的位置，例如「第 3 个 / 共 5 个」。</summary>
    public string SelectedOrderText
    {
        get
        {
            if (SelectedComponent is null) return "";
            var index = EnabledItems.IndexOf(SelectedComponent);
            return index < 0 ? "" : $"第 {index + 1} 个 / 共 {EnabledItems.Count} 个";
        }
    }

    // ================= 初始化与列表重建 =================

    /// <summary>把组件清单变成可绑定的行；顺手把每个组件的默认值补进设置（老配置升级用）。</summary>
    private void InitializeComponentItems()
    {
        foreach (var item in ComponentItems)
        {
            item.Layout.PropertyChanged -= OnComponentLayoutChanged;
            item.DetachLayout();
        }
        ComponentItems.Clear();

        AddCatalogComponentItems();
        AddStickerComponentItems();

        RebuildLists();

        // Layout 挂在设置对象上、比页面活得久：只在页面挂到可视树上时订阅，收起时摘掉，免得留下页面。
        Loaded += (_, _) =>
        {
            HookComponentLayouts();
            // 贴纸列表变化（面板里增删/换图）→ 组件条跟着重建，新实例立刻出现在这里
            _settings.PropertyChanged -= OnComponentsSettingsChanged;
            _settings.PropertyChanged += OnComponentsSettingsChanged;
        };
        Unloaded += (_, _) =>
        {
            UnhookComponentLayouts();
            _settings.PropertyChanged -= OnComponentsSettingsChanged;
        };
    }

    /// <summary>
    /// 静态组件行，直接照 <see cref="ComponentCatalog"/> 清单来。
    /// 旧版单图「自定义图片」已经从清单里删掉（图片统一由贴纸实例承载），
    /// 所以这里不再需要为它写任何「隐藏/跳过」的特例。
    /// </summary>
    private void AddCatalogComponentItems()
    {
        for (var i = 0; i < ComponentCatalog.All.Count; i++)
        {
            var descriptor = ComponentCatalog.All[i];
            ComponentItems.Add(new ComponentSettingItem(descriptor, _settings.GetComponentLayout(descriptor.Id), i));
        }
    }

    /// <summary>
    /// 贴纸实例行：每个实例在组件条里是一行独立组件（id = image:实例id），
    /// 选中后走的是同一套「大小 / 位置 / 不透明度 / 外观」设置行，所以不用为贴纸另做一套界面。
    /// </summary>
    private void AddStickerComponentItems()
    {
        _settings.EnsureImageInstances();   // 老配置首次打开设置页时兜一次迁移

        for (var i = 0; i < _settings.ImageInstances.Count; i++)
        {
            var instance = _settings.ImageInstances[i];
            var descriptor = new ComponentDescriptor(
                instance.ComponentId,
                "贴纸 " + (i + 1),
                "自选图片（贴纸）。大小、位置、不透明度与其它组件一样调。",
                "\uE9B2");
            ComponentItems.Add(new ComponentSettingItem(
                descriptor,
                _settings.GetComponentLayout(instance.ComponentId),
                ComponentCatalog.All.Count + i));
        }
    }

    /// <summary>贴纸列表变了：只重建贴纸那几行，静态组件行不动（它们的布局对象没变）。</summary>
    private void OnComponentsSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is not nameof(PluginSettings.ImageInstances)) return;
        RefreshStickerComponentItems();
    }

    /// <summary>把贴纸实例行按最新列表重建，并顺手收拾选中项（被删的那个实例不能让设置行悬空）。</summary>
    private void RefreshStickerComponentItems()
    {
        if (_suppressListRebuild) return;

        foreach (var item in ComponentItems.Where(IsStickerItem).ToList())
        {
            item.Layout.PropertyChanged -= OnComponentLayoutChanged;
            item.DetachLayout();
        }
        foreach (var item in ComponentItems.Where(IsStickerItem).ToList())
            ComponentItems.Remove(item);

        AddStickerComponentItems();
        if (_componentLayoutsHooked)
        {
            foreach (var item in ComponentItems.Where(IsStickerItem))
            {
                item.Layout.PropertyChanged += OnComponentLayoutChanged;
                item.AttachLayout();
            }
        }

        RebuildLists();
    }

    private static bool IsStickerItem(ComponentSettingItem item)
        => ComponentIds.TryGetImageInstanceId(item.Descriptor.Id, out _);

    /// <summary>
    /// 一键重置后重建组件行：布局对象整批换成了默认实例，旧行还绑在旧对象上，必须重挂。
    /// 不重复注册 Loaded/Unloaded（那些由 <see cref="InitializeComponentItems"/> 注册一次即可）。
    /// </summary>
    private void RefreshComponentItemsAfterReset()
    {
        foreach (var item in ComponentItems)
        {
            item.Layout.PropertyChanged -= OnComponentLayoutChanged;
            item.DetachLayout();
        }
        ComponentItems.Clear();

        AddCatalogComponentItems();
        AddStickerComponentItems();

        // 新布局对象也要挂上「启停/排序变化」监听；先把旧订阅状态清掉再重挂。
        _componentLayoutsHooked = false;
        RebuildLists();
        HookComponentLayouts();
    }

    private bool _componentLayoutsHooked;
    private void HookComponentLayouts()
    {
        if (_componentLayoutsHooked) return;
        foreach (var item in ComponentItems)
        {
            item.Layout.PropertyChanged += OnComponentLayoutChanged;
            item.AttachLayout();
        }
        _componentLayoutsHooked = true;
    }

    private void UnhookComponentLayouts()
    {
        if (!_componentLayoutsHooked) return;
        foreach (var item in ComponentItems)
        {
            item.Layout.PropertyChanged -= OnComponentLayoutChanged;
            item.DetachLayout();
        }
        _componentLayoutsHooked = false;
    }

    /// <summary>任一组件改了「启用」或「顺序」→ 重排/重建组件条；被关掉的那个如果正被选中，自动挑一个顶上。</summary>
    private void OnComponentLayoutChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressListRebuild) return;
        switch (e.PropertyName)
        {
            case nameof(ComponentLayout.Enabled):
            case nameof(ComponentLayout.Order):
                RebuildLists();
                break;
        }
    }

    private bool _suppressListRebuild;

    /// <summary>
    /// 按 Layout.Order（null = 清单顺序）重排，重建组件条与「添加组件」两个列表，
    /// 并校正选中项：还启用着就保持，被关掉就挑它原来位置附近的那个。
    /// </summary>
    private void RebuildLists()
    {
        var previous = SelectedComponent;

        var ordered = ComponentItems
            .OrderBy(EffectiveOrder)
            .ThenBy(item => item.CatalogIndex)
            .ToList();
        var enabled = ordered.Where(item => item.Layout.Enabled).ToList();

        EnabledItems.Clear();
        foreach (var item in enabled) EnabledItems.Add(item);

        DisabledItems.Clear();
        foreach (var item in ordered.Where(item => !item.Layout.Enabled)) DisabledItems.Add(item);

        OnPropertyChanged(nameof(HasEnabledItems));
        OnPropertyChanged(nameof(HasDisabledItems));

        // 重建集合会让 ListBox 的 SelectedItem 掉成 null（双向绑定会把它写回来），这里统一收口。
        SelectedComponent = previous is not null && enabled.Contains(previous)
            ? previous
            : PickNear(ordered, enabled, previous);
        OnPropertyChanged(nameof(SelectedOrderText));
    }

    /// <summary>选中项被关掉后挑一个顶上：优先它原位置之后的第一个启用项，没有就往前找，再没有就不选。</summary>
    private static ComponentSettingItem? PickNear(
        IReadOnlyList<ComponentSettingItem> ordered,
        IReadOnlyList<ComponentSettingItem> enabled,
        ComponentSettingItem? previous)
    {
        if (enabled.Count == 0) return null;
        if (previous is null) return enabled[0];

        var index = -1;
        for (var i = 0; i < ordered.Count; i++)
            if (ReferenceEquals(ordered[i], previous)) { index = i; break; }
        if (index < 0) return enabled[0];

        for (var i = index; i < ordered.Count; i++)
            if (enabled.Contains(ordered[i])) return ordered[i];
        for (var i = index - 1; i >= 0; i--)
            if (enabled.Contains(ordered[i])) return ordered[i];
        return enabled[0];
    }

    /// <summary>排序键：用户排过就用 Order，没排过就用清单里的位置（老配置全是 null，也不写回）。</summary>
    private static int EffectiveOrder(ComponentSettingItem item) => item.Layout.Order ?? item.CatalogIndex;

    /// <summary>
    /// 把 13 个静态组件的 Order 统一写成 0..12（按当前生效顺序；贴纸实例另有自己的布局）。
    /// 只有用户点箭头挪动时才调用：平时允许 Order 是 null，省得启动就改一遍设置。
    /// </summary>
    private void NormalizeOrders()
    {
        var ordered = ComponentItems
            .OrderBy(EffectiveOrder)
            .ThenBy(item => item.CatalogIndex)
            .ToList();

        _suppressListRebuild = true;
        try
        {
            for (var i = 0; i < ordered.Count; i++)
                ordered[i].Layout.Order = i;
        }
        finally
        {
            _suppressListRebuild = false;
        }
    }

    // ================= 交互：添加 / 恢复默认 / 前移后移 =================

    /// <summary>「＋ 添加组件」列表里点一项：启用它，并选中它。</summary>
    private void AddComponent_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: ComponentSettingItem item }) return;
        item.Layout.Enabled = true;   // 触发 RebuildLists
        SelectedComponent = item;
    }

    /// <summary>「恢复默认」按钮：大小与位置打回默认（启用/顺序不动）。</summary>
    private void ResetComponent_Click(object? sender, RoutedEventArgs e)
    {
        var target = (sender as Control)?.DataContext as ComponentSettingItem ?? SelectedComponent;
        target?.Layout.Reset();
    }

    private void MoveEarlier_Click(object? sender, RoutedEventArgs e) => MoveSelectedComponent(-1);

    private void MoveLater_Click(object? sender, RoutedEventArgs e) => MoveSelectedComponent(+1);

    /// <summary>把选中组件在「已启用」这条队里往前/往后挪一位（只是改 Layout.Order）。</summary>
    private void MoveSelectedComponent(int delta)
    {
        if (SelectedComponent is null) return;

        NormalizeOrders();                 // 先统一成 0..12，null 不参与交换
        var list = EnabledItems.ToList();  // 规范化不改相对顺序，这份顺序仍然有效
        var index = list.FindIndex(item => ReferenceEquals(item, SelectedComponent));
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count) return;

        SwapComponentOrder(list, index, target);
    }

    private void SwapComponentOrder(IReadOnlyList<ComponentSettingItem> enabled, int from, int to)
    {
        if (from == to) return;
        if (from < 0 || to < 0 || from >= enabled.Count || to >= enabled.Count) return;

        var moved = enabled[from];
        var reordered = enabled.ToList();
        reordered.RemoveAt(from);
        reordered.Insert(to, moved);

        _suppressListRebuild = true;
        try
        {
            var order = 0;
            foreach (var item in reordered) item.Layout.Order = order++;
            // 关掉的组件排到后面，保留它们原来彼此的先后，避免下次加回来时位置乱跳。
            foreach (var item in ComponentItems
                         .Where(item => !item.Layout.Enabled)
                         .OrderBy(item => item.Layout.Order ?? EffectiveOrder(item))
                         .ThenBy(item => item.CatalogIndex))
                item.Layout.Order = order++;
        }
        finally
        {
            _suppressListRebuild = false;
        }

        RebuildLists();
        SelectedComponent = moved;
    }


}

/// <summary>
/// 「组件」页签里一行组件的绑定对象。
/// 设置项的读写都直接落在 <see cref="Layout"/> 上（它是 INotifyPropertyChanged 的），
/// 这里除了固定信息（名字、说明、图标）之外，再提供「百分比滑块 + 可键入输入框」用的两套包装属性：
/// <list type="bullet">
/// <item>XxxPercent（double）：滑块绑这个，单位是百分比（缩放 10~200、偏移 -100~100、不透明度 0~100）；</item>
/// <item>XxxPercentText（string）：输入框绑这个，显示规范文本（如「120%」「-35%」），
/// 键入时按不变文化解析，越界自动夹回范围，解析失败则丢弃输入、恢复原文本。</item>
/// </list>
/// Layout 一变就同步通知这两套属性，所以拖滑块时输入框里的数字会跟着走。
/// </summary>
public sealed class ComponentSettingItem : INotifyPropertyChanged
{
    public ComponentSettingItem(ComponentDescriptor descriptor, ComponentLayout layout, int catalogIndex)
    {
        Descriptor = descriptor;
        Layout = layout;
        CatalogIndex = catalogIndex;
        // ClassIsland 的 Fluent 图标源：直接给码点，形状与官方设置页里那些图标一致。
        IconSource = new FluentIconSource(descriptor.Glyph);
        // 布局对象活得比行久：行被重建时要先 DetachLayout()，免得旧行被 Layout 一直挂着。
        AttachLayout();
    }

    /// <summary>组件固定描述（id/名字/说明/图标码点）。</summary>
    public ComponentDescriptor Descriptor { get; }

    /// <summary>这一份就是 PluginSettings 里存的那份实例，改它就是改设置。</summary>
    public ComponentLayout Layout { get; }

    /// <summary>在 <see cref="ComponentCatalog.All"/> 里的位置：Order 为 null 时的默认排序键。</summary>
    public int CatalogIndex { get; }

    /// <summary>行首图标（FluentIconSource，SettingsExpander.IconSource 用）。</summary>
    public IconSource IconSource { get; }

    public string Name => Descriptor.Name;

    public string Description => Descriptor.Description;

    /// <summary>图标码点（组件条里用 IconText 直接给字形）。</summary>
    public string Glyph => Descriptor.Glyph;

    // ================= 百分比包装属性（滑块 + 输入框） =================

    /// <summary>缩放百分比（10~200），滑块绑它。</summary>
    public double ScalePercent
    {
        get => Layout.Scale * 100;
        // 百分比一律落到整 1%（拖动滑块的手感与输入框文本一致，不会出现「文本 137% 实际 137.4%」）。
        set => Layout.Scale = SnapPercent(Math.Clamp(value, ComponentLayout.MinScale * 100, ComponentLayout.MaxScale * 100)) / 100;
    }

    /// <summary>水平偏移百分比（-100~100），直接对应 Layout.OffsetX。</summary>
    public double OffsetXPercent
    {
        get => Layout.OffsetX;
        set => Layout.OffsetX = SnapPercent(Math.Clamp(value, ComponentLayout.MinOffsetPercent, ComponentLayout.MaxOffsetPercent));
    }

    /// <summary>垂直偏移百分比（-100~100），直接对应 Layout.OffsetY。</summary>
    public double OffsetYPercent
    {
        get => Layout.OffsetY;
        set => Layout.OffsetY = SnapPercent(Math.Clamp(value, ComponentLayout.MinOffsetPercent, ComponentLayout.MaxOffsetPercent));
    }

    /// <summary>不透明度百分比（0~100），滑块绑它。</summary>
    public double OpacityPercent
    {
        get => Layout.Opacity * 100;
        set => Layout.Opacity = SnapPercent(Math.Clamp(value, ComponentLayout.MinOpacity * 100, ComponentLayout.MaxOpacity * 100)) / 100;
    }

    /// <summary>缩放的规范文本（输入框绑它）。</summary>
    public string ScalePercentText
    {
        get => FormatPercent(ScalePercent);
        set => ApplyPercentText(value, nameof(ScalePercentText), v => ScalePercent = v);
    }

    /// <summary>水平偏移的规范文本（输入框绑它）。</summary>
    public string OffsetXPercentText
    {
        get => FormatPercent(OffsetXPercent);
        set => ApplyPercentText(value, nameof(OffsetXPercentText), v => OffsetXPercent = v);
    }

    /// <summary>垂直偏移的规范文本（输入框绑它）。</summary>
    public string OffsetYPercentText
    {
        get => FormatPercent(OffsetYPercent);
        set => ApplyPercentText(value, nameof(OffsetYPercentText), v => OffsetYPercent = v);
    }

    /// <summary>不透明度的规范文本（输入框绑它）。</summary>
    public string OpacityPercentText
    {
        get => FormatPercent(OpacityPercent);
        set => ApplyPercentText(value, nameof(OpacityPercentText), v => OpacityPercent = v);
    }

    /// <summary>挂上对 Layout 的监听（重复调用无副作用）。</summary>
    public void AttachLayout()
    {
        Layout.PropertyChanged -= OnLayoutPropertyChanged;
        Layout.PropertyChanged += OnLayoutPropertyChanged;
    }

    /// <summary>摘掉对 Layout 的监听（页面重建组件行/暂时离开时调用，防止旧行被设置对象长期引用）。</summary>
    public void DetachLayout() => Layout.PropertyChanged -= OnLayoutPropertyChanged;

    /// <summary>
    /// 解析失败就只通知一次（让输入框退回原文本）；成功则写入并再通知一次，把文本规范成「120%」这种形式。
    /// </summary>
    private void ApplyPercentText(string? text, string propertyName, Action<double> apply)
    {
        if (!TryParsePercent(text, out var value))
        {
            OnPropertyChanged(propertyName);
            return;
        }

        apply(value);
        OnPropertyChanged(propertyName);
    }

    /// <summary>允许「120」「120%」「120 ％」，一律按不变文化解析（避免小数点随系统语言变成逗号）。</summary>
    private static bool TryParsePercent(string? text, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var trimmed = text.Trim();
        if (trimmed.EndsWith('%') || trimmed.EndsWith('％'))
            trimmed = trimmed[..^1].Trim();

        return double.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out value);
    }

    /// <summary>滑块是连续的，但语义是 1% 一档：写回设置前先取整，显示文本才与实际值一致。</summary>
    private static double SnapPercent(double value) => Math.Round(value, MidpointRounding.AwayFromZero);

    private static string FormatPercent(double value)
    {
        var rounded = Math.Round(value, MidpointRounding.AwayFromZero);
        if (Math.Abs(rounded) < 0.5) rounded = 0; // 避免出现「-0%」
        return rounded.ToString("0", CultureInfo.InvariantCulture) + "%";
    }

    private void OnLayoutPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ComponentLayout.Scale):
                OnPropertyChanged(nameof(ScalePercent));
                OnPropertyChanged(nameof(ScalePercentText));
                break;
            case nameof(ComponentLayout.OffsetX):
                OnPropertyChanged(nameof(OffsetXPercent));
                OnPropertyChanged(nameof(OffsetXPercentText));
                break;
            case nameof(ComponentLayout.OffsetY):
                OnPropertyChanged(nameof(OffsetYPercent));
                OnPropertyChanged(nameof(OffsetYPercentText));
                break;
            case nameof(ComponentLayout.Opacity):
                OnPropertyChanged(nameof(OpacityPercent));
                OnPropertyChanged(nameof(OpacityPercentText));
                break;
            case null:
            case "":
                OnPropertyChanged(nameof(ScalePercent));
                OnPropertyChanged(nameof(ScalePercentText));
                OnPropertyChanged(nameof(OffsetXPercent));
                OnPropertyChanged(nameof(OffsetXPercentText));
                OnPropertyChanged(nameof(OffsetYPercent));
                OnPropertyChanged(nameof(OffsetYPercentText));
                OnPropertyChanged(nameof(OpacityPercent));
                OnPropertyChanged(nameof(OpacityPercentText));
                break;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
