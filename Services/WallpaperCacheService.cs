using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using LargeScreenClock.Helpers;

namespace LargeScreenClock.Services;

/// <summary>
/// 「模糊壁纸」的预渲染 + 两级缓存（内存 LRU + 磁盘 PNG），把
/// 「解码壁纸 → 按屏宽缩放 → StackBlur 模糊」这套重活从 UI 线程挪到线程池，
/// 并在下次开屏时直接命中，避免大屏窗口出现时掉帧。
///
/// 缓存键 = 壁纸完整路径 + 文件最后写入时间(UTC ticks) + 目标宽度 + 生效模糊半径。
/// 把 mtime 放进键里，壁纸被替换（哪怕路径没变）时旧缓存自然失效，不需要额外失效逻辑；
/// 模糊半径用钳制后的整数值，88 和 500 会共享同一条缓存，避免无意义的重复渲染。
///
/// 所有权约定：**返回的 Bitmap 归调用方释放**。内存缓存里另存一份主副本，主副本永远不交给调用方，
/// 命中内存时通过 CopyPixels 克隆一份新位图返回（纯内存拷贝，比重新解码 PNG 便宜得多），
/// 这样调用方误 Dispose 也不会把缓存搞坏。
///
/// 线程约定：生成、磁盘读写都在 <see cref="Task.Run(Action, CancellationToken)"/> 里做；
/// 缓存字典本身用锁保护（只在极短的查找/替换窗口内持有）。
/// 任何异常（文件不存在、解码失败、磁盘只读、取消）都返回 null，绝不向上抛。
/// </summary>
public static class WallpaperCacheService
{
    /// <summary>内存缓存条数。大屏只需要「当前壁纸 + 上一次壁纸」两条，再多纯占内存。</summary>
    private const int MemoryCacheCapacity = 2;

    private const string PngExtension = ".png";

    /// <summary>保护 <see cref="MemoryCache"/>；同时也保证克隆时主副本不会被并发逐出。</summary>
    private static readonly object CacheGate = new();

    /// <summary>按 LRU 排列：索引 0 最新，末尾最旧。容量固定很小，用 List 比 LinkedList 更省事。</summary>
    private static readonly List<MemoryEntry> MemoryCache = new(MemoryCacheCapacity + 1);

    /// <summary>缓存目录只解析一次；<see cref="Lazy{T}"/> 默认线程安全即可。</summary>
    private static readonly Lazy<string> CacheDirectoryLazy = new(CreateCacheDirectory, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>
    /// 磁盘缓存目录（<c>Plugin.ConfigFolder/cache</c>），首次访问时创建。
    /// 目录建不出来也只返回路径，读写失败由调用链各自兜底，不影响背景回退。
    /// </summary>
    public static string CacheDirectory => CacheDirectoryLazy.Value;

    /// <summary>
    /// 取「模糊后的壁纸」：先查内存，再查磁盘 PNG，最后才真正解码 + 模糊并回写磁盘。
    /// <paramref name="ct"/> 只用于放弃本次等待，取消时返回 null（与「异常一律 null」的约定一致）。
    /// 返回的位图由调用方负责 <see cref="IDisposable.Dispose"/>。
    /// </summary>
    public static async Task<Bitmap?> GetBlurredWallpaperAsync(
        string path, int targetWidth, double blurRadius, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || targetWidth < 1) return null;

        try
        {
            ct.ThrowIfCancellationRequested();

            // 键里已经带上「生效半径」，后续所有环节都用同一个值，避免 0.4 和 0.6 各算一遍
            var effectiveRadius = Math.Clamp((int)Math.Round(blurRadius), 0, StackBlur.MaxRadius);
            var cacheKey = BuildCacheKey(path, targetWidth, effectiveRadius);

            // 1) 内存命中：直接把主副本克隆一份给调用方
            var memoryHit = TryTakeFromMemory(cacheKey);
            if (memoryHit != null) return memoryHit;

            var diskPath = GetDiskPath(cacheKey);

            // 2) 磁盘命中：解码一次，存入内存当主副本，再克隆给调用方
            var fromDisk = TryLoadFromDisk(diskPath);
            if (fromDisk != null)
            {
                // 先克隆再入缓存：入缓存后主副本可能被并发逐出并 Dispose，克隆会踩到已释放对象
                var diskClone = CloneBitmap(fromDisk);
                AddToMemory(cacheKey, fromDisk);
                return diskClone;
            }

            // 3) 真正的生成：解码 + 模糊，全在线程池里做
            var generated = await Task.Run(() =>
            {
                ct.ThrowIfCancellationRequested();

                var wallpaper = WallpaperService.LoadWallpaper(path, targetWidth);
                if (wallpaper == null) return null;

                // LoadWallpaper 解码出来的中间位图没人再用了，模糊完立刻释放
                try
                {
                    return ImageBlur.Blur(wallpaper, effectiveRadius);
                }
                finally
                {
                    wallpaper.Dispose();
                }
            }, ct).ConfigureAwait(true);

            if (generated == null) return null;
            ct.ThrowIfCancellationRequested();

            // 4) 回写磁盘（PNG）。写失败不影响本次使用，只是下次还得重新生成
            await Task.Run(() => SavePng(generated, diskPath), ct).ConfigureAwait(true);

            // 同样先克隆再入缓存：主副本进缓存后随时可能被别的线程逐出并释放
            var result = CloneBitmap(generated);
            AddToMemory(cacheKey, generated);
            return result;
        }
        catch (OperationCanceledException)
        {
            // 取消是正常路径（比如用户快速切换背景），不当作错误
            return null;
        }
        catch
        {
            // 解码失败 / 磁盘只读 / 内存不足：一律退回「没有模糊壁纸」，调用方用纯色兜底
            return null;
        }
    }

