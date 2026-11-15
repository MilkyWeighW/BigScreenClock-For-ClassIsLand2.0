using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LargeScreenClockRevived.Models;

namespace LargeScreenClockRevived.Settings;

/// <summary>
/// 「贴纸」面板：管理 <see cref="PluginSettings.ImageInstances"/>（1.4.0.0 起自定义图片支持多个实例）。
///
/// 只负责「有几张图、每张是哪张、填充方式」；位置/大小/不透明度仍然归「组件」页签（每个实例在那边
/// 是一行独立的「贴纸 N」），这样数据只有一处真相，不会出现面板里改一套、组件页里改另一套。
/// 绑定数据放在 <see cref="StickerPanelViewModel"/> 里，理由同 ComponentStylePanel。
/// </summary>
public partial class StickerPanel : UserControl
{
    private readonly StickerPanelViewModel _viewModel = new();

    public StickerPanel()
    {
        InitializeComponent();

        // 只把内层绑定上下文指到视图模型：面板外层仍继承设置页的 DataContext
        PanelRoot.DataContext = _viewModel;
    }

    protected override void OnAttachedToVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _viewModel.Attach(Plugin.Settings);
    }

    protected override void OnDetachedFromVisualTree(Avalonia.VisualTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromVisualTree(e);
        _viewModel.Detach();
    }

    // ===== 交互（文件选择、确认动作留在 code-behind；数据变更都在视图模型里） =====

    private async void AddSticker_Click(object? sender, RoutedEventArgs e)
    {
        var instance = _viewModel.AddSticker();
        if (instance is not null) await PickImageForAsync(instance);
    }

    private async void PickImage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: StickerRow row }) return;
        await PickImageForAsync(row.Instance);
    }

    private async Task PickImageForAsync(ImageInstance instance)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择贴纸图片",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp", "*.gif"],
                    },
                ],
            });

            if (files.Count == 0) return;
            var source = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;

            _viewModel.SetImage(instance, source);
        }
        catch (Exception ex)
        {
            _viewModel.SetHint("导入失败：" + ex.Message);
        }
    }

    private void ClearImage_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: StickerRow row }) return;
        _viewModel.ClearImage(row.Instance);
    }

    private void DeleteSticker_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control { DataContext: StickerRow row }) return;
        _viewModel.DeleteSticker(row.Instance);
    }

    private void Prune_Click(object? sender, RoutedEventArgs e) => _viewModel.Prune();
}

/// <summary>
/// 贴纸面板的视图模型：包住设置里的 ImageInstances，负责增删/换图/清理与对应的提示文本。
/// 图片仍是复制进配置目录的副本（<see cref="ComponentImageStore"/>），面板只记文件名。
/// </summary>
public sealed class StickerPanelViewModel : INotifyPropertyChanged
{
    private PluginSettings? _settings;
    private string _hint = "";

    /// <summary>已经挂上 PropertyChanged 的实例（从 Settings.json 反序列化出来的实例，设置对象那边不一定订阅过）。</summary>
    private readonly HashSet<ImageInstance> _hookedInstances = new();

    /// <summary>面板里的一行 = 一个贴纸实例。</summary>
    public ObservableCollection<StickerRow> Items { get; } = new();

    public bool IsEmpty => Items.Count == 0;

    public string Hint
    {
        get => _hint;
        private set
        {
            if (_hint == value) return;
            _hint = value;
            OnPropertyChanged();
        }
    }

    /// <summary>挂到设置对象上（面板进可视树时调用）；实例增删/换图都会冒泡成一次 ImageInstances 通知。</summary>
    public void Attach(PluginSettings? settings)
    {
        if (ReferenceEquals(_settings, settings))
        {
            Refresh();
            return;
        }

        Detach();
        _settings = settings;
        if (_settings is null) return;

        _settings.PropertyChanged -= OnSettingsPropertyChanged;
        _settings.PropertyChanged += OnSettingsPropertyChanged;
        _settings.EnsureImageInstances();
        Refresh();
    }

    /// <summary>摘掉对设置的订阅（面板离开可视树时调用）。</summary>
    public void Detach()
    {
        if (_settings is not null) _settings.PropertyChanged -= OnSettingsPropertyChanged;

        foreach (var instance in _hookedInstances) instance.PropertyChanged -= OnInstancePropertyChanged;
        _hookedInstances.Clear();

        _settings = null;
        Items.Clear();
        OnPropertyChanged(nameof(IsEmpty));
    }

    /// <summary>
    /// 让实例监听与列表对齐。实例自己改 File/Stretch 时刷新面板文字；
    /// 不依赖 PluginSettings 的冒泡（反序列化出来的实例不一定被那边订阅）。
    /// </summary>
    private void HookInstances()
    {
        if (_settings is null) return;

        var current = new HashSet<ImageInstance>(_settings.ImageInstances);
        foreach (var instance in _hookedInstances.Where(i => !current.Contains(i)).ToList())
        {
            instance.PropertyChanged -= OnInstancePropertyChanged;
            _hookedInstances.Remove(instance);
        }
        foreach (var instance in current.Where(i => !_hookedInstances.Contains(i)))
        {
            instance.PropertyChanged += OnInstancePropertyChanged;
            _hookedInstances.Add(instance);
        }
    }

