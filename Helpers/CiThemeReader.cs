using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;

namespace LargeScreenClockRevived.Helpers;

/// <summary>
/// 读 ClassIsland 当前主题的强调色（单向只读，绝不回写 CI）。
///
/// 为什么是「读资源」而不是读服务：ClassIsland 2.1 基于 FluentAvalonia，强调色不是
/// <c>IThemeService</c> 上的属性，而是主题资源里的画刷/颜色（<c>AccentFillColorDefaultBrush</c>、
/// <c>SystemAccentColor</c> 等）。插件 SDK 没有公开的强调色接口，所以这里按资源键逐个找，
/// 找到即返回 HEX（#RRGGBB），找不到就返回 null，由调用方完全回退到本插件主题自带配色。
///
/// 所有路径都 try/catch：读不到强调色最多是「不跟随」，绝不能让大屏或设置页出错。
/// </summary>
public static class CiThemeReader
{
    /// <summary>
    /// 候选资源键，从「最贴近强调填充色」到「系统强调色」排列。
    /// 前几个是 FluentAvalonia / CI 主题模板实际在用的键，最后的 SystemAccentColor 兜底。
    /// </summary>
    private static readonly string[] CandidateKeys =
    {
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentTextFillColorPrimaryBrush",
        "SystemAccentColor",
    };

    private static readonly object Gate = new();

    /// <summary>已读到的强调色缓存（只在命中时缓存；读不到就每次重试，避免启动早期把 null 定死）。</summary>
    private static string? _cached;

    /// <summary>
    /// 读当前强调色；拿不到返回 null。需要在 UI 线程调用才能访问资源字典，
    /// 非 UI 线程直接返回缓存值（可能为 null），不冒险跨线程读主题资源。
    /// </summary>
    public static string? TryReadAccentColor()
    {
        try
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                lock (Gate) return _cached;
            }

            lock (Gate)
            {
                if (_cached is not null) return _cached;
            }

            if (Application.Current is not { } app) return null;

            foreach (var key in CandidateKeys)
            {
                if (TryFindResource(app, key) is not { } hex) continue;
                lock (Gate) _cached = hex;
                return hex;
            }

            return null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[CiThemeReader] 读取 CI 强调色失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>CI 换了主题/强调色（资源重建）时调用：丢掉缓存，下次重新读。</summary>
    public static void Invalidate()
    {
        lock (Gate) _cached = null;
    }

    /// <summary>按资源键查一次：先走「整棵树查找」（含 Styles 里的 FluentAvalonia 主题），再退回 Application.Resources。</summary>
    private static string? TryFindResource(Application app, string key)
    {
        try
        {
            if (app.TryFindResource(key, out var viaTree) && ToHex(viaTree) is { } hexTree) return hexTree;
        }
        catch { /* 该键不在资源树里 */ }

        try
        {
            if (app.Resources.TryGetResource(key, app.ActualThemeVariant, out var viaResources)
                && ToHex(viaResources) is { } hexRes)
                return hexRes;
        }
        catch { /* 同上 */ }

        return null;
    }

    /// <summary>把资源值转成 #RRGGBB：支持画刷/颜色/字符串三种常见形态，其它类型当作读不到。</summary>
    private static string? ToHex(object? value)
    {
        switch (value)
        {
            case ISolidColorBrush brush:
                return ToHex(brush.Color);
            case Color color:
                return ToHex(color);
            case string text:
                try
                {
                    var parsed = Color.Parse(text);
                    return ToHex(parsed);
                }
                catch { return null; }
            default:
                return null;
        }
    }

    private static string ToHex(Color color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";
}
