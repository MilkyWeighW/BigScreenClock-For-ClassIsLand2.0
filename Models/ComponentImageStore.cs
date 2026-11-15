using System.IO;

namespace LargeScreenClockRevived.Models;

/// <summary>
/// 「自定义图片」组件所用图片的存放：和背景图一样，把用户选的那张**复制一份**进插件配置目录，
/// 设置里只记副本的文件名，原图之后挪走/删掉都不影响大屏。
///
/// 单独一套前缀（<c>component-image</c>），与背景图的 <c>background-*</c> 互不覆盖：
/// 换组件图片不会顺手把背景图删掉，反过来也一样。
/// </summary>
public static class ComponentImageStore
{
    /// <summary>副本的文件名前缀。</summary>
    private const string BaseName = "component-image";

    /// <summary>设置里记的文件名 → 完整路径；没设或文件已经不在了都返回 null。</summary>
    public static string? ResolvePath(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return null;

        var folder = Plugin.ConfigFolder;
        if (string.IsNullOrWhiteSpace(folder)) return null;

        var path = Path.Combine(folder, fileName);
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 把 <paramref name="sourcePath"/> 复制进配置目录并写进设置；顺手删掉上一张副本。
    /// 文件名带时间戳，保证「同格式换图」也会触发 PropertyChanged（理由同 <see cref="BackgroundImageStore"/>）。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置目录不可用，或文件头认不出图片格式。</exception>
    public static void Replace(string sourcePath, PluginSettings settings)
    {
        var folder = Plugin.ConfigFolder;
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("插件配置目录不可用");

        Directory.CreateDirectory(folder);

        if (ImageHeader.ReadSize(sourcePath) is null)
        {
            throw new InvalidOperationException(
                "不支持的图片格式：只支持 PNG / JPEG / BMP / GIF / WebP。请换一张这些格式的图片（改了扩展名也不行）。");
        }

        var name = BaseName + "-" + DateTime.Now.ToString("yyyyMMddHHmmssfff")
                   + Path.GetExtension(sourcePath).ToLowerInvariant();
        var target = Path.Combine(folder, name);
        var temp = target + ".tmp";

        // 先写临时文件、成功后再换正式名字；绝不先删旧图（理由同背景图：源文件读不了时别把旧的也弄丢）。
        try
        {
            File.Copy(sourcePath, temp, overwrite: true);
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 删不掉就留给下次清理 */ }
            throw;
        }

        settings.CustomImageFile = name;
        RemoveExisting(exceptName: name);
    }

    /// <summary>清掉副本并清空设置（组件在大屏上随之隐藏）。</summary>
    public static void Clear(PluginSettings settings)
    {
        RemoveExisting();
        settings.CustomImageFile = "";
    }

    /// <param name="exceptName">这张别删（刚放进去的那张）。</param>
    private static void RemoveExisting(string? exceptName = null)
    {
        var folder = Plugin.ConfigFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        foreach (var file in Directory.GetFiles(folder, BaseName + "*"))
        {
            var name = Path.GetFileName(file);
            if (!name.StartsWith(BaseName + ".", StringComparison.OrdinalIgnoreCase) &&
                !name.StartsWith(BaseName + "-", StringComparison.OrdinalIgnoreCase)) continue;

            // 贴纸实例的副本（component-image-sticker-*）**不归这里管**：它们由
            // PruneUnusedInstanceFiles 按实例引用情况清理。旧单图的「换图/清除」绝不能顺带
            // 把贴纸的图删了 —— 两条路径共用同一前缀，这里必须先按中缀排除。
            if (name.StartsWith(BaseName + "-sticker-", StringComparison.OrdinalIgnoreCase)) continue;

            if (exceptName is not null && string.Equals(name, exceptName, StringComparison.OrdinalIgnoreCase))
                continue;

            try { File.Delete(file); }
            catch { /* 正被占用就留着，下次换图再删 */ }
        }
    }

    // ===== 1.4.0.0：多贴纸（ImageInstance）=====
    // 旧版只有一张 CustomImageFile，1.4.0.0 起一个实例一张图。下面这几个方法都只动某个实例自己的
    // 副本文件与 instance.File，不碰旧版单图字段：贴纸与旧单图可以同时存在，回退版本时老图还在。

