using System.Diagnostics;
using ClassIsland.Core.Abstractions.Services;
using Microsoft.Extensions.DependencyInjection;

namespace LargeScreenClockRevived.Services;

/// <summary>
/// 全插件统一的时间源：优先用 ClassIsland 的 <see cref="IExactTimeService"/>
/// （它已经把 CI 设置里的「时间偏移」算进去了），拿不到或抛异常时退回本机 <see cref="DateTime.Now"/>。
///
/// 为什么要抽出来：大屏「读数」此前用本机时间，课程进度/打铃用 CI 虚拟时间，
/// 二者相差 TimeOffsetSeconds —— 表现为读数与进度/打铃对不齐。现在两边共用同一条时间轴；
/// 时间偏移为 0 时它等价于本机时间，所以默认设置下的行为与 1.3.1.1 完全一致。
///
/// 两个性能点：
/// 1) 宿主服务只解析一次（惰性），不每次查 DI——每秒刷新要调很多次时间；
/// 2) 值缓存极短（15ms）：同一次刷新里取多次时间不会反复跨服务调用，又不会让秒跳变迟滞。
/// </summary>
public static class CiTimeService
{
    /// <summary>值缓存时长（按 Stopwatch 频率换算成 tick，不假设 Tick 单位是时间）。</summary>
    private static readonly long CacheTicks = Math.Max(1, Stopwatch.Frequency * 15 / 1000);

    private static readonly object Gate = new();

    private static IServiceProvider? _provider;
    private static IExactTimeService? _exactTime;
    private static bool _resolved;
    private static bool _reportedError;
    private static long _cachedAt;
    private static DateTime _cachedNow;

    /// <summary>
    /// 把宿主服务提供者交给时间服务。全进程一次即可，重复调用会重置解析结果（便于宿主较晚才就绪的场景）。
    /// </summary>
    public static void Attach(IServiceProvider? provider)
    {
        lock (Gate)
        {
            _provider = provider;
            _exactTime = null;
            _resolved = false;
            _reportedError = false;
        }
    }

    /// <summary>丢掉已解析的服务与缓存值：下次访问重新解析（CI 服务可能晚于插件就绪）。</summary>
    public static void Invalidate()
    {
        lock (Gate)
        {
            _exactTime = null;
            _resolved = false;
            _cachedAt = 0;
        }
    }

    /// <summary>
    /// 当前本地时间（含 CI 的时间偏移）。CI 服务不可用/调用失败时回退本机时间，绝不抛。
    /// </summary>
    public static DateTime Now
    {
        get
        {
            lock (Gate)
            {
                var stamp = Stopwatch.GetTimestamp();
                if (_cachedAt != 0 && stamp - _cachedAt < CacheTicks) return _cachedNow;

                _cachedNow = ReadNow();
                _cachedAt = stamp;
                return _cachedNow;
            }
        }
    }

    /// <summary>真正取时间（已持有 <see cref="Gate"/>）。服务解析失败一次后不再反复抛栈刷日志。</summary>
    private static DateTime ReadNow()
    {
        try
        {
            if (!_resolved)
            {
                _resolved = true;
                _exactTime = _provider?.GetService<IExactTimeService>();
            }

            if (_exactTime is not null) return _exactTime.GetCurrentLocalDateTime();
        }
        catch (Exception ex)
        {
            // 服务存在但调用失败（极少数宿主状态）→ 永久退回本机时间，只记一次
            _exactTime = null;
            if (!_reportedError)
            {
                _reportedError = true;
                Debug.WriteLine($"[CiTimeService] 读取 CI 时间失败，回退本机时间：{ex.Message}");
            }
        }

        return DateTime.Now;
    }
}
