// ===== SettingsPage 拆分之一：背景（壁纸 / 纯色 / 自定义图片） =====
// 职责：背景类型档位（提取当前壁纸 / 纯色 / 自定义图片）与三种类型的行可见性、
//       壁纸的景深放大与模糊半径、自定义图片的选择/清除/提示文案与填充方式、背景明暗遮罩。
//       成员与逻辑从原 SettingsPage.axaml.cs 原样搬移。
// 注意：纯色背景的 ColorPicker 颜色成员（BackgroundColorValue / BackgroundColorBrush / BackgroundColorHex）
//       在 SettingsPage.Appearance.cs 里，与其它颜色行共用同一套 hex 存取。

using Avalonia.Controls;
using Avalonia.Platform.Storage;
using LargeScreenClock.Models;

namespace LargeScreenClock.Settings;

public partial class SettingsPage
{

    // ===== 背景类型 =====

    /// <summary>背景类型：0 提取当前壁纸 / 1 纯色背景 / 2 自定义图片。</summary>
    public int BackgroundKindIndex
    {
        get => _settings.BackgroundKind;
        set
        {
            if (_settings.BackgroundKind == value) return;
            _settings.BackgroundKind = value;
            OnPropertyChanged(nameof(BackgroundKindIndex));
            // 三种类型各露各的行，换档时三个可见性都得重新通知一遍
            OnPropertyChanged(nameof(IsWallpaperBackground));
            OnPropertyChanged(nameof(IsSolidColorBackground));
            OnPropertyChanged(nameof(IsCustomImageBackground));
            SchedulePreviewSync();
        }
    }

    /// <summary>当前背景是不是「提取当前桌面壁纸」——景深放大、模糊半径两行只在此时露出。</summary>
    public bool IsWallpaperBackground => _settings.BackgroundKind == (int)BackgroundKind.Wallpaper;

    /// <summary>当前背景是不是纯色——背景颜色行只在此时露出。</summary>
    public bool IsSolidColorBackground => _settings.BackgroundKind == (int)BackgroundKind.SolidColor;

    /// <summary>当前背景是不是用户导入的那张图——背景图片、填充方式两行只在此时露出。</summary>
    public bool IsCustomImageBackground => _settings.BackgroundKind == (int)BackgroundKind.CustomImage;

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