    /// <summary>
    /// 清理磁盘缓存：按最后写入时间只保留最新 <paramref name="keepCount"/> 个 PNG。
    /// 启动时或壁纸切换后调一次即可；<paramref name="keepCount"/> 为 0 表示全删。
    /// </summary>
    public static void CleanupCache(int keepCount = 6)
    {
        try
        {
            var directory = CacheDirectory;
            if (!Directory.Exists(directory)) return;

            if (keepCount < 0) keepCount = 0;

            var files = new DirectoryInfo(directory).GetFiles("*" + PngExtension);
            if (files.Length <= keepCount) return;

            // 新的在前，从第 keepCount 个开始删
            Array.Sort(files, static (left, right) => right.LastWriteTimeUtc.CompareTo(left.LastWriteTimeUtc));
            for (var i = keepCount; i < files.Length; i++)
            {
                try
                {
                    files[i].Delete();
                }
                catch
                {
                    // 文件正被别的进程占用（或刚被删掉）：留下一个旧文件不影响正确性，跳过
                }
            }
        }
        catch
        {
            // 清理是尽力而为，失败不能影响开屏
        }
    }

    /// <summary>缓存键：完整路径 + mtime + 宽度 + 生效半径。Windows 路径里不可能出现 '|'，用它当分隔符安全。</summary>
    private static string BuildCacheKey(string path, int targetWidth, int effectiveRadius)
    {
        // 相对路径在两台机器/两个工作目录下含义不同，先绝对化再进键
        var fullPath = Path.GetFullPath(path);
        // 文件被删时 mtime 是 1601 年，与真实文件的键必然不同，不会误命中旧缓存
        var lastWriteTicks = File.GetLastWriteTimeUtc(fullPath).Ticks;

        // 全是整数/路径，不经过区域设置格式化，键在进程内稳定
        return $"{fullPath}|{lastWriteTicks}|{targetWidth}|{effectiveRadius}";
    }

