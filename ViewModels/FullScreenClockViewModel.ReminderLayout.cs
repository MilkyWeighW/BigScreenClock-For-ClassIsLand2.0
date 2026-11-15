// =============================================================================
// FullScreenClockViewModel（partial）— 提醒首行合并布局与颜文字副标题
// 职责：顶部第一行「主位块 + 降雨/倒计时/文本」按渲染宽度合并的判定与阈值、
// 面板/课程信息/地震块的最大宽度约束，以及气温/日期/倒计时旁的颜文字副标题。
// 本类按职责拆成 6 个 partial 文件（同一 namespace，均声明 partial class + INotifyPropertyChanged）：
//   FullScreenClockViewModel.cs                   核心：字段、构造、基础属性、设置变更、主题与外观
//   FullScreenClockViewModel.Lifecycle.cs         窗口生命周期、定时器与每秒刷新循环
//   FullScreenClockViewModel.Course.cs            课表加载、课程定位与打铃时间
//   FullScreenClockViewModel.Noise.cs             噪音统计、音量条、计数片段与「正在记录」
//   FullScreenClockViewModel.Reminders.cs         提醒面板、天气/预警数据、地震提醒
//   FullScreenClockViewModel.ReminderLayout.cs    提醒首行合并布局与颜文字副标题
// 成员只是原样搬移（未改逻辑/可见性/命名），每个字段/成员只在一处声明。
// =============================================================================

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Timers;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Media.TextFormatting;
using Avalonia.Styling;
using Avalonia.Threading;
using ClassIsland.Core;
using ClassIsland.Core.Abstractions.Services;
using ClassIsland.Core.Icons;
using ClassIsland.Core.Models.Components;
using ClassIsland.Shared.Enums;
using LargeScreenClockRevived.Helpers;
using LargeScreenClockRevived.Models;
using LargeScreenClockRevived.NoiseDetection;
using LargeScreenClockRevived.Services;
using LargeScreenClockRevived.Views;
using Microsoft.Extensions.DependencyInjection;

namespace LargeScreenClockRevived.ViewModels;

public partial class FullScreenClockViewModel : INotifyPropertyChanged
{
    // ===== 提醒自动合并为同行（顶部第一行 = 主位块 + 预警详情弹幕 + 日期） =====
    // 用户定稿规则：
    // - 天气固定第一行，与弹幕同行；
    // - 与天气合并的优先级：降雨提醒 > 倒计时 > 文本，能放进屏幕 1/3 宽才合并，放不下该项留在提醒面板；
    // - 无天气时，第一行主位由倒计时顶替，再无就文本框；
    // - 天气预警始终单开一行/多行（面板）；
    // - 颜文字（副标题）显示在主提醒的正下方。

    private bool _showWeatherInFirstRow;
    private bool _showRainMergedInFirstRow;
    private bool _showCountdownMergedInFirstRow;
    private bool _showTextMergedInFirstRow;