    private void OnInstancePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ImageInstance.File)
            or nameof(ImageInstance.Stretch)
            or null
            or "")
        {
            Refresh();
        }
    }

    /// <summary>
    /// 立刻落盘一次。为什么要显式存：实例的 File/Stretch 变化在设置对象那边**不一定**会冒泡
    /// （老配置反序列化出来的实例没被订阅过），漏了这一步用户换的图可能重启后就没了。
    /// SaveSettings 本身是 fire-and-forget，这里用 try 兜住，失败不影响界面。
    /// </summary>
    private static void Persist()
    {
        try { Plugin.SaveSettings(); } catch { /* 存盘失败不影响设置页操作 */ }
    }

    private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginSettings.ImageInstances))
            Refresh();
    }

    /// <summary>
    /// 新增一个空贴纸实例，并把它在大屏上的初始位置错开一点（第一张与 1.3.1.1 单图位置一致）。
    /// 错开只是初始值，用户之后用组件页的「位置偏移」拖到哪都行。
    /// </summary>
    public ImageInstance? AddSticker()
    {
        if (_settings is null) return null;

        try
        {
            var instance = _settings.AddImageInstance();
            var index = _settings.ImageInstances.IndexOf(instance);
            if (index > 0)
            {
                var layout = _settings.GetComponentLayout(instance.ComponentId);
                layout.OffsetY = Math.Min(ComponentLayout.MaxOffsetPercent, index * 12);
            }

            Hint = "已添加，接着选一张图。";
            Refresh();
            return instance;
        }
        catch (Exception ex)
        {
            Hint = "添加失败：" + ex.Message;
            return null;
        }
    }

    /// <summary>给某个实例换图（复制副本进配置目录，原图之后挪走也不影响）。</summary>
    public void SetImage(ImageInstance instance, string sourcePath)
    {
        if (_settings is null) return;

        try
        {
            ComponentImageStore.ReplaceForInstance(sourcePath, instance, _settings);
            Hint = $"已导入：{Path.GetFileName(sourcePath)}";
            Persist();
            Refresh();
        }
        catch (Exception ex)
        {
            Hint = "导入失败：" + ex.Message;
        }
    }

    /// <summary>清掉实例的图（实例还留着，大屏上暂时不显示）。</summary>
    public void ClearImage(ImageInstance instance)
    {
        if (_settings is null) return;

        ComponentImageStore.ClearForInstance(instance, _settings);
        Hint = "已清除这张图（实例还在，大屏上暂时不显示）。";
        Persist();
        Refresh();
    }

    /// <summary>删掉实例：连同它的布局与图片副本一起回收（见 RemoveImageInstance / PruneUnusedInstanceFiles）。</summary>
    public void DeleteSticker(ImageInstance instance)
    {
        if (_settings is null) return;

        _settings.RemoveImageInstance(instance);
        ComponentImageStore.PruneUnusedInstanceFiles(_settings);
        Hint = "已删除贴纸。";
        Persist();
        Refresh();
    }

    /// <summary>清理没有被任何实例引用的图片副本（删实例留下的孤儿文件）。</summary>
    public void Prune()
    {
        if (_settings is null) return;

        ComponentImageStore.PruneUnusedInstanceFiles(_settings);
        Hint = "已清理没有被任何贴纸使用的图片副本。";
    }

    public void SetHint(string hint) => Hint = hint;

    /// <summary>按设置里的实例列表重建行；行很轻（只是一层包装，不复制图片），顺序与设置一致。</summary>
    private void Refresh()
    {
        if (_settings is null) return;

        var instances = _settings.ImageInstances;
        var matched = new List<StickerRow>();
        foreach (var instance in instances)
        {
            var row = Items.FirstOrDefault(r => ReferenceEquals(r.Instance, instance));
            if (row is null) row = new StickerRow(instance);
            else row.Refresh();
            matched.Add(row);
        }

        for (var i = 0; i < matched.Count; i++)
        {
            var index = Items.IndexOf(matched[i]);
            if (index < 0) Items.Insert(i, matched[i]);
            else if (index != i) Items.Move(index, i);
        }
        while (Items.Count > matched.Count) Items.RemoveAt(Items.Count - 1);

        HookInstances();
        OnPropertyChanged(nameof(IsEmpty));
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>贴纸面板里的一行：包装一个 <see cref="ImageInstance"/>，只提供显示用的文本与填充方式。</summary>
public sealed class StickerRow : INotifyPropertyChanged
{
    public StickerRow(ImageInstance instance)
    {
        Instance = instance;
    }

    public ImageInstance Instance { get; }

    /// <summary>大屏组件条里的名字与这里保持一致：都叫「贴纸 N」，序号由组件页按列表顺序给。</summary>
    public string Title => "贴纸";

    public string FileName => string.IsNullOrWhiteSpace(Instance.File)
        ? "未选择图片（大屏上不显示）"
        : Instance.File;

    /// <summary>填充方式：0 完整显示 / 1 铺满裁切 / 2 拉伸（写回实例，大屏立刻跟着变）。</summary>
    public int StretchIndex
    {
        get => Instance.Stretch;
        set
        {
            if (Instance.Stretch == value) return;
            Instance.Stretch = value;
            OnPropertyChanged();
        }
    }

    /// <summary>实例被换图后（面板重建行时）刷一下显示文本。</summary>
    public void Refresh() => OnPropertyChanged(nameof(FileName));

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
