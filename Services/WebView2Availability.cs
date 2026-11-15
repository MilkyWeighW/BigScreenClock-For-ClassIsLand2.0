using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;

namespace LargeScreenClockRevived.Services;

/// <summary>
/// 探测本机有没有可用的 Microsoft Edge WebView2 运行时。
///
/// 为什么要单独探测而不是直接 new 控件：网页背景是「可选装饰」，运行时没装/装坏了
/// 必须安静地退回纯色背景，绝不能让大屏起不来。所以设置页在切档前、服务在开窗前
/// 都先问这里一句；探测本身全程 try/catch，最坏结果是「不可用 + 一句中文原因」。
///
/// 两条证据链（任一成立即认为可用）：
/// 1. WebView2 加载器自己的 API GetAvailableBrowserVersionString()——最准，
///    它找到的运行时就是后面真能用的那个；
/// 2. 注册表里 EdgeUpdate 的 WebView2 客户端版本（加载器不可用时的兜底，
///    例如加载器 DLL 尚未初始化、或 API 因权限抛错）。
/// 另外单独检查 WebView2Loader.dll 是否随包落地：缺这个原生库时上面两条都可能
/// 看着正常，但一初始化就失败，提前说清楚比事后静默黑屏好。
/// </summary>
public static class WebView2Availability
{
    /// <summary>Edge WebView2 Runtime 在 EdgeUpdate 里的固定客户端 GUID。</summary>
    private const string RuntimeClientGuid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

    private const string LoaderFileName = "WebView2Loader.dll";

    /// <summary>
    /// 探测结果只算一次：运行时不会在进程生命周期里凭空出现/消失，
    /// 每次切背景都去读注册表/起 COM 太浪费。
    /// </summary>
    private static Lazy<ProbeResult> _probe =
        new(ProbeCore, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>WebView2Loader.dll 的目录是否已经告诉过 WebView2 SDK（只需要设一次）。</summary>
    private static bool _loaderPathApplied;

    /// <summary>本机能否使用网页背景。false 时调用方必须静默退回纯色/壁纸，不要弹框。</summary>
    public static bool IsAvailable => _probe.Value.Available;

    /// <summary>可用的 WebView2 运行时版本号；不可用时为 null。</summary>
    public static string? Version => _probe.Value.Version;

    /// <summary>不可用时的中文原因（可用时为空串），可直接显示给用户。</summary>
    public static string UnavailableReason => _probe.Value.Reason;

    /// <summary>
    /// 丢掉缓存的探测结果（设置页的「重新检测」用）。运行时可能是在 CI 运行期间装上的。
    /// </summary>
    public static void Refresh()
    {
        _loaderPathApplied = false;
        _probe = new Lazy<ProbeResult>(ProbeCore, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    private sealed class ProbeResult
    {
        public ProbeResult(bool available, string? version, string reason)
        {
            Available = available;
            Version = version;
            Reason = reason;
        }

        public bool Available { get; }
        public string? Version { get; }
        public string Reason { get; }
    }

    /// <summary>
    /// 告诉 WebView2 SDK 去哪里找 WebView2Loader.dll。
    ///
    /// 插件是装在 ClassIsland 插件目录里的：Core.dll 自己的 DllImport 走的是它所在目录/进程目录，
    /// 不一定能看见我们 cipx 里的 runtimes/win-x64/native。找不到 Loader 时
    /// GetAvailableBrowserVersionString() 会抛异常，于是「明明装了 Edge 却报 WebView2 不可用」——
    /// 网页背景选项就永远置灰。这里在第一次探测前把真实目录设给 SDK。
    /// 老版本 SDK 没有这个 API 时抛 MissingMethodException，忽略即可。
    /// </summary>
    private static void TryApplyLoaderPath()
    {
        if (_loaderPathApplied) return;
        _loaderPathApplied = true;

        try
        {
            foreach (var directory in CandidateDirectories())
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    if (!File.Exists(Path.Combine(directory, LoaderFileName))) continue;
                    CoreWebView2Environment.SetLoaderDllFolderPath(directory);
                    return;
                }
                catch
                {
                    // 单个目录不适合就继续找下一个
                }
            }
        }
        catch
        {
            // SDK 太老没有这个 API：交给默认解析
        }
    }

    private static ProbeResult ProbeCore()
    {
        try
        {
            TryApplyLoaderPath();
            // 先问加载器：它找到的运行时就是后面真能用的那个。
            // 注意不能先查 WebView2Loader.dll 再决定——插件打包方式可能把原生库放在
            // runtimes/<rid>/native 等不同位置，先看文件容易「其实能用却报不可用」。
            string? loaderVersion = null;
            var loaderError = "";
            try
            {
                loaderVersion = CoreWebView2Environment.GetAvailableBrowserVersionString();
            }
            catch (Exception ex)
            {
                // 运行时没装时这里抛 WebView2RuntimeNotFoundException，属正常的「不可用」
                loaderError = ex.Message;
            }

            if (!string.IsNullOrWhiteSpace(loaderVersion))
                return new ProbeResult(true, loaderVersion.Trim(), "");

            // 加载器不可用时再看注册表：有些环境下 API 因权限/初始化失败抛错，
            // 但运行时注册项是完好的，装好/重启后仍有机会可用。
            var registryVersion = TryReadRegistryVersion();
            if (!string.IsNullOrWhiteSpace(registryVersion))
                return new ProbeResult(true, registryVersion.Trim(), "");

            var detail = string.IsNullOrWhiteSpace(loaderError) ? "" : "（加载器返回：" + loaderError + "）";
            var loaderHint = LoaderExists()
                ? ""
                : "（另外没找到 " + LoaderFileName + "，插件包可能缺少这个原生组件。）";
            return new ProbeResult(false, null,
                "未检测到 Microsoft Edge WebView2 运行时" + detail + loaderHint +
                "。可到微软官网下载「WebView2 Runtime」安装后重启 ClassIsland；在此之前网页背景会退回纯色。");
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, null, "检测 WebView2 运行时失败：" + ex.Message);
        }
    }