    /// <summary>天气是否显示在第一行（有天气数据时恒为第一行主位）。</summary>
    public bool ShowWeatherInFirstRow
    {
        get => _showWeatherInFirstRow;
        private set
        {
            if (_showWeatherInFirstRow == value) return;
            _showWeatherInFirstRow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>降雨提醒是否并入第一行。</summary>
    public bool ShowRainMergedInFirstRow
    {
        get => _showRainMergedInFirstRow;
        private set
        {
            if (_showRainMergedInFirstRow == value) return;
            _showRainMergedInFirstRow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>倒计时是否并入第一行（无天气时作为第一行主位）。</summary>
    public bool ShowCountdownMergedInFirstRow
    {
        get => _showCountdownMergedInFirstRow;
        private set
        {
            if (_showCountdownMergedInFirstRow == value) return;
            _showCountdownMergedInFirstRow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>文本是否并入第一行（无天气且无倒计时时作为第一行主位）。</summary>
    public bool ShowTextMergedInFirstRow
    {
        get => _showTextMergedInFirstRow;
        private set
        {
            if (_showTextMergedInFirstRow == value) return;
            _showTextMergedInFirstRow = value;
            OnPropertyChanged();
        }
    }

    /// <summary>合并判定阈值：主位块 + 并入项总宽不超过「屏幕宽 × 1/3」才并入（窗口 SizeChanged 设置）。</summary>
    public double MergeThreshold
    {
        get => _mergeThreshold;
        set
        {
            if (Math.Abs(_mergeThreshold - value) < 0.5) return;
            _mergeThreshold = value;
            OnPropertyChanged();
            UpdateReminderMerge();   // 阈值变化会改变是否并入
        }
    }
    private double _mergeThreshold = 640;

    /// <summary>按实际渲染宽度重算第一行合并：天气固定，降雨>倒计时>文本 逐项尝试并入（≤1/3 屏）。</summary>
    private void UpdateReminderMerge()
    {
        bool weather = ShowWeatherRow;
        bool rain = ShowRainReminder;
        bool countdown = ShowCountdownRow;
        bool text = ShowTextRow;

        bool rainMerged = false, countdownMerged = false, textMerged = false;
        const double spacing = 36;   // 与 XAML 里第一行 StackPanel 的 Spacing 保持一致（玻璃底要占位置）
        var threshold = MergeThreshold;

        if (weather)
        {
            var used = IconWidth(ComponentFontSizeOf("weather")) + 6 + TextWidthFor("weather", WeatherText);
            // 合并优先级：降雨提醒 > 倒计时 > 文本
            TryMerge(used, rain, rain ? IconWidth(ComponentFontSizeOf("rainMerge")) + 6 + TextWidthFor("rainMerge", RainReminderTitle) : 0,
                spacing, threshold, ref used, ref rainMerged);
            TryMerge(used, countdown, countdown ? IconWidth(ComponentFontSizeOf("countdownMerge")) + 6 + TextWidthFor("countdownMerge", CountdownText) : 0,
                spacing, threshold, ref used, ref countdownMerged);
            TryMerge(used, text, text ? TextWidthFor("textMerge", ReminderText) : 0,
                spacing, threshold, ref used, ref textMerged);
        }
        else if (rain)
        {
            // 没有天气时降雨才是第一行的主位（README：降雨 > 倒计时 > 文本）。
            // 旧实现把降雨合并写死在 weather 分支里，无天气时降雨永远进不了第一行。
            rainMerged = true;
        }
        else if (countdown)
        {
            countdownMerged = true;   // 无天气：第一行主位由倒计时顶替
        }
        else if (text)
        {
            textMerged = true;        // 再无就文本框
        }

        ShowWeatherInFirstRow = weather;
        ShowRainMergedInFirstRow = rainMerged;
        ShowCountdownMergedInFirstRow = countdownMerged;
        ShowTextMergedInFirstRow = textMerged;

        // 面板项（未并入的）与总开关重新求值
        OnPropertyChanged(nameof(ShowRainReminderPanel));
        OnPropertyChanged(nameof(ShowCountdownPanel));
        OnPropertyChanged(nameof(ShowTextPanel));
        OnPropertyChanged(nameof(ShowReminderPanel));
    }

    /// <summary>尝试把某项并入主位块：当前已用宽度 + 间距 + 该项宽 ≤ 阈值 才并入。</summary>
    private static void TryMerge(double used, bool candidate, double candidateWidth,
        double spacing, double threshold, ref double usedOut, ref bool merged)
    {
        if (!candidate || candidateWidth <= 0) return;
        if (used + spacing + candidateWidth <= threshold)
        {
            usedOut = used + spacing + candidateWidth;
            merged = true;
        }
    }

    /// <summary>提醒面板最大宽度：由窗口按「屏幕宽 × 2/3」设置，只约束提醒面板（降雨/文本/预警）。</summary>
    public double ReminderPanelMaxWidth
    {
        get => _reminderPanelMaxWidth;
        set
        {
            if (Math.Abs(_reminderPanelMaxWidth - value) < 0.5) return;
            _reminderPanelMaxWidth = value;
            OnPropertyChanged();
        }
    }
    private double _reminderPanelMaxWidth = 520;

    /// <summary>
    /// 底部课程信息的最大宽（窗口按「整行宽 × 1/2」设置）。超过这个宽就交给 Viewbox 等比缩字，
    /// 不再被列宽裁掉——开了「显示精确打铃时间」后这行会明显变长，正中那列又不好压缩。
    /// 默认无穷大：窗口还没量出尺寸前不设限，免得一上来被压成 0 宽。
    /// </summary>
    public double CourseInfoMaxWidth
    {
        get => _courseInfoMaxWidth;
        set
        {
            if (Math.Abs(_courseInfoMaxWidth - value) < 0.5) return;
            _courseInfoMaxWidth = value;
            OnPropertyChanged();
        }
    }
    private double _courseInfoMaxWidth = double.PositiveInfinity;

    /// <summary>
    /// 地震提醒块的最大宽（窗口按「整窗宽去掉页面左右边距」设置）。两行文字太长时交给 Viewbox 等比缩字，
    /// 保证整个提醒都塞得进屏幕。默认无穷大：窗口还没量出尺寸前不设限。
    /// </summary>
    public double EarthquakeAlertMaxWidth
    {
        get => _earthquakeAlertMaxWidth;
        set
        {
            if (Math.Abs(_earthquakeAlertMaxWidth - value) < 0.5) return;
            _earthquakeAlertMaxWidth = value;
            OnPropertyChanged();
        }
    }
    private double _earthquakeAlertMaxWidth = double.PositiveInfinity;

    // ===== 颜文字副标题（趣味提醒）：气温/天气旁、日期旁、倒计时旁、降雨旁的小字号副标题 =====
    // 由「颜文字提醒」开关统一控制；关闭时全部清空、保留正文字幕。固定文本不轮换。

    /// <summary>距最近降雨的小时数（0=正在下；null=未来无雨或未读到）。带伞副标题只在 6 小时内显示。</summary>
    private int? _rainHours;

    /// <summary>降雨提醒副标题：距离下雨 ≤6 小时才显示「记得带伞哦」（超过 6 小时只留主标题）；
    /// 开关开启带颜文字，否则纯文字。</summary>
    public string? RainSubtitle
    {
        get
        {
            if (_rainHours is not (>= 0 and <= 6)) return null;
            return _settings.ShowEmojiSubtitles ? "记得带伞哦 (ノω≦)" : "记得带伞哦";
        }
    }
    public bool HasRainSubtitle => RainSubtitle is not null;

    private string? _weatherCode;
    private double? _weatherTempC;

    /// <summary>雪/雨夹雪天气码（下雪优先显示保暖副标题，压过温度提示）。</summary>
    private static readonly HashSet<string> SnowCodes = new() { "06", "13", "14", "15", "16", "17", "26", "27", "28" };
    /// <summary>雾/霾天气码。</summary>
    private static readonly HashSet<string> FogCodes = new() { "18", "22", "32" };

    private string? _weatherSubtitle;
    public string? WeatherSubtitle
    {
        get => _weatherSubtitle;
        private set
        {
            if (_weatherSubtitle == value) return;
            _weatherSubtitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasWeatherSubtitle));
        }
    }
    public bool HasWeatherSubtitle => !string.IsNullOrEmpty(_weatherSubtitle);

    private string? _dateSubtitle;
    public string? DateSubtitle
    {
        get => _dateSubtitle;
        private set
        {
            if (_dateSubtitle == value) return;
            _dateSubtitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasDateSubtitle));
        }
    }
    public bool HasDateSubtitle => !string.IsNullOrEmpty(_dateSubtitle);

    private string? _countdownSubtitle;
    public string? CountdownSubtitle
    {
        get => _countdownSubtitle;
        private set
        {
            if (_countdownSubtitle == value) return;
            _countdownSubtitle = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCountdownSubtitle));
        }
    }
    public bool HasCountdownSubtitle => !string.IsNullOrEmpty(_countdownSubtitle);

