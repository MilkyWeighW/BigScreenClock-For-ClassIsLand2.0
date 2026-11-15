using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using LargeScreenClockRevived.Models;
using LargeScreenClockRevived.Services;
using LargeScreenClockRevived.Views;

namespace LargeScreenClockRevived.Settings;

/// <summary>
/// 背景页签的 1.4 补充面板：实时模糊说明 + 网页背景地址/运行时状态。
///
/// 为什么做成自包含控件而不是往 SettingsPage 里再塞一堆成员：
/// 设置页已经很大，而这些内容只在「实时模糊 / 网页背景」两档下出现；
/// 面板自己从 <see cref="Plugin.Settings"/> 取数据、自己订阅变更，设置页只需要
/// 在「背景」分组里放一个 &lt;settings:BackgroundExtrasPanel/&gt; 即可，零接线。
///
/// 所有失败路径都只改提示文字：地址非法、WebView2 不可用都不会抛异常，
/// 更不会影响已经打开的大屏。
/// </summary>
public partial class BackgroundExtrasPanel : UserControl
{
    private PluginSettings? _settings;
    private bool _updatingUrl;

    public BackgroundExtrasPanel()
    {
        InitializeComponent();

        ApplyButton.Click += ApplyUrl_Click;
        UrlBox.LostFocus += (_, _) => ApplyUrl();
        PickLocalFileButton.Click += PickLocalFile_Click;
        ClearLocalFileButton.Click += ClearLocalFile_Click;

        Loaded += (_, _) =>
        {
            HookSettings();
            Refresh();
        };
        Unloaded += (_, _) => UnhookSettings();
    }

    /// <summary>
    /// 订阅设置变更：切档/改地址/改颜色时刷新显示。
    /// 进入视觉树才挂、离开就摘，避免页面被 CI 复用后旧实例一直活着。
    /// </summary>
    private void HookSettings()
    {
        var settings = Plugin.Settings;
        if (settings is null) return;
        if (ReferenceEquals(settings, _settings)) return;

        UnhookSettings();
        _settings = settings;
        _settings.PropertyChanged += OnSettingsChanged;
    }

    private void UnhookSettings()
    {
        if (_settings is not null)
            _settings.PropertyChanged -= OnSettingsChanged;
        _settings = null;
    }