    /// <summary>
    /// 给一个贴纸实例换图：复制进配置目录，写回 <see cref="ImageInstance.File"/>，
    /// 并删掉这个实例**换下来**的那张旧副本（别的实例还引用同一张时保留）。
    /// </summary>
    /// <exception cref="InvalidOperationException">配置目录不可用，或文件头认不出图片格式。</exception>
    public static void ReplaceForInstance(string sourcePath, ImageInstance instance, PluginSettings settings)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var folder = Plugin.ConfigFolder;
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("插件配置目录不可用");

        Directory.CreateDirectory(folder);

        if (ImageHeader.ReadSize(sourcePath) is null)
        {
            throw new InvalidOperationException(
                "不支持的图片格式：只支持 PNG / JPEG / BMP / GIF / WebP。请换一张这些格式的图片（改了扩展名也不行）。");
        }

        // 文件名带 sticker 段 + 时间戳：与旧单图副本区分开，清理时不会互相误删；
        // 也保证「同格式换图」时 File 必然变化（大屏侧据此重新解码）。
        var name = BaseName + "-sticker-" + DateTime.Now.ToString("yyyyMMddHHmmssfff")
                   + Path.GetExtension(sourcePath).ToLowerInvariant();
        var target = Path.Combine(folder, name);
        var temp = target + ".tmp";

        try
        {
            File.Copy(sourcePath, temp, overwrite: true);
            if (File.Exists(target)) File.Delete(target);
            File.Move(temp, target);
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 删不掉就留给下次清理 */ }
            throw;
        }

        var previous = instance.File;
        instance.File = name;

        // 删旧副本前先确认没有别的实例还在用它：同一张图被两个贴纸引用时不至于把别人的图删了
        if (!string.IsNullOrWhiteSpace(previous)
            && !string.Equals(previous, name, StringComparison.OrdinalIgnoreCase)
            && !AnyInstanceUses(settings, previous, except: instance))
        {
            DeleteFile(previous);
        }
    }

    /// <summary>
    /// 清掉一个贴纸实例的图（实例本身还留着，用户可以在设置页重新选图）。
    /// 别的实例还引用同一张时只清字段、不删文件。
    /// </summary>
    public static void ClearForInstance(ImageInstance instance, PluginSettings settings)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var previous = instance.File;
        instance.File = "";

        if (!string.IsNullOrWhiteSpace(previous) && !AnyInstanceUses(settings, previous, except: instance))
            DeleteFile(previous);
    }

    /// <summary>删掉配置目录里的一张组件图片副本（文件名无效/已不存在都安静返回）。</summary>
    public static void DeleteFile(string? fileName)
    {
        if (string.IsNullOrWhiteSpace(fileName)) return;

        var folder = Plugin.ConfigFolder;
        if (string.IsNullOrWhiteSpace(folder)) return;

        try { File.Delete(Path.Combine(folder, fileName)); }
        catch { /* 正被占用就留着，下次换图/清理再删 */ }
    }

    /// <summary>
    /// 清掉配置目录里已经没有实例引用的贴纸副本（删实例、迁移异常留下的孤儿文件）。
    /// 只清带 <c>component-image-sticker-</c> 段的文件，不碰旧单图副本（那个由 <see cref="Clear"/> 负责）。
    /// </summary>
    public static void PruneUnusedInstanceFiles(PluginSettings settings)
    {
        var folder = Plugin.ConfigFolder;
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return;

        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var instance in settings.ImageInstances)
        {
            if (!string.IsNullOrWhiteSpace(instance.File)) used.Add(instance.File);
        }

        foreach (var file in Directory.GetFiles(folder, BaseName + "-sticker-*"))
        {
            var name = Path.GetFileName(file);
            if (used.Contains(name)) continue;
            try { File.Delete(file); }
            catch { /* 正被占用就留着，下次再清 */ }
        }
    }

    /// <summary>除了 <paramref name="except"/> 之外，还有没有别的实例在用这个文件名。</summary>
    private static bool AnyInstanceUses(PluginSettings settings, string fileName, ImageInstance except)
    {
        foreach (var instance in settings.ImageInstances)
        {
            if (ReferenceEquals(instance, except)) continue;
            if (string.Equals(instance.File, fileName, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }
}