    /// <summary>气温旁副标题：雪 > 雾霾 > 高温 > 低温，取最先命中的一项。</summary>
    private void UpdateWeatherSubtitle()
    {
        string? s = null;
        if (_settings.ShowEmojiSubtitles)
        {
            if (_weatherCode is { } code)
            {
                if (SnowCodes.Contains(code)) s = "注意保暖 (❁´ω`❁)";
                else if (FogCodes.Contains(code)) s = "出门戴口罩 (´-ω-`)";
            }
            if (s is null && _weatherTempC is { } t)
            {
                if (t >= 36) s = "天太热，多喝水 (￣△￣;)";
                else if (t >= 33) s = "记得防晒 (っ'ω')ﾉ";
                else if (t <= 0) s = "冻手冻脚，注意保暖 (｡-_-｡)";
                else if (t <= 8) s = "天冷多穿点 (｡>ㅅ<｡)";
            }
        }
        WeatherSubtitle = s;
    }

    /// <summary>日期下方副标题：深夜 > 周五 > 月初（每秒刷新，到点自动切换）。</summary>
    private void UpdateDateSubtitle()
    {
        string? s = null;
        if (_settings.ShowEmojiSubtitles)
        {
            // 副标题挂在显示的日期下面，用与读数相同的 CI 时间轴，免得零点前后两者对不上
            var now = CiTimeService.Now;
            if (now.DayOfWeek == DayOfWeek.Thursday) s = "撑住！明天就能回家了！ (๑•̀ㅂ•́)و";
            else if (now.Day == 1) s = "新的一个月，加油 (ง •̀_•́)ง";
        }
        DateSubtitle = s;
    }