    private void OnSettingsChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PluginSettings.BackgroundKind)
            or nameof(PluginSettings.BackgroundWebUrl)
            or nameof(PluginSettings.BackgroundWebFile)
            or nameof(PluginSettings.BackgroundColor))
            Refresh();
    }

    /// <summary>按当前档位决定面板露出哪一段；网址输入框只在网页档下可用。</summary>
    private void Refresh()
    {
        var settings = _settings;
        if (settings is null)
        {
            IsVisible = false;
            return;
        }

        var kind = (BackgroundKind)settings.BackgroundKind;
        var isNativeBlur = kind == BackgroundKind.NativeBlur;
        var isWeb = kind == BackgroundKind.WebPage;

        // 面板自身只在两个新档位下出现，其它背景类型保持原本的设置页布局
        IsVisible = isNativeBlur || isWeb;
        NativeBlurSection.IsVisible = isNativeBlur;
        WebSection.IsVisible = isWeb;

        if (isNativeBlur)
        {
            NativeBlurHint.Text =
                "实时模糊由 Windows 自身的 DWM 完成：窗口保持透明，桌面画面被实时模糊后透出来，" +
                "不额外占用渲染开销。系统或显卡驱动不支持时会自动退回纯色背景，不会影响大屏使用。";
        }

        if (!isWeb) return;

        // 地址框不要在用户正在输入时被设置变更打断
        if (!UrlBox.IsFocused)
        {
            _updatingUrl = true;
            UrlBox.Text = settings.BackgroundWebUrl;
            _updatingUrl = false;
        }

        var available = WebView2Availability.IsAvailable;
        UrlBox.IsEnabled = available;
        ApplyButton.IsEnabled = available;

        // 本地网页行：显示文件名（路径通常很长，摘要只放文件名，完整路径在提示里）
        var localFile = (settings.BackgroundWebFile ?? "").Trim();
        PickLocalFileButton.IsEnabled = available;
        ClearLocalFileButton.IsEnabled = available && localFile.Length > 0;
        LocalFileSummary.Text = DescribeLocalFile(localFile);
        LocalFileHint.Text = "本地网页会以 file:// 打开；页面里的 css / js / 图片要和这个 html 放在同一目录（或子目录）并用相对路径，否则只会显示一张空白页。";

        if (available)
        {
            var version = WebView2Availability.Version;
            AvailabilityText.Text = string.IsNullOrWhiteSpace(version)
                ? "WebView2 运行时可用。"
                : "WebView2 运行时可用（版本 " + version + "）。";
            if (string.IsNullOrEmpty(UrlHint.Text))
                UrlHint.Text = "建议填写带 https:// 的完整地址；网页会显示在大屏组件下面，加载失败时保持纯色背景。";
        }
        else
        {
            AvailabilityText.Text = "网页背景不可用：" + WebView2Availability.UnavailableReason;
            UrlHint.Text = "当前系统无法显示网页背景，可改用其它背景类型。";
        }
    }

    private void ApplyUrl_Click(object? sender, RoutedEventArgs e) => ApplyUrl();

    /// <summary>本地网页摘要：没有选、选中且存在、选中但文件已经不在了三种说法。</summary>
    private static string DescribeLocalFile(string path)
    {
        if (path.Length == 0) return "本地网页：未选择";

        try
        {
            var name = Path.GetFileName(path);
            if (string.IsNullOrEmpty(name)) name = path;
            return File.Exists(path)
                ? "本地网页：" + name + "（优先于上面的地址）"
                : "本地网页：" + name + "（文件不存在，已回退到上面的地址 / 纯色）";
        }
        catch
        {
            return "本地网页：" + path;
        }
    }

    /// <summary>
    /// 选择本地 .html。只存绝对路径、不复制文件：网页通常要带同目录的资源，
    /// 只搬走 html 会让页面变成空白；复制整个目录又会失控，所以维持引用。
    /// </summary>
    private async void PickLocalFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is null) return;

        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择本地网页",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("网页文件")
                    {
                        Patterns = ["*.html", "*.htm"],
                    },
                    // 允许全部文件：有些网页没有扩展名，或用户手写了一个别的后缀
                    FilePickerFileTypes.All,
                ],
            });

            if (files.Count == 0) return;

            var path = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(path))
            {
                LocalFileHint.Text = "这个位置拿不到本地路径（可能在压缩包里），请先把 html 解压到磁盘再选。";
                return;
            }

            if (!File.Exists(path))
            {
                LocalFileHint.Text = "选中的文件已不存在：" + path;
                return;
            }

            _settings.BackgroundWebFile = path;
            NotifyBackgroundChanged("已选择本地网页：" + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            LocalFileHint.Text = "选择本地网页失败：" + ex.Message;
        }
    }

    /// <summary>清除本地网页：退回用上面的网页地址；地址也为空时大屏保持纯色兜底。</summary>
    private void ClearLocalFile_Click(object? sender, RoutedEventArgs e)
    {
        if (_settings is null) return;

        try
        {
            if (string.IsNullOrEmpty(_settings.BackgroundWebFile)) return;
            _settings.BackgroundWebFile = "";
            NotifyBackgroundChanged("已清除本地网页，改用上面的网页地址（地址为空时保持纯色）。");
        }
        catch (Exception ex)
        {
            LocalFileHint.Text = "清除失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 本地网页变更后的统一收尾：立即存盘一次、让挂着的网页窗口重新解析导航目标，再刷新摘要。
    /// 走 <see cref="WebBackgroundService.Active"/> 是因为面板不知道大屏窗口是否开着；
    /// 没开时它只是 null，不影响保存。
    /// </summary>
    private void NotifyBackgroundChanged(string hint)
    {
        try
        {
            Plugin.SaveSettings();
        }
        catch
        {
            // 存盘失败不影响本次生效（设置对象已经改了，防抖存盘还会再试）
        }

        try
        {
            if (_settings is not null) WebBackgroundService.Active?.Sync(_settings);
        }
        catch
        {
            // 同步失败最多是网页窗口等下一次设置变更再生效
        }

        Refresh();
        LocalFileHint.Text = hint;
    }

    /// <summary>
    /// 保存地址：清洗失败就只提示、不改设置；成功写入设置（存盘由插件统一的防抖存盘负责）。
    /// 设置一变更，大屏/预览会通过既有 PropertyChanged 链路自动刷新。
    /// </summary>
    private void ApplyUrl()
    {
        if (_updatingUrl || _settings is null) return;

        var raw = UrlBox.Text ?? "";
        var normalized = WebViewHost.NormalizeUrl(raw);
        if (normalized.Length == 0)
        {
            UrlHint.Text = "地址无效：只支持 http / https / file 地址，例如 https://example.com";
            return;
        }

        try
        {
            _settings.BackgroundWebUrl = normalized;
            UrlHint.Text = "已保存：" + normalized;

            // 地址立即生效：网页窗口若正开着就重新解析导航目标（本地网页仍优先）
            try { WebBackgroundService.Active?.Sync(_settings); } catch { }
        }
        catch (Exception ex)
        {
            UrlHint.Text = "保存失败：" + ex.Message;
        }
    }
}
