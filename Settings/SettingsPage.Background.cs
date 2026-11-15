// ===== SettingsPage 拆分之一：背景（壁纸 / 纯色 / 自定义图片） =====
// 职责：背景类型档位（提取当前壁纸 / 纯色 / 自定义图片）与三种类型的行可见性、
//       壁纸的景深放大与模糊半径、自定义图片的选择/清除/提示文案与填充方式、背景明暗遮罩。
//       成员与逻辑从原 SettingsPage.axaml.cs 原样搬移。
// 注意：纯色背景的 ColorPicker 颜色成员（BackgroundColorValue / BackgroundColorBrush / BackgroundColorHex）
//       在 SettingsPage.Appearance.cs 里，与其它颜色行共用同一套 hex 存取。

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LargeScreenClockRevived.Models;
using LargeScreenClockRevived.Services;
using LargeScreenClockRevived.Views;

namespace LargeScreenClockRevived.Settings;

public partial class SettingsPage
{

    // ===== 背景类型 =====

    /// <summary>背景类型：0 提取当前壁纸 / 1 纯色背景 / 2 自定义图片 / 3 实时模糊 / 4 网页背景。</summary>
    public int BackgroundKindIndex
    {
        get => _settings.BackgroundKind;
        set
        {
            var clamped = Math.Clamp(value, 0, (int)BackgroundKind.WebPage);
            if (_settings.BackgroundKind == clamped) return;

            // 网页背景依赖系统里的 WebView2 运行时：没装时直接拦下这一档，
            // 否则用户选完看到的是「透明 + 纯色兜底」，会以为插件坏了。
            // 只拦这一档，其它档位照常切换。
            if ((BackgroundKind)clamped == BackgroundKind.WebPage && !WebView2Availability.IsAvailable)
            {
                BackgroundKindHint = WebView2Availability.UnavailableReason;
                OnPropertyChanged(nameof(BackgroundKindHint));
                // 设置没变，但 ComboBox 已经把自己挪到新档了：再通知一次让绑定拉回旧值
                OnPropertyChanged(nameof(BackgroundKindIndex));
                return;
            }

            _settings.BackgroundKind = clamped;
            OnPropertyChanged(nameof(BackgroundKindIndex));
            // 1.4 起有五档，各露各的行：统一在一处通知，避免以后再加档位漏发
            NotifyBackgroundKindVisibility();
            SchedulePreviewSync();
        }
    }

    /// <summary>切档后把「哪几行该露出来」统一通知一遍（可见性都是直接读设置的 getter）。</summary>
    private void NotifyBackgroundKindVisibility()
    {
        OnPropertyChanged(nameof(IsWallpaperBackground));
        OnPropertyChanged(nameof(IsSolidColorBackground));
        OnPropertyChanged(nameof(IsCustomImageBackground));
        OnPropertyChanged(nameof(IsNativeBlurBackground));
        OnPropertyChanged(nameof(IsWebBackground));
        OnPropertyChanged(nameof(IsTransparentBackground));
    }

    /// <summary>当前背景是不是「提取当前桌面壁纸」——景深放大、模糊半径两行只在此时露出。</summary>
    public bool IsWallpaperBackground => _settings.BackgroundKind == (int)BackgroundKind.Wallpaper;

    /// <summary>当前背景是不是纯色——背景颜色行只在此时露出。</summary>
    public bool IsSolidColorBackground => _settings.BackgroundKind == (int)BackgroundKind.SolidColor;

    /// <summary>当前背景是不是用户导入的那张图——背景图片、填充方式两行只在此时露出。</summary>
    public bool IsCustomImageBackground => _settings.BackgroundKind == (int)BackgroundKind.CustomImage;

    /// <summary>实时模糊：不画自己的底，交给 Windows 原生把窗口后面的桌面实时模糊掉。</summary>
    public bool IsNativeBlurBackground => _settings.BackgroundKind == (int)BackgroundKind.NativeBlur;

    /// <summary>网页背景：WebView2 在独立的下层窗口里画网页，组件叠在网页上。</summary>
    public bool IsWebBackground => _settings.BackgroundKind == (int)BackgroundKind.WebPage;

    /// <summary>实时模糊 / 网页：大屏窗口需要透明（背景层不铺自己的底）。</summary>
    public bool IsTransparentBackground => IsNativeBlurBackground || IsWebBackground;

    /// <summary>
    /// 背景档位的提示文案：只在「切换被拦下」时给出原因（例如 WebView2 不可用）。
    /// 正常切换时保持空串，设置页不必显示任何提示。
    /// </summary>
    public string BackgroundKindHint { get; private set; } = "";

    // ===== 壁纸：景深放大 + 模糊半径 =====

    /// <summary>景深放大：壁纸放大多少倍来铺满（1.0 ~ 1.6），越大边缘裁得越多、越不容易露出黑边。</summary>
    public double WallpaperZoom
    {
        get => _settings.WallpaperZoom;
        set
        {
            if (Math.Abs(_settings.WallpaperZoom - value) < 0.001) return;
            _settings.WallpaperZoom = value;
            OnPropertyChanged(nameof(WallpaperZoom));
            OnPropertyChanged(nameof(WallpaperZoomText));
            SchedulePreviewSync();
        }
    }