    /// <summary>倒计时旁副标题：剩余 ≤30 天进入冲刺提示。</summary>
    private void UpdateCountdownSubtitle()
    {
        string? s = null;
        if (_settings.ShowEmojiSubtitles && _reminderData.CountdownRemaining is { } remaining)
        {
            var digits = new string(remaining.Where(char.IsDigit).ToArray());
            if (int.TryParse(digits, out var days) && days is > 0 and <= 30)
                s = "冲刺啦，冲鸭 (๑•̀ㅂ•́)و✧";
        }
        CountdownSubtitle = s;
    }

    /// <summary>从 CI 温度字符串（可能带 ℃/负号）解析数值，失败返回 null。</summary>
    private static double? ParseTemp(string? t)
    {
        if (string.IsNullOrEmpty(t)) return null;
        var s = new string(t.Where(c => char.IsDigit(c) || c == '-').ToArray());
        return double.TryParse(s, out var v) ? v : null;
    }

    /// <summary>
    /// 用 TextLayout 独立测量文本实际渲染宽度（不依赖可视树布局，中英文混合也准确）。
    /// 字体族可传：1.4.0.0 起组件能单独设字体，第一行合并判定必须用同一个字体量，
    /// 否则「用户把字体换成更宽的一支」之后，估算与实际渲染会对不上。
    /// </summary>
    private static double TextWidth(string? s, double fontSize, string? fontFamily = null)
        => string.IsNullOrEmpty(s)
            ? 0
            : new TextLayout(s,
                new Typeface(string.IsNullOrWhiteSpace(fontFamily) ? "Microsoft YaHei" : fontFamily),
                fontSize, null, TextAlignment.Left, TextWrapping.NoWrap).Width;

    /// <summary>组件实际字号：组件自己设了字号用它的，否则用默认的两级字号之一（这里默认 24）。</summary>
    private double ComponentFontSizeOf(string componentId, double fallback = 24)
    {
        var size = _settings.GetComponentLayout(componentId).FontSize;
        return size > 0 ? size : fallback;
    }

    /// <summary>按组件自己的字体族 + 字号量文本宽度（组件没设就等价于原来的固定测量）。</summary>
    private double TextWidthFor(string componentId, string? s)
    {
        var layout = _settings.GetComponentLayout(componentId);
        return TextWidth(s, ComponentFontSizeOf(componentId), layout.FontFamily);
    }

    /// <summary>
    /// 矢量图标宽度：Lucide 图标为正方形，字宽≈字号（不能用 TextWidth 量——图标字形落在
    /// 私有区，用中文字体量会得到错误的宽度，导致第一行合并判定失准）。
    /// </summary>
    private static double IconWidth(double fontSize) => fontSize;
}