    /// <summary>
    /// 检查 WebView2Loader.dll 是否随插件/宿主落地。插件可能被解压到 ClassIsland 的插件目录，
    /// 原生库也可能被拷到程序根目录或 runtimes/&lt;rid&gt;/native 下，三处都找一遍。
    /// </summary>
    private static bool LoaderExists()
    {
        try
        {
            foreach (var directory in CandidateDirectories())
            {
                if (string.IsNullOrWhiteSpace(directory)) continue;
                try
                {
                    if (File.Exists(Path.Combine(directory, LoaderFileName))) return true;
                }
                catch
                {
                    // 单个目录不可访问就跳过
                }
            }
        }
        catch
        {
            // 探测失败不致命：交给后续 API 判断
        }

        return false;
    }

    private static string[] CandidateDirectories()
    {
        var rid = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.Arm64 => "win-arm64",
            Architecture.X86 => "win-x86",
            _ => "win-x64",
        };

        var pluginDirectory = "";
        try
        {
            pluginDirectory = Path.GetDirectoryName(typeof(WebView2Availability).Assembly.Location) ?? "";
        }
        catch
        {
            // 单文件发布等场景拿不到路径，留空
        }

        return new[]
        {
            AppContext.BaseDirectory,
            pluginDirectory,
            Path.Combine(AppContext.BaseDirectory, "runtimes", rid, "native"),
            Path.Combine(pluginDirectory, "runtimes", rid, "native"),
        };
    }

    /// <summary>
    /// 注册表兜底：依次查 HKLM 32 位视图、HKLM 64 位视图、HKCU。
    /// 任何一项读失败都跳过，不往上报。
    /// </summary>
    private static string? TryReadRegistryVersion()
    {
        var subKey = @"SOFTWARE\Microsoft\EdgeUpdate\Clients\" + RuntimeClientGuid;

        foreach (var hive in new[] { RegistryHive.LocalMachine, RegistryHive.CurrentUser })
        {
            foreach (var view in new[] { RegistryView.Registry32, RegistryView.Registry64 })
            {
                try
                {
                    using var baseKey = RegistryKey.OpenBaseKey(hive, view);
                    using var key = baseKey.OpenSubKey(subKey);
                    if (key?.GetValue("pv") is string version && !string.IsNullOrWhiteSpace(version))
                        return version;
                }
                catch
                {
                    //  hive/视图不可用：继续下一个
                }
            }
        }

        return null;
    }
}
