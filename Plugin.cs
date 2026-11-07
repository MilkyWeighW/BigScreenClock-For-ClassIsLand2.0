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
    /// 主动保存设置到文件
    /// </summary>
    public static void SaveSettings()
    {
        if (Settings == null || ConfigFolder == null) return;
        var path = Path.Combine(ConfigFolder, "Settings.json");
        var dir = Path.GetDirectoryName(path);
        if (dir != null) Directory.CreateDirectory(dir);
        ConfigureFileHelper.SaveConfig(path, Settings);
    }

    public override void Initialize(HostBuilderContext context, IServiceCollection services)
    {
        ConfigFolder = PluginConfigFolder;

        var settingsPath = Path.Combine(ConfigFolder, "Settings.json");
        Settings = ConfigureFileHelper.LoadConfig<PluginSettings>(settingsPath);

        // 「正在拖位置/字号」纯界面状态，绝不该跨会话留着：万一哪次拖到一半崩了把它存了下来，
        // 下次启动布局就一直是冻结的。这里强制归零。
        Settings.AdjustingLayout = false;

        // 组件布局按清单补齐：老配置里没有「组件」这一节，升级上来后设置页与组件壳都能直接读到默认值
        Settings.EnsureComponentLayouts();

        // 旧版的 ShowXxx 显示开关一次性搬进组件启用开关（只搬一次，之后以组件设置里的开关为准）
        Settings.MigrateLegacyComponentVisibility();

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

        services.AddSingleton(Settings);

        services.AddSingleton<DecibelMeterService>();
        services.AddSingleton<CameraActivityService>();
        services.AddSingleton<FullScreenClockViewModel>();
        services.AddSingleton<AutoTriggerService>();

        services.AddHostedService<ServiceProviderCapture>();
        services.AddHostedService<AutoTriggerService>();
        services.AddHostedService<TrayMenuService>();

        services.AddSettingsPage<LargeScreenClockRevived.Settings.SettingsPage>();
    }
}