    /// <summary>磁盘文件名：sha256(key) 前 16 字节的小写 hex（32 字符）+ .png，避免非法字符与大小写问题。</summary>
    private static string GetDiskPath(string cacheKey)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(cacheKey));
        var name = Convert.ToHexString(hash.AsSpan(0, 16)).ToLowerInvariant() + PngExtension;
        return Path.Combine(CacheDirectory, name);
    }

    /// <summary>从内存缓存取主副本的克隆；命中时把条目提到最前，未命中返回 null。</summary>
    private static Bitmap? TryTakeFromMemory(string cacheKey)
    {
        lock (CacheGate)
        {
            var index = MemoryCache.FindIndex(entry => entry.Key == cacheKey);
            if (index < 0) return null;

            var entry = MemoryCache[index];
            if (index > 0)
            {
                MemoryCache.RemoveAt(index);
                MemoryCache.Insert(0, entry);
            }

            // 克隆在锁内做：否则刚拿到主副本、条目就被另一线程逐出并 Dispose，会踩到已释放的位图
            return CloneBitmap(entry.Bitmap);
        }
    }

    /// <summary>读磁盘 PNG；文件不存在/损坏/解码失败都返回 null（损坏文件顺手删掉，免得每次开屏都重试）。</summary>
    private static Bitmap? TryLoadFromDisk(string diskPath)
    {
        try
        {
            if (!File.Exists(diskPath)) return null;

            // Bitmap 构造函数会把整个文件读进内存，构造完文件句柄即释放，可以安全返回
            return new Bitmap(diskPath);
        }
        catch
        {
            try
            {
                File.Delete(diskPath);
            }
            catch
            {
                // 删不掉就算了，下次解码还会失败，代价只是一次多余的 IO
            }

            return null;
        }
    }

    /// <summary>主副本入内存缓存（幂等），超出容量时把最旧的逐出并释放。</summary>
    private static void AddToMemory(string cacheKey, Bitmap bitmap)
    {
        lock (CacheGate)
        {
            var index = MemoryCache.FindIndex(entry => entry.Key == cacheKey);
            if (index >= 0)
            {
                var existing = MemoryCache[index];
                MemoryCache.RemoveAt(index);
                if (!ReferenceEquals(existing.Bitmap, bitmap)) existing.Bitmap.Dispose();
            }

            MemoryCache.Insert(0, new MemoryEntry(cacheKey, bitmap));

            while (MemoryCache.Count > MemoryCacheCapacity)
            {
                var last = MemoryCache[MemoryCacheCapacity];
                MemoryCache.RemoveAt(MemoryCacheCapacity);
                last.Bitmap.Dispose();
            }
        }
    }

    /// <summary>
    /// 克隆一份位图（BGRA8888，保留 alpha）。命中路径靠它把「缓存主副本」和「调用方实例」分开，
    /// 调用方 Dispose 不影响缓存；失败返回 null（调用方就当没有缓存）。
    /// </summary>
    private static Bitmap? CloneBitmap(Bitmap source)
    {
        try
        {
            var size = source.PixelSize;
            if (size.Width < 1 || size.Height < 1) return null;

            var clone = new WriteableBitmap(size, new Vector(96, 96),
                PixelFormat.Bgra8888, AlphaFormat.Premul);

            try
            {
                using var frame = clone.Lock();
                source.CopyPixels(frame, AlphaFormat.Premul);
                return clone;
            }
            catch
            {
                clone.Dispose();
                return null;
            }
        }
        catch
        {
            // 4K 位图可能因为内存不足失败：宁可退回无缓存，也不要抛
            return null;
        }
    }

    /// <summary>
    /// 把位图存成 PNG。先写同目录临时文件再原子改名，避免进程中途退出留下半截 PNG
    /// （半截文件会被下次解码判为损坏并删除，但能避免就避免）。
    /// 临时文件也带 .png 后缀：Avalonia 的 <see cref="Bitmap.Save(string, int?)"/> 只按扩展名选格式。
    /// </summary>
    private static void SavePng(Bitmap bitmap, string finalPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(finalPath);
            if (string.IsNullOrEmpty(directory)) return;

            Directory.CreateDirectory(directory);

            var tempPath = Path.Combine(directory, Guid.NewGuid().ToString("N") + PngExtension);
            try
            {
                bitmap.Save(tempPath);
                // overwrite=true：并发进程/两个相同键同时生成时，后来的覆盖先来的，内容等价
                File.Move(tempPath, finalPath, overwrite: true);
            }
            catch
            {
                try
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                catch
                {
                    // 临时文件残留由 CleanupCache 之外的磁盘清理兜底，这里不额外处理
                }
            }
        }
        catch
        {
            // 磁盘只读 / 路径过长 / 磁盘满：只是没缓存成功，本次仍能正常显示
        }
    }

    /// <summary>惰性解析并创建缓存目录；<c>Plugin.ConfigFolder</c> 尚未初始化时退回临时目录，保证可写。</summary>
    private static string CreateCacheDirectory()
    {
        string baseDirectory;
        try
        {
            // ConfigFolder 在插件 Initialize 之前是 null；早期调用（比如设置页预览）不能因此崩
            baseDirectory = Plugin.ConfigFolder
                            ?? Path.Combine(Path.GetTempPath(), "LargeScreenClock");
        }
        catch
        {
            baseDirectory = Path.GetTempPath();
        }

        var directory = Path.Combine(baseDirectory, "cache");
        try
        {
            Directory.CreateDirectory(directory);
        }
        catch
        {
            // 建不出来就让后续读写各自失败并走 null 兜底
        }

        return directory;
    }

    /// <summary>内存缓存条目。Key 唯一，Bitmap 是缓存自己拥有的主副本，永不交给调用方。</summary>
    private sealed class MemoryEntry
    {
        public MemoryEntry(string key, Bitmap bitmap)
        {
            Key = key;
            Bitmap = bitmap;
        }

        public string Key { get; }

        public Bitmap Bitmap { get; }
    }
}
