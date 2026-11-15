using ClassIsland.Core;
using ClassIsland.Core.Abstractions;
using ClassIsland.Core.Attributes;
using ClassIsland.Core.Extensions.Registry;
using ClassIsland.Shared.Helpers;
using LargeScreenClockRevived.Helpers;
using LargeScreenClockRevived.Models;
using LargeScreenClockRevived.Services;
using LargeScreenClockRevived.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace LargeScreenClockRevived;

[PluginEntrance]
public class Plugin : PluginBase
{
    public static PluginSettings? Settings { get; private set; }
    public static IServiceProvider? ServiceProvider { get; set; }
    public static string? ConfigFolder { get; private set; }

    private static readonly System.Timers.Timer SettingsSaveDebouncer =
        new(400) { AutoReset = false };

    /// <summary>
    /// 主动保存设置到文件。
    ///
    /// 签名保持同步（设置页/托盘等一堆旧调用点不用改），内部改成异步：
    /// 原来这里直接在 UI 线程上「序列化整份配置 + 写盘」，拖一下滑条就是几十毫秒的卡顿。
    /// 现在交给 <see cref="SettingsStore"/>——线程池 IO + 10ms 合并，连续变更只落最后一份。
    /// 失败只记 Debug 日志，不影响大屏运行。
    /// </summary>
    public static void SaveSettings()
    {
        if (Settings == null || ConfigFolder == null) return;
        var path = Path.Combine(ConfigFolder, "Settings.json");
        // fire-and-forget：SettingsStore 内部吞掉所有异常，这里不需要也无法 await
        _ = SettingsStore.SaveAsync(path, Settings);
    }

    /// <summary>退出前同步落盘一次（只在 ProcessExit 里用；常规路径仍走异步保存）。</summary>
    private static void FlushSettingsNow()
    {
        try
        {
            if (Settings is null || ConfigFolder is null) return;
            SettingsStore.SaveNow(Path.Combine(ConfigFolder, "Settings.json"), Settings);
        }
        catch
        {
            // 退出路径上不能抛
        }
    }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        ConfigFolder = PluginConfigFolder;

        var settingsPath = Path.Combine(ConfigFolder, "Settings.json");
        // 启动这次加载必须同步：宿主 Initialize 返回前就得有 Settings —— DI 单例、设置页、
        // 组件默认值都直接读它，异步加载会让这些路径先拿到 null。
        // 会话内的保存与重读一律走 SettingsStore 的异步路径（10ms 合并、线程池 IO）。
        Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(settingsPath);

        // 「正在拖位置/字号」纯界面状态，绝不该跨会话留着：万一哪次拖到一半崩了把它存了下来，
        // 下次启动布局就一直是冻结的。这里强制归零。
        Settings.AdjustingLayout = false;

        // 组件布局按清单补齐：老配置里没有「组件」这一节，升级上来后设置页与组件壳都能直接读到默认值
        Settings.EnsureComponentLayouts();

        // 旧版的 ShowXxx 显示开关一次性搬进组件启用开关（只搬一次，之后以组件设置里的开关为准）
        Settings.MigrateLegacyComponentVisibility();

        // 与 ClassIsland 重合的那几块（天气/日期/倒计时/降雨/文本提醒/提醒面板）对老配置也默认隐藏一次
        Settings.MigrateOverlappingComponentDefaults();

        // 旧配置里「背景」只有「背景图」一个概念：选过图的沿用自定义图片，其余人默认那项就是壁纸
        if (File.Exists(settingsPath) && !string.IsNullOrWhiteSpace(Settings.BackgroundImageFile))
            Settings.BackgroundKind = (int)BackgroundKind.CustomImage;

        // 从来没调过背景明暗的老配置：补一次「默认压暗 30%」（调过的值不动，迁移只跑一次）
        Settings.MigrateBackgroundDimDefault();

        // 模糊壁纸磁盘缓存：每次启动顺手清一次，只留最近几张（换壁纸后旧缓存没人再引用）
        WallpaperCacheService.CleanupCache();

        // 已经有配置文件 = 老版本升级上来：把原有那套颜色原样留在黑暗主题下，
        // 别让「跟随 ClassIsland」在首次启动时就把外观换成另一套（新装用户没有这个问题，默认跟随）
        if (File.Exists(settingsPath))
            ClockThemeApplier.MigrateLegacySettings(Settings);

        // 设置保存防抖：拖滑条/连续修改不会狂写盘，停顿 400ms 后统一保存
        SettingsSaveDebouncer.Elapsed += (_, _) =>
        {
            try { SaveSettings(); } catch { }
        };
        Settings.PropertyChanged += (_, _) =>
        {
            SettingsSaveDebouncer.Stop();
            SettingsSaveDebouncer.Start();
        };

        // 进程退出前再同步刷一次：最后一次改动落在 400ms 防抖窗口里时，
        // 异步保存可能还没轮到它，进程就没了。ProcessExit 不依赖宿主 API，最省事也最可靠。
        AppDomain.CurrentDomain.ProcessExit += (_, _) => FlushSettingsNow();

        services.AddSingleton(Settings);

        services.AddSingleton<DecibelMeterService>();
        services.AddSingleton<CameraActivityService>();
        services.AddSingleton<FullScreenClockViewModel>();
        // 自动触发只保留一份实例：以前 AddSingleton + AddHostedService 各注册一次，
        // hosted 的那个才订阅课程事件，而 DI 里解析到的是另一个空壳单例（谁拿到都白拿）。
        // 现在 hosted 服务用工厂取同一个单例：既能从 DI 解析，又只订阅一次事件。
        services.AddSingleton<AutoTriggerService>();

        services.AddHostedService<ServiceProviderCapture>();
        services.AddHostedService(sp => sp.GetRequiredService<AutoTriggerService>());
        services.AddHostedService<TrayMenuService>();

        services.AddSettingsPage<LargeScreenClockRevived.Settings.SettingsPage>();
    }
}
