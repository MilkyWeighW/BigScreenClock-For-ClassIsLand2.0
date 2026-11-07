using System.IO;

namespace LargeScreenClock.Models;

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
            if (exceptName is not null && string.Equals(name, exceptName, StringComparison.OrdinalIgnoreCase))
                continue;

            try { File.Delete(file); }
            catch { /* 正被占用就留着，下次换图再删 */ }
        }
    }
}
