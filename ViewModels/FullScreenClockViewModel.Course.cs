// =============================================================================
// FullScreenClockViewModel（partial）— 课表加载、课程定位与打铃时间
// 职责：从 CI 课表文件加载当天时段（LoadTodaySchedule）、
// 当前/下一节课程信息与进度、打铃时间换算，以及 CI 上课状态事件的处理（含噪音计数器的换课重置）。
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
    public bool ShowCourseInfo
    {
        get => _showCourseInfo;
        set { _showCourseInfo = value; OnPropertyChanged(); }
    }

    public string CourseInfoText
    {
        get => _courseInfoText;
        set { _courseInfoText = value; OnPropertyChanged(); }
    }

    private double _courseProgress;

    /// <summary>当前时段进行进度（0~100）。上课=本课进度，课间=当前休息进度。</summary>
    public double CourseProgress
    {
        get => _courseProgress;
        set { _courseProgress = value; OnPropertyChanged(); }
    }

    private bool _showCourseProgress;

    /// <summary>是否显示进度条（当前存在时段槽时显示，放学/无课隐藏）。</summary>
    public bool ShowCourseProgress
    {
        get => _showCourseProgress;
        set { _showCourseProgress = value; OnPropertyChanged(); }
    }

    /// <summary>底部居中的课程信息字号（设置项，改完进大屏时钟时生效）。</summary>
    public int CourseInfoFontSize
    {
        get => _courseInfoFontSize;
        set { _courseInfoFontSize = value; OnPropertyChanged(); }
    }

    private void LoadTodaySchedule()
    {
        // 日期在这里一次记下：进入即置，失败也置（课表读失败时免得每秒重试风暴）
        _scheduleDate = DateTime.Today;
        _todaySlots.Clear();
        try
        {
            var dataDir = Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..", "..", ".."));
            var settingsPath = Path.Combine(dataDir, "Settings.json");
            var profileFileName = "7.json";
            if (File.Exists(settingsPath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(settingsPath));
                if (doc.RootElement.TryGetProperty("SelectedProfile", out var sp))
                    profileFileName = sp.GetString() ?? "7.json";
                if (doc.RootElement.TryGetProperty("TimeOffsetSeconds", out var offset))
                    _timeOffsetSeconds = offset.GetInt32();
            }
            var profilePath = Path.Combine(dataDir, "Profiles", profileFileName);
            if (!File.Exists(profilePath)) return;

            using var profileDoc = JsonDocument.Parse(File.ReadAllText(profilePath));
            var root = profileDoc.RootElement;
            if (!root.TryGetProperty("TimeLayouts", out var layouts)) return;
            if (!root.TryGetProperty("ClassPlans", out var classPlans)) return;
            if (!root.TryGetProperty("Subjects", out var subjects)) return;

            var now = DateTime.Now;
            int todayDayOfWeek = (int)now.DayOfWeek;

            // 找今天的课表
            JsonElement? todayPlan = null;
            string? tlId = null;
            foreach (var cp in classPlans.EnumerateObject())
            {
                var v = cp.Value;
                if (v.TryGetProperty("IsOverlay", out var io) && io.GetBoolean()) continue;
                if (!v.TryGetProperty("TimeRule", out var tr)) continue;
                if (!tr.TryGetProperty("WeekDay", out var wd) || wd.GetInt32() != todayDayOfWeek) continue;
                if (!v.TryGetProperty("IsEnabled", out var ie) || !ie.GetBoolean()) continue;
                todayPlan = v;
                if (v.TryGetProperty("TimeLayoutId", out var t)) tlId = t.GetString();
                break;
            }
            if (todayPlan == null || tlId == null) return;
            if (!layouts.TryGetProperty(tlId, out var timeLayout)) return;
            if (!timeLayout.TryGetProperty("Layouts", out var items)) return;

            var classes = todayPlan.Value.GetProperty("Classes");
            int ci = 0;
            foreach (var item in items.EnumerateArray())
            {
                int timeType = item.GetProperty("TimeType").GetInt32();
                var start = TimeSpan.Parse(item.GetProperty("StartTime").GetString()!);
                var end = TimeSpan.Parse(item.GetProperty("EndTime").GetString()!);
                string? subjName = null;
                string? subjId = null;
                if (timeType == 0 && ci < classes.GetArrayLength())
                {
                    var ce = classes[ci];
                    if (ce.TryGetProperty("SubjectId", out var sid))
                    {
                        var sidStr = sid.GetString();
                        subjId = sidStr;
                        if (sidStr != null && subjects.TryGetProperty(sidStr, out var subj))
                            subjName = subj.GetProperty("Name").GetString();
                    }
                    ci++;
                }
                _todaySlots.Add(new TimeSlot
                {
                    Start = start,
                    End = end,
                    IsClass = timeType == 0,
                    SubjectName = subjName,
                    SubjectId = subjId,
                });
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] 加载课表: {ex}");
        }
    }

    private void UpdateCourseInfo(DateTime now)
    {
        var time = now.TimeOfDay;
        TimeSlot? current = null;
        TimeSlot? nextClass = null;

        for (int i = 0; i < _todaySlots.Count; i++)
        {
            var s = _todaySlots[i];
            if (time >= s.Start && time < s.End)
            {
                current = s;
                for (int j = i + 1; j < _todaySlots.Count; j++)
                {
                    if (_todaySlots[j].IsClass && !string.IsNullOrEmpty(_todaySlots[j].SubjectName))
                    { nextClass = _todaySlots[j]; break; }
                }
                break;
            }
        }

        if (current != null && current.IsClass && !string.IsNullOrEmpty(current.SubjectName))
        {
            // 上课中：显示当前课程（含实际打铃时间，如有偏移）
            CourseInfoText = $"当前课程为 {current.SubjectName}   {current.Start:hh\\:mm} — {current.End:hh\\:mm}{FormatBellTime(current)}";
        }
        else if (_isInBreak && nextClass != null)
        {
            // 课间：显示下节课程
            CourseInfoText = $"下节课程为 {nextClass.SubjectName}   {nextClass.Start:hh\\:mm} — {nextClass.End:hh\\:mm}{FormatBellTime(nextClass)}";
        }
        else
        {
            CourseInfoText = "";
        }

        // 进度条：上课槽或课间槽都显示「当前时间状态」的进行进度。
        // time 与课程定位是同一时刻（NowVirtual），current 是同一时段槽 → 时间显示与进度天然一致。
        if (current != null)
        {
            CourseProgress = ProgressCalculator.Calc(time, current.Start, current.End);
            ShowCourseProgress = true;
        }
        else
        {
            ShowCourseProgress = false;
            CourseProgress = 0;
        }
    }

    /// <summary>
    /// 把课表时间换算成实际打铃时间（北京真实时间）的说明文字。
    /// 实际打铃时间 = 课表时间 - TimeOffsetSeconds。无偏移或用户关掉该子项时返回空串。
    /// </summary>
    private string FormatBellTime(TimeSlot slot)
    {
        if (!_settings.ShowBellTime || _timeOffsetSeconds == 0) return "";
        var offset = TimeSpan.FromSeconds(_timeOffsetSeconds);
        var bellStart = slot.Start - offset;
        var bellEnd = slot.End - offset;
        var fmt = _timeOffsetSeconds % 60 == 0 ? @"hh\:mm" : @"hh\:mm\:ss";
        return $"（实际打铃 {bellStart.ToString(fmt)} — {bellEnd.ToString(fmt)}）";
    }

    private void SubscribeToClassEvents()
    {
        try
        {
            var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
            // 幂等订阅：先退订再订阅，避免 Show/Hide 多次叠加（P0 修复）
            lessonsService.CurrentTimeStateChanged -= OnTimeStateChanged;
            lessonsService.CurrentTimeStateChanged += OnTimeStateChanged;
            // 立即检查当前状态（用户可能在打开时钟时已经在上课）
            Dispatcher.UIThread.Post(() => OnTimeStateChanged(null, EventArgs.Empty));
        }
        catch { }
    }

    private void UnsubscribeToClassEvents()
    {
        try
        {
            var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
            lessonsService.CurrentTimeStateChanged -= OnTimeStateChanged;
        }
        catch { }
    }

    /// <summary>
    /// 从课表里查出当前课程的开始时间（用 CI 虚拟时间定位，与实际打铃对齐）。
    /// 传了科目 Id 就按 Id 匹配（同名不同绑定的课才分得开），否则按名字匹配。
    /// 如果课表没加载或找不到，返回 null，调用方降级使用虚拟时间。
    /// </summary>
    private DateTime? LookupActualClassStart(string subjectName, string? subjectId = null)
    {
        if (_todaySlots.Count == 0) return null;
        if (string.IsNullOrEmpty(subjectName) && string.IsNullOrWhiteSpace(subjectId)) return null;

        var now = NowVirtual;
        foreach (var slot in _todaySlots)
        {
            if (!slot.IsClass) continue;
            if (now.TimeOfDay < slot.Start || now.TimeOfDay >= slot.End) continue;

            var matches = !string.IsNullOrWhiteSpace(subjectId) && !string.IsNullOrWhiteSpace(slot.SubjectId)
                ? slot.SubjectId == subjectId
                : slot.SubjectName == subjectName;
            if (matches) return now.Date + slot.Start;
        }
        return null;
    }

    /// <summary>课表里当前正在上的那节课的科目 Id（没有则 null）。</summary>
    private string? LookupCurrentClassSubjectId()
    {
        if (_todaySlots.Count == 0) return null;
        var now = NowVirtual;
        foreach (var slot in _todaySlots)
        {
            if (slot.IsClass && !string.IsNullOrEmpty(slot.SubjectName)
                && now.TimeOfDay >= slot.Start && now.TimeOfDay < slot.End)
            {
                return slot.SubjectId;
            }
        }
        return null;
    }

    /// <summary>
    /// 从课表里查出「当前正在上的那节课」的实际开始时间（用 CI 虚拟时间定位，与实际打铃对齐）。
    /// 大屏可能在上课中途才打开，此时 CI 的时段事件不会重发，保护期起点不能从打开那一刻重新算；
    /// 用这个把起点纠正回课表里真正的上课时刻。
    /// 课表没加载 / 当前不在上课时段时返回 null，调用方再退回 CI 事件给过的开始时间。
    /// </summary>
    private DateTime? LookupCurrentClassStart()
    {
        if (_todaySlots.Count == 0) return null;
        var now = NowVirtual;
        foreach (var slot in _todaySlots)
        {
            if (slot.IsClass && !string.IsNullOrEmpty(slot.SubjectName)
                && now.TimeOfDay >= slot.Start && now.TimeOfDay < slot.End)
            {
                return now.Date + slot.Start;
            }
        }
        return null;
    }

    /// <summary>
    /// 直接从 ClassIsland 的课程服务取「当前这节课」的开始时间。
    /// 不依赖插件自己解析的 Profile JSON：大屏在上课中途才打开、或者课表文件结构变化导致
    /// 自解析失败时，这条路径仍然拿得到准确的开始时刻，保护期就不会从打开大屏那一刻重算。
    /// </summary>
    private DateTime? LookupCiClassStart()
    {
        try
        {
            var lessons = _serviceProvider.GetService<ILessonsService>();
            if (lessons is null) return null;
            if (lessons.CurrentState != ClassIsland.Shared.Enums.TimeState.OnClass) return null;

            var item = lessons.CurrentTimeLayoutItem;
            if (item is null || item.TimeType != 0 || item.StartTime <= TimeSpan.Zero) return null;

            // 课表里的时间点用「虚拟时间轴」表示，偏移由 CI 统一处理；
            // 这里要和 NowVirtual 对比，所以两侧都不动偏移，偏移量自然抵消。
            return DateTime.Today + item.StartTime;
        }
        catch
        {
            return null;
        }
    }

    private void OnTimeStateChanged(object? sender, EventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            try
            {
                var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
                var state = lessonsService.CurrentState;
                var subjectName = lessonsService.CurrentSubject?.Name ?? "";

                if (state == ClassIsland.Shared.Enums.TimeState.OnClass)
                {
                    _currentClassName = subjectName;
                    _counter.SetBreak(false);
                    _counter.StartNewClass();
                    // 起点优先级：课表里按科目匹配 → 课表里当前的课 → CI 当前时间点。
                    // 全都拿不到就留 default（保护期按「没有依据就不保护」处理），绝不退回 NowVirtual，
                    // 否则等于从「打开大屏」那一刻开始算，正是用户反馈的 bug。
                    var subjectId = LookupCurrentClassSubjectId();
                    _classStartTime = LookupActualClassStart(subjectName, subjectId)
                                      ?? LookupCurrentClassStart()
                                      ?? LookupCiClassStart()
                                      ?? default;
                    _isInBreak = false;
                    UpdateNoisyDisplay();
                    UpdateRecording();
                }
                else if (state == ClassIsland.Shared.Enums.TimeState.Breaking)
                {
                    _counter.SetBreak(true);
                    _isInBreak = true;
                    UpdateNoisyDisplay();
                    UpdateRecording();
                }
            }
            catch { }
        });
    }
}

internal class TimeSlot
{
    public TimeSpan Start { get; set; }
    public TimeSpan End { get; set; }
    public bool IsClass { get; set; }
    public string? SubjectName { get; set; }
    /// <summary>科目 Id（CI 课表 Subjects 字典的键），用于区分同名不同绑定的课。</summary>
    public string? SubjectId { get; set; }
}
