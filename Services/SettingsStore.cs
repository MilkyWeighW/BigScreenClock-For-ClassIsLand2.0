using System.Diagnostics;
using ClassIsland.Shared.Helpers;
using LargeScreenClockRevived.Models;

namespace LargeScreenClockRevived.Services;

/// <summary>
/// 设置文件的异步读写。
///
/// 为什么要有它：<see cref="PluginSettings"/> 的任何属性变更都会触发保存，而保存原来发生在调用线程
/// （设置页里就是 UI 线程）上——「序列化整份配置 + 写盘」几十毫秒，拖一下颜色滑块/字号就能感到卡顿。
/// 这里把 IO 挪到线程池，并且先等 10ms 再动手：拖滑块这种连续变更只会落最后一份，中间那些根本不会开始写。
///
/// 关于启动时的同步加载：本类只提供异步入口；<see cref="Plugin.Initialize"/> 那一次仍然同步读盘，
/// 因为宿主 Initialize 返回前必须拿到 Settings（DI 单例、设置页、组件默认值都直接读它），
/// 异步加载会让这些路径读到 null。会话内的所有保存/重读都走这里。
///
/// 失败策略：一律只写 Debug 日志，绝不抛——保存失败不该把大屏或设置页带崩。
/// </summary>
public static class SettingsStore
{
    /// <summary>落盘前的合并窗口（毫秒）：这段时间内又来了新状态，就直接写新的那份。</summary>
    private const int CoalesceDelayMs = 10;

    /// <summary>同一时刻只允许一个写盘循环；后来者只更新「最新状态」然后返回。</summary>
    private static readonly SemaphoreSlim SaveGate = new(1, 1);

    /// <summary>保护「最新要写的那份状态」与版本号（UI 线程写、线程池读）。</summary>
    private static readonly object StateGate = new();

    private static string? _pendingPath;
    private static PluginSettings? _pendingSettings;
    private static long _pendingVersion;

    /// <summary>
    /// 异步读取配置。<paramref name="path"/> 不存在/内容损坏/IO 失败时返回 null，由调用方自行回退。
    /// </summary>
    public static async Task<PluginSettings?> LoadAsync(string path)
    {
        try
        {
            // 先让出 10ms：调用方往往紧接着还要读别的文件，没必要两处 IO 抢同一时刻
            await Task.Delay(CoalesceDelayMs).ConfigureAwait(false);
            return await Task.Run(() => ConfigureFileHelper.LoadConfig<PluginSettings>(path)).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsStore] 读取设置失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 异步保存配置。连续调用会被合并：已经有一个写盘循环在跑时，本次只更新「最新状态」，
    /// 循环写完手上的那份发现版本变了就再写一轮，因此永远只落最后一份。
    /// 返回的 Task 在「本次请求的数据一定已被某次写盘覆盖或已有更新请求接手」时完成，不抛异常。
    /// </summary>
    public static async Task SaveAsync(string path, PluginSettings settings)
    {
        lock (StateGate)
        {
            _pendingPath = path;
            _pendingSettings = settings;
            _pendingVersion++;
        }

        // 排队而不是「有写入者就直接放弃」：放弃会让「刚发生的最后一次变更」有机会漏写
        // （写入者已经取完快照、正要收工）。等它写完再跑一轮，确保本次请求的数据一定落盘。
        await SaveGate.WaitAsync().ConfigureAwait(false);

        try
        {
            while (true)
            {
                await Task.Delay(CoalesceDelayMs).ConfigureAwait(false);

                string? targetPath;
                PluginSettings? targetSettings;
                long version;
                lock (StateGate)
                {
                    targetPath = _pendingPath;
                    targetSettings = _pendingSettings;
                    version = _pendingVersion;
                }

                if (targetPath is null || targetSettings is null) return;

                // 序列化跑在线程池上，而设置对象是可变的：UI 线程正好增删集合时会抛
                // "Collection was modified"。这类失败只是一次竞态，重试两次即可；
                // 磁盘真满/文件被占时才放弃（下次变更还会再触发一次保存）。
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        await Task.Run(() => WriteNow(targetPath, targetSettings)).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (attempt >= 2)
                        {
                            Debug.WriteLine($"[SettingsStore] 保存设置失败（已重试）：{ex.Message}");
                            break;
                        }

                        await Task.Delay(CoalesceDelayMs).ConfigureAwait(false);
                    }
                }

                lock (StateGate)
                {
                    // 写盘期间没有新请求 → 收工；有新请求 → 再写一轮最新的那份
                    if (version == _pendingVersion) return;
                }
            }
        }
        finally
        {
            SaveGate.Release();
        }
    }

    /// <summary>
    /// 同步落盘。只给「必须立刻可见」的时刻用（例如退出前强制刷一次），常规路径请用 <see cref="SaveAsync"/>。
    /// 失败返回 false 并记日志，不抛。
    /// </summary>
    public static bool SaveNow(string path, PluginSettings settings)
    {
        try
        {
            WriteNow(path, settings);
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[SettingsStore] 同步保存设置失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>真正干活的写盘（目录不存在就补建，格式仍由 ConfigureFileHelper 保证）。</summary>
    private static void WriteNow(string path, PluginSettings settings)
    {
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        ConfigureFileHelper.SaveConfig(path, settings);
    }
}
