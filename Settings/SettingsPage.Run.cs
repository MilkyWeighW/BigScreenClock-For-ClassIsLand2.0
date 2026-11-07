using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using FluentAvalonia.UI.Controls;

namespace LargeScreenClockRevived.Settings;

/// <summary>
/// 「运行」页的一键重置。
/// 重置的是整份插件设置（组件开关/顺序/大小位置、颜色字号、音量阈值、背景、运行设置……），
/// 而不仅是这一页上的两项——所以入口放在「运行」页、描述里写清楚范围，并且先弹确认框。
/// </summary>
public partial class SettingsPage
{
    private async void ResetAllSettings_Click(object? sender, RoutedEventArgs e)
    {
        var root = TopLevel.GetTopLevel(this);
        if (root is null) return;

        var dialog = new TaskDialog
        {
            Header = "重置所有设置？",
            Content = "组件开关与顺序、大小位置、颜色字号、音量阈值、背景和运行设置都会恢复默认值，重置后无法撤销。",
            XamlRoot = root,
            Buttons =
            {
                new TaskDialogButton("取消", false),
                new TaskDialogButton("重置", true) { IsDefault = true },
            },
        };

        var result = await dialog.ShowAsync();
        if (result is not bool confirmed || !confirmed) return;

        // 模型侧：整份设置回到默认（内部逐项触发 PropertyChanged，大屏与预览会跟着刷新）
        _settings.ResetAllToDefaults();

        // 页面侧：组件行绑的是旧的布局实例、课程列表绑的是旧的集合，都得重建
        RefreshComponentItemsAfterReset();
        LoadAllCourseNames();
        RestoreSelectedCourses();
        LoadCameraDevices();

        // 这一页大量属性是「转发到 settings」的包装器：设置变了它们自己不会发通知，统一刷一遍
        NotifyAllSettingsBindings();
        NotifyAllColorsChanged();
        SchedulePreviewSync();
    }

    /// <summary>把页面上所有公开属性的 PropertyChanged 重发一次（重置后让全部绑定重新取值）。</summary>
    private void NotifyAllSettingsBindings()
    {
        foreach (var property in GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (property.GetIndexParameters().Length > 0) continue;
            OnPropertyChanged(property.Name);
        }
    }
}
