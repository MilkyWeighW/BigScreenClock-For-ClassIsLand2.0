// =============================================================================
// FullScreenClockViewModel（partial）— 提醒面板、天气/预警数据与地震提醒
// 职责：CI 组件（倒计时/文本框）与天气缓存的读取、预警列表合成与着色、
// 提醒面板可见性合成、地震提醒的每秒粘滞更新，以及提醒数据变更的统一通知。
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
    // ===== 提醒面板（左上角）：天气 / 降雨 / 预警 / 倒计时 / 文本框，跟随 CI 配置 =====

    /// <summary>天气图标字形（无数据时用温度计兜底）。</summary>
    public string WeatherIcon => _reminderData.WeatherIcon ?? IconGlyph.Of(LucideIconKind.Thermometer);

    private string _rainReminderTitle = "";

    /// <summary>未来降雨提醒主标题（如「20小时内当前地区有降雨」）；无雨为空串。副标题「记得带伞哦」界面固定。</summary>
    public string RainReminderTitle
    {
        get => _rainReminderTitle;
        set
        {
            if (_rainReminderTitle == value) return;
            _rainReminderTitle = value;
            OnPropertyChanged(nameof(RainReminderTitle));
            OnPropertyChanged(nameof(ShowRainReminder));
        }
    }

    /// <summary>降雨提醒可见性：跟随独立「多久下雨」开关，且有降雨数据。</summary>
    public bool ShowRainReminder => _settings.ShowRainReminder && !string.IsNullOrEmpty(_rainReminderTitle);
    public string WeatherText => _reminderData.WeatherText ?? "";
    public string ReminderText => _reminderData.TextContent ?? "";

    /// <summary>倒计时整行文本（如「高考   还有 30 天」）。</summary>
    public string CountdownText
    {
        get
        {
            var title = _reminderData.CountdownTitle;
            var remaining = _reminderData.CountdownRemaining;
            if (string.IsNullOrEmpty(title)) return remaining ?? "";
            return string.IsNullOrEmpty(remaining) ? title : $"{title}   {remaining}";
        }
    }

    /// <summary>预警列表（按等级着色）。</summary>
    public ObservableCollection<AlertDisplayItem> AlertItems { get; } = new();

    /// <summary>当前展开预警的详情全文（顶部弹幕带显示；点「>」展开时设置，收起或刷新时清空）。</summary>
    private string? _expandedAlertDetail;
    public string? ExpandedAlertDetail
    {
        get => _expandedAlertDetail;
        set
        {
            if (_expandedAlertDetail == value) return;
            _expandedAlertDetail = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasExpandedAlert));
        }
    }
    public bool HasExpandedAlert => !string.IsNullOrEmpty(_expandedAlertDetail);

    // 组合可见性：子开关 && 有数据
    public bool ShowWeatherRow => _settings.ShowWeatherReminder && _reminderData.HasWeather;
    // 预警开关的过滤在 UpdateAlertItems() 里做（摄像头占用那条不受天气「预警」开关影响）
    public bool ShowAlertsRow => _reminderData.HasAlerts;
    public bool ShowCountdownRow => _settings.ShowCountdownReminder && _reminderData.HasCountdown;
    public bool ShowTextRow => _settings.ShowTextReminder && _reminderData.HasText;

    // 提醒面板项：未并入第一行的 降雨/倒计时/文本 才在面板显示（天气永不在面板，预警始终在面板）
    public bool ShowRainReminderPanel => ShowRainReminder && !ShowRainMergedInFirstRow;
    public bool ShowCountdownPanel => ShowCountdownRow && !ShowCountdownMergedInFirstRow;
    public bool ShowTextPanel => ShowTextRow && !ShowTextMergedInFirstRow;
    public bool ShowReminderPanel => _settings.ShowReminderPanel
        && (ShowAlertsRow || ShowRainReminderPanel || ShowCountdownPanel || ShowTextPanel);

    // ===== 地震提醒（屏幕上方弹出，内容读自「地震预警」插件） =====
    // 每分钟的行程另有其人：数据由 EarthquakeReader 反射取，组装规则在 EarthquakeAlertBuilder（纯函数）。

    /// <summary>当前这场地震的提醒；没有正在预警的地震时为 null。</summary>
    private EarthquakeAlert? _earthquakeAlert;

    /// <summary>这场地震的横波到达时刻。一旦开始显示就守着它倒数到点，中途读不到数据也不收。</summary>
    private DateTime _earthquakeEndTime;

    /// <summary>正在显示的这场地震是谁（对方的事件标识）。用来认出「对方换了一场」。</summary>
    private string? _earthquakeEventKey;

    /// <summary>是否在时钟正上方弹出地震提醒（开关开着且有正在预警的地震）。</summary>
    public bool ShowEarthquakeAlert => _settings.ShowEarthquakeReminder && _earthquakeAlert is not null;

    /// <summary>第一行：横波还有 X 秒到达。</summary>
    public string EarthquakeLine1 => _earthquakeAlert?.Line1 ?? "";

    /// <summary>第二行：震中 + 震级。</summary>
    public string EarthquakeLine2 => _earthquakeAlert?.Line2 ?? "";

    /// <summary>等级色（本地烈度分档，与地震预警插件同一套）：两行文字、描边、底色都用它。</summary>
    public IBrush EarthquakeAlertBrush => QuakeBrush(0xFF);

    /// <summary>提醒块底色：等级色的低透明版（强调用，不盖住文字）。</summary>
    public IBrush EarthquakeAlertBackground => QuakeBrush(Chrome.IsLight ? (byte)0x22 : (byte)0x33);

    /// <summary>提醒块描边：等级色的半透明版。</summary>
    public IBrush EarthquakeAlertBorder => QuakeBrush(Chrome.IsLight ? (byte)0x88 : (byte)0x99);

    /// <summary>第一行（倒计时）字号：随时钟字号缩放，太长时由 Viewbox 再缩。</summary>
    public double EarthquakeLine1FontSize => Math.Clamp(ClockFontSize * 0.18, 26, 96);

    /// <summary>第二行（震中/震级）字号。</summary>
    public double EarthquakeLine2FontSize => Math.Clamp(ClockFontSize * 0.12, 18, 64);

    private IBrush QuakeBrush(byte alpha)
    {
        var hex = SemanticColors.EarthquakeHex(_earthquakeAlert?.Intensity ?? 0, Chrome.IsLight);
        var color = Color.Parse(hex);
        return new SolidColorBrush(Color.FromArgb(alpha, color.R, color.G, color.B));
    }

    /// <summary>
    /// 地震提醒：每秒从「地震预警」插件读一次（横波倒计时每秒都在变）。
    /// 内容没变就不发通知，免得每秒都重排一次布局。
    ///
    /// 一旦开始显示就「粘」住：记下横波到达时刻，之后哪怕某一秒读不到数据（对方正在更新记录、
    /// 或我们这边正好在改设置重排），也照着自己的节拍把倒计时数到点才收——不会中途闪断。
    ///
    /// 但粘住的范围只限「同一场地震」：对方换了另一场（比如测试里从四川换成云南，
    /// 或前一报已经了结）就得当场收起，不能抱着上一场往下数（用户 2026-10-01 反馈）。
    /// 靠对方的事件标识分辨这两件事。
    /// </summary>
    private void UpdateEarthquakeAlert()
    {
        var now = DateTime.Now;

        if (!_settings.ShowEarthquakeReminder)
        {
            ClearEarthquakeAlert();
            return;
        }

        var reading = _earthquakeReader.Read(now);
        if (reading.Alert is { } next)
        {
            _earthquakeEventKey = reading.EventKey ?? _earthquakeEventKey;
            _earthquakeEndTime = now.AddSeconds(next.SecondsLeft);
            if (next == _earthquakeAlert) return;   // 文案没变（同一秒内被调了两次）
            _earthquakeAlert = next;
            NotifyEarthquakeChanged();
            return;
        }

        // 这一拍没有要提醒的地震。没在显示的就算了；正在显示的得先看对方是不是换了一场
        if (_earthquakeAlert is null) return;
        if (reading.EventKey is not null && reading.EventKey != _earthquakeEventKey)
        {
            ClearEarthquakeAlert();
            return;
        }

        // 还是同一场、只是这拍没读到：接着数，数到横波到达才收
        var left = (int)Math.Round((_earthquakeEndTime - now).TotalSeconds);
        if (left < 1)
        {
            ClearEarthquakeAlert();
            return;
        }

        if (left == _earthquakeAlert.SecondsLeft) return;
        _earthquakeAlert = _earthquakeAlert.WithSecondsLeft(left);
        NotifyEarthquakeChanged();
    }

    private void ClearEarthquakeAlert()
    {
        _earthquakeEventKey = null;
        if (_earthquakeAlert is null) return;
        _earthquakeAlert = null;
        NotifyEarthquakeChanged();
    }

    private void NotifyEarthquakeChanged()
    {
        OnPropertyChanged(nameof(ShowEarthquakeAlert));
        OnPropertyChanged(nameof(EarthquakeLine1));
        OnPropertyChanged(nameof(EarthquakeLine2));
        OnPropertyChanged(nameof(EarthquakeAlertBrush));
        OnPropertyChanged(nameof(EarthquakeAlertBackground));
        OnPropertyChanged(nameof(EarthquakeAlertBorder));
        OnPropertyChanged(nameof(EarthquakeLine1FontSize));
        OnPropertyChanged(nameof(EarthquakeLine2FontSize));
    }

    /// <summary>进入全屏时立即同步一次提醒数据（不等定时器）。</summary>
    private void RefreshRemindersNow()
    {
        RefreshComponents();
        RefreshWeather();
    }

    /// <summary>从 CI 组件配置读取倒计时/文本框（每 3 秒）；用户增删组件自动跟随。</summary>
    private void RefreshComponents()
    {
        try
        {
            var compService = _serviceProvider.GetService<IComponentsService>();
            var profile = compService?.CurrentComponents;
            if (profile is null) return;

            string? title = null, remaining = null;
            var texts = new List<string>();
            foreach (var line in profile.Lines)
                foreach (var child in line.Children)
                    CollectComponents(child, ref title, ref remaining, texts);

            _reminderData.CountdownTitle = title;
            _reminderData.CountdownRemaining = remaining;
            _reminderData.TextContent = texts.Count > 0 ? string.Join("\n", texts) : null;
            NotifyReminderChanged();
        }
        catch { /* CI 组件服务暂不可用时保持上次数据 */ }
    }

    /// <summary>递归收集组件设置里的倒计时/文本框（容器组件也遍历 Children）。</summary>
    /// <remarks>
    /// 不过滤 <see cref="ComponentSettings.IsActive"/>：实测 CI 布局文件里该字段对已显示组件也常为 false，
    /// 并非「是否显示」的可靠标志；用户要求「加了组件就显示」，故只按是否提取到内容判定。
    /// </remarks>
    private void CollectComponents(
        ComponentSettings c,
        ref string? title, ref string? remaining, List<string> texts)
    {
        if (c.Settings is not null)
        {
            var (t, r, txt) = CiComponentsReader.Extract(c.Settings, NowVirtual);
            if (!string.IsNullOrEmpty(t)) title ??= t;
            if (!string.IsNullOrEmpty(r)) remaining ??= r;
            if (!string.IsNullOrEmpty(txt)) texts.Add(txt!);
        }
        if (c.Children is not null)
            foreach (var child in c.Children)
                CollectComponents(child, ref title, ref remaining, texts);
    }

    /// <summary>从 CI Settings.json 缓存读取天气 + 预警（每 60 秒）。</summary>
    private void RefreshWeather()
    {
        try
        {
            var settingsPath = Path.Combine(CiDataDir, "Settings.json");
            if (!File.Exists(settingsPath)) return;
            var (code, temp, alerts) = CiWeatherReader.ReadLastWeather(settingsPath);

            _weatherAlerts.Clear();
            _weatherAlerts.AddRange(alerts);
            UpdateAlertItems();

            if (string.IsNullOrEmpty(code))
            {
                _reminderData.WeatherIcon = null;
                _reminderData.WeatherText = null;
            }
            else
            {
                var desc = CiWeatherReader.GetWeatherDescription(code) ?? code;
                _reminderData.WeatherText = string.IsNullOrEmpty(temp) ? desc : $"{temp}° {desc}";

                // 天气图标：按天气码查 Lucide 图标名再转字形，用 CI 自带图标字体画成矢量图标。
                // （CI 的天气图标模板按小米天气码查表、码表在 CI 内部私有初始化，插件接不到，
                //   故这里自建 weathercn 码 → Lucide 图标映射。）
                _reminderData.WeatherIcon = CiWeatherReader.WeatherIcons.TryGetValue(code, out var iconName)
                    ? IconGlyph.Of(iconName)
                    : IconGlyph.Of(LucideIconKind.Thermometer);
            }

            _weatherCode = string.IsNullOrEmpty(code) ? null : code;
            _weatherTempC = string.IsNullOrEmpty(temp) ? null : ParseTemp(temp);
            _rainHours = CiWeatherReader.ReadRainHours(settingsPath);
            RainReminderTitle = CiWeatherReader.ReadRainReminder(settingsPath) ?? "";
            NotifyReminderChanged();
        }
        catch { }
    }

    /// <summary>
    /// 合成预警列表并显示：天气预警（按「预警」开关过滤）+ 摄像头占用提醒（按「摄像头占用」开关过滤）。
    /// 内容与上次一致就不重建——重建会把正在悬停的条目整链移除、触发 PointerExited/Entered，
    /// 导致顶部详情弹幕（天气 60 秒一刷、摄像头每秒一算）重置重播一次。
    /// </summary>
    private void UpdateAlertItems()
    {
        // 摄像头占用快照由 CameraActivityService 在后台更新，这里取当前快照重算条目
        var cameraAlerts = _settings.ShowCameraReminder
            ? CameraAlertBuilder.Build(_settings.MonitoredCameras, _cameraService.Occupancy,
                                       _settings.CameraAlertFontSize)
            : new List<AlertInfo>();

        var merged = new List<AlertInfo>();
        if (_settings.ShowAlertsReminder) merged.AddRange(_weatherAlerts);
        merged.AddRange(cameraAlerts);

        var changed = merged.Count != _reminderData.Alerts.Count
            || !merged.Zip(_reminderData.Alerts).All(p =>
                p.First.Title == p.Second.Title && p.First.Level == p.Second.Level
                && p.First.Detail == p.Second.Detail && Math.Abs(p.First.FontSize - p.Second.FontSize) < 0.01);
        if (!changed) return;

        AlertItems.Clear();
        ExpandedAlertDetail = null;   // 预警内容更新，收起旧展开态（弹幕尺寸按旧文本算会错位）
        foreach (var a in merged)
            AlertItems.Add(new AlertDisplayItem
            {
                DisplayText = a.Title,
                Icon = IconGlyph.Of(a.IconName),
                Level = a.Level,
                Foreground = AlertBrush(a.Level, Chrome.IsLight),
                Detail = a.Detail,
                FontSize = a.FontSize,
            });

        _reminderData.Alerts.Clear();
        _reminderData.Alerts.AddRange(merged);
        NotifyReminderChanged();   // 条目增减会影响面板/第一行的可见性
    }

    /// <summary>预警等级 → 显示颜色（蓝/黄/橙/红，浅底上取压暗版）。等级缺失时用主题正文色。</summary>
    private IBrush AlertBrush(string? level, bool light)
    {
        var hex = SemanticColors.AlertHex(level, light);
        return hex is null ? Chrome.TextPrimary : new SolidColorBrush(Color.Parse(hex));
    }

    /// <summary>触发所有提醒相关属性变更通知（数据或开关变化后调用）。</summary>
    private void NotifyReminderChanged()
    {
        OnPropertyChanged(nameof(WeatherIcon));
        OnPropertyChanged(nameof(WeatherText));
        OnPropertyChanged(nameof(WeatherBrush));
        OnPropertyChanged(nameof(RainReminderTitle));
        OnPropertyChanged(nameof(ShowRainReminder));
        OnPropertyChanged(nameof(ReminderText));
        OnPropertyChanged(nameof(CountdownText));
        OnPropertyChanged(nameof(ShowWeatherRow));
        OnPropertyChanged(nameof(ShowAlertsRow));
        OnPropertyChanged(nameof(ShowCountdownRow));
        OnPropertyChanged(nameof(ShowTextRow));
        OnPropertyChanged(nameof(ShowReminderPanel));
        OnPropertyChanged(nameof(RainSubtitle));
        OnPropertyChanged(nameof(HasRainSubtitle));
        UpdateWeatherSubtitle();   // 天气码/温度变化后重算气温副标题
        UpdateCountdownSubtitle(); // 倒计时剩余天数变化后重算冲刺副标题
        UpdateReminderMerge();   // 内容/开关变化后重算第一行合并
        OnPropertyChanged(nameof(ShowReminderPanel));
    }

    /// <summary>CI 数据目录（data\，位于插件配置目录的上一级）。</summary>
    private string CiDataDir => Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..", "..", ".."));
}

