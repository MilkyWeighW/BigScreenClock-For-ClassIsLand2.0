using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace LargeScreenClockRevived.Models;

/// <summary>
/// 一个「贴纸」实例：自定义图片组件可以有多个实例，每个实例绑定一张图片。
///
/// 实例的稳定 id 同时是它在 <see cref="PluginSettings.ComponentLayouts"/> 里的键
/// （前缀 <see cref="ComponentIds.ImagePrefix"/>），所以每个实例都有自己的
/// 大小 / 位置 / 不透明度 / 字体 / 文字材质，互不影响。
///
/// 图片文件本身仍然复制进插件配置目录（见 <see cref="ComponentImageStore"/>），
/// 这里只记副本文件名——原图被挪走、删掉都不影响显示。
/// </summary>
public sealed class ImageInstance : INotifyPropertyChanged
{
    private string _id = Guid.NewGuid().ToString("N")[..8];
    private string _file = "";
    private int _stretch;

    /// <summary>实例 id（稳定、存盘；只允许小写字母/数字/短横线，追加进组件 id 后做字典键）。</summary>
    public string Id
    {
        get => _id;
        set => Set(ref _id, SanitizeId(value));
    }

    /// <summary>这张贴纸的图片文件名（插件配置目录里的副本；空 = 还没选图，不渲染）。</summary>
    public string File
    {
        get => _file;
        set => Set(ref _file, value ?? "");
    }

    /// <summary>填充方式：0 = 完整显示，1 = 铺满裁切，2 = 拉伸。</summary>
    public int Stretch
    {
        get => _stretch;
        set => Set(ref _stretch, Math.Clamp(value, 0, 2));
    }

    [System.Text.Json.Serialization.JsonIgnore]
    public string ComponentId => ComponentIds.ImagePrefix + Id;

    public ImageInstance Clone() => new() { Id = Id, File = File, Stretch = Stretch };

    private static string SanitizeId(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Guid.NewGuid().ToString("N")[..8];
        var chars = value.Trim().ToLowerInvariant()
            .Where(c => char.IsAsciiLetterOrDigit(c) || c == '-')
            .ToArray();
        return chars.Length == 0 ? Guid.NewGuid().ToString("N")[..8] : new string(chars);
    }

    private void Set<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 组件 id 的约定：静态组件直接用清单里的 id，贴纸/自定义图片实例用 <c>image:&lt;实例id&gt;</c>。
/// 一处定义，设置页、组件壳、布局字典都从这里取，避免三处各写一遍前缀。
/// </summary>
public static class ComponentIds
{
    /// <summary>贴纸实例组件 id 的前缀（完整 id = 前缀 + <see cref="ImageInstance.Id"/>）。</summary>
    public const string ImagePrefix = "image:";

    /// <summary>旧版单张「自定义图片」组件的 id（老配置迁移用）。</summary>
    public const string LegacyImage = "image";

    /// <summary>这个组件 id 是不是贴纸实例，是则返回实例 id。</summary>
    public static bool TryGetImageInstanceId(string? componentId, out string instanceId)
    {
        instanceId = "";
        if (componentId is null) return false;
        if (!componentId.StartsWith(ImagePrefix, StringComparison.Ordinal)) return false;
        instanceId = componentId[ImagePrefix.Length..];
        return instanceId.Length > 0;
    }
}
