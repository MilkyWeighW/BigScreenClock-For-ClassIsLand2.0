using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
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
        }
        catch (Exception ex)
        {
            UrlHint.Text = "保存失败：" + ex.Message;
        }
    }
}