/// <summary>提醒面板中单条预警的显示项（等级着色 + 详情全文，鼠标悬停即在顶部弹幕带显示）。</summary>
public class AlertDisplayItem : INotifyPropertyChanged
{
    public required string DisplayText { get; init; }

    /// <summary>图标矢量字形（警告三角，XAML 用 Lucide 字体渲染）。</summary>
    public string? Icon { get; init; }

    /// <summary>预警等级（蓝/黄/橙/红），换主题时据此重算颜色。</summary>
    public string? Level { get; init; }

    private IBrush _foreground = Brushes.White;

    /// <summary>等级色（可写：切主题时就地换压暗版，不重建列表）。</summary>
    public required IBrush Foreground
    {
        get => _foreground;
        set
        {
            _foreground = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Foreground)));
        }
    }

    /// <summary>预警详情全文（alerts[].detail），鼠标悬停该条时显示。</summary>
    public string? Detail { get; init; }

    /// <summary>面板上这条的字号（天气预警用默认值，摄像头占用那条由设置调）。</summary>
    public double FontSize { get; init; } = AlertInfo.DefaultFontSize;

    /// <summary>图标字号：比正文小一点，与正文一起缩放。</summary>
    public double IconFontSize => Math.Max(8, FontSize - 1);

    public event PropertyChangedEventHandler? PropertyChanged;
}