    public string WallpaperZoomText => $"{_settings.WallpaperZoom:0.##}×";

    /// <summary>壁纸模糊半径（0 ~ 64，0 = 不模糊）。半径按屏幕宽度换算，大屏小屏观感一致。</summary>
    public double WallpaperBlurRadius
    {
        get => _settings.WallpaperBlurRadius;
        set
        {
            if (Math.Abs(_settings.WallpaperBlurRadius - value) < 0.01) return;
            _settings.WallpaperBlurRadius = value;
            OnPropertyChanged(nameof(WallpaperBlurRadius));
            OnPropertyChanged(nameof(WallpaperBlurRadiusText));
            SchedulePreviewSync();
        }
    }

    public string WallpaperBlurRadiusText => $"{_settings.WallpaperBlurRadius:0}";

    /// <summary>开屏/退场动画速率（0.5 ~ 2.0 倍，越大越快，1.0 = 默认时长）。</summary>
    public double AnimationSpeed
    {
        get => _settings.AnimationSpeed;
        set
        {
            if (Math.Abs(_settings.AnimationSpeed - value) < 0.005) return;
            _settings.AnimationSpeed = value;
            OnPropertyChanged(nameof(AnimationSpeed));
            OnPropertyChanged(nameof(AnimationSpeedText));
        }
    }

    public string AnimationSpeedText => $"{_settings.AnimationSpeed:0.00}×";

    // ===== 自定义图片 =====

    /// <summary>当前背景图的文件名；没选就是空。</summary>
    public string BackgroundImageSummary
        => BackgroundImageStore.ResolvePath(_settings.BackgroundImageFile) is null
            ? "未选择（退回纯色底）"
            : _settings.BackgroundImageFile;

    public string BackgroundImageHint { get; private set; } = "选中的图片会复制一份到插件配置目录，原图之后挪走也不影响";

    /// <summary>填充方式：0 铺满裁切 / 1 完整显示 / 2 拉伸。</summary>
    public int BackgroundImageStretchIndex
    {
        get => _settings.BackgroundImageStretch;
        set
        {
            if (_settings.BackgroundImageStretch == value) return;
            _settings.BackgroundImageStretch = value;
            OnPropertyChanged(nameof(BackgroundImageStretchIndex));
            SchedulePreviewSync();
        }
    }

    private async void PickBackgroundImage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (TopLevel.GetTopLevel(this) is not { } top) return;

            var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择背景图片",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("图片")
                    {
                        Patterns = ["*.jpg", "*.jpeg", "*.png", "*.bmp", "*.webp", "*.gif"],
                    },
                ],
            });

            if (files.Count == 0) return;
            var source = files[0].TryGetLocalPath();
            if (string.IsNullOrWhiteSpace(source) || !File.Exists(source)) return;

            BackgroundImageStore.Replace(source, _settings);
            BackgroundImageHint = $"已导入背景图：{Path.GetFileName(source)}";
            NotifyBackgroundChanged();
        }
        catch (Exception ex)
        {
            BackgroundImageHint = "导入失败：" + ex.Message;
            OnPropertyChanged(nameof(BackgroundImageHint));
        }
    }

    private void ClearBackgroundImage_Click(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        BackgroundImageStore.Clear(_settings);
        BackgroundImageHint = "已清除，退回纯色底";
        NotifyBackgroundChanged();
    }

    private void NotifyBackgroundChanged()
    {
        OnPropertyChanged(nameof(BackgroundImageSummary));
        OnPropertyChanged(nameof(BackgroundImageHint));
        RefreshThemeFromBackground();   // 「自动」主题档要按新图重判一次
    }

    // ===== 网页背景地址（1.4.0.0） =====

    /// <summary>
    /// 网页背景地址。写入前统一清洗：没写协议补 https://，非 http/https/file/about 的协议
    /// （javascript: 之类）不接受——设置页和 BackgroundExtrasPanel 都走同一个入口，
    /// 免得两处各写一套校验、行为不一致。
    /// </summary>
    public string BackgroundWebUrl
    {
        get => _settings.BackgroundWebUrl;
        set
        {
            var normalized = WebViewHost.NormalizeUrl(value);
            if (normalized.Length == 0) return;
            if (string.Equals(_settings.BackgroundWebUrl, normalized, StringComparison.Ordinal)) return;
            _settings.BackgroundWebUrl = normalized;
            OnPropertyChanged(nameof(BackgroundWebUrl));
        }
    }

    // ===== 背景明暗 =====

    /// <summary>背景明暗：负数把整张背景压暗、正数提亮（0 = 不调），保证背景再花字也读得清。</summary>
    public double BackgroundDim
    {
        get => _settings.BackgroundDim;
        set
        {
            if (Math.Abs(_settings.BackgroundDim - value) < 0.01) return;
            _settings.BackgroundDim = value;
            OnPropertyChanged(nameof(BackgroundDim));
            OnPropertyChanged(nameof(BackgroundDimText));
            RefreshThemeFromBackground();
        }
    }

    public string BackgroundDimText => _settings.BackgroundDim switch
    {
        > 0.5 => $"提亮 {_settings.BackgroundDim:0}%",
        < -0.5 => $"压暗 {-_settings.BackgroundDim:0}%",
        _ => "不调",
    };
}
