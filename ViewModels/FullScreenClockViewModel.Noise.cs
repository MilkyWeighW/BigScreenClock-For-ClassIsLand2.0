// =============================================================================
// FullScreenClockViewModel（partial）— 噪音统计、音量条、计数片段与「正在记录」
// 职责：分贝服务的属性/事件转发、计数冷却与保护期判定、音量条五档动画与文字高亮、
// 左下角计数片段（课程名/一般/吵闹/保护提示）与「正在记录」提示的增量刷新。
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
    /// <summary>
    /// 分贝属性变更：仅转发显示属性给全屏界面绑定。计数由持续事件驱动（NoiseEventRaised）。
    /// </summary>
    private void OnDecibelPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(DecibelMeterService.NoiseLevelProgress):
                // 采样得到新的目标进度 → 交给动画平滑逼近（文字/高亮由动画帧在跨档位时刷新）
                _targetProgress = _decibelService.NoiseLevelProgress;
                StartProgressAnimation();
                break;
            case nameof(DecibelMeterService.IsMonitoring):
            case nameof(DecibelMeterService.NoiseLevelText):
                // 监测开关 / 错误提示文字变化 → 直接刷新右侧文字（如「未检测到麦克风」「等待检测…」）
                OnPropertyChanged(nameof(NoiseLevelText));
                OnPropertyChanged(nameof(NoiseLevelFillBrush));
                OnPropertyChanged(nameof(NoiseLevelTextBrush));
                break;
            case nameof(DecibelMeterService.CurrentSegmentLevel):
                UpdateRecording();
                break;
        }
    }

    /// <summary>
    /// 持续事件（一般/吵闹）→ 计数。事件在音频线程引发，这里调度到 UI 线程。
    /// 事件等级已由探测器按段内一般/吵闹占比判定，计数层只应用 冷却/保护 规则。
    /// </summary>
    private void OnNoiseEventRaised(object? sender, NoiseEventRaisedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var decision = _counter.OnEvent(e.Level, IsInProtection());
            if (decision != CountDecision.None)
                UpdateNoisyDisplay();
        });
    }

    /// <summary>
    /// 是否处于「上课初期保护」窗口。起点优先用课表里当前课的实际开始时间：
    /// 大屏可能在上课中途才打开，CI 的时段事件没重发过，_classStartTime 会停在 default 或上一节课，
    /// 拿它算就会把保护窗口从打开大屏那一刻重新开始（用户反馈的 bug）。
    /// 课表查不到时退回 CI 事件给过的开始时间；两者都没有就不保护（没有依据就别凭空压着不计数）。
    /// </summary>
    private bool IsInProtection()
    {
        if (!_settings.SkipFirst3Min) return false;
        // 起点优先级：CI 课程服务给的当前时间点 → 课表里当前的课 → CI 事件给过的开始时间。
        // 都不要「打开大屏的时刻」这种兜底——那正是旧版保护期从打开时刻重算的 bug。
        var start = LookupCiClassStart()
                    ?? LookupCurrentClassStart()
                    ?? (_classStartTime == default ? (DateTime?)null : _classStartTime);
        if (start is null) return false;
        return (NowVirtual - start.Value).TotalSeconds < _settings.SkipFirstMinutes * 60;
    }

    /// <summary>音量条当前显示的进度（0-100）：由动画向采样得到的目标进度平滑逼近，绑到进度条 Value。</summary>
    public double AnimatedProgress
    {
        get => _animatedProgress;
        private set { _animatedProgress = value; OnPropertyChanged(); }
    }
    private double _animatedProgress;
    private double _targetProgress;
    private DispatcherTimer? _progressAnimTimer;

    /// <summary>统一档位索引（0-4）：轨道位置、五档高亮、右侧状态文字全部由 AnimatedProgress 反推，条和字永远同源。</summary>
    private int CurrentSlot => LevelSlotCalculator.SlotFromProgress(AnimatedProgress);

    /// <summary>右侧状态文字：正常监测时按当前档位查表（与条同源）；无麦克风/出错时显示 Service 的提示文字。</summary>
    public string NoiseLevelText => _decibelService.IsMonitoring ? SlotNames[CurrentSlot] : _decibelService.NoiseLevelText;

    /// <summary>右侧状态文字的颜色：按当前档位取「已达到」色（随主题换），与下方五档文字同源。</summary>
    public IBrush NoiseLevelTextBrush => (Chrome.IsLight ? SlotReachedBrushesLight : SlotReachedBrushesDark)[CurrentSlot];

    /// <summary>音量条五档文字集合（安静/良好/一般/吵闹/嘈杂）。</summary>
    public ObservableCollection<NoiseLevelSlotItem> NoiseLevelSlots => _noiseLevelSlots;

    /// <summary>音量条轨道填充色：随当前档位可变（绿→黄→红），取缓存的画刷实例。</summary>
    public IBrush NoiseLevelFillBrush => SlotBrushes[CurrentSlot];

    /// <summary>音量条轨道底色 / 课程进度带 track 底色（随主题翻转，与进度带共用同一画刷实例）。</summary>
    public IBrush NoiseLevelTrackBrush => Chrome.VolumeTrack;

    /// <summary>档位变化时：刷新当前档位文字高亮 + 轨道填充色 + 右侧状态文字（全部同一档位源）。</summary>
    private void UpdateNoiseLevelSlots()
    {
        var slot = CurrentSlot;
        for (var i = 0; i < _noiseLevelSlots.Count; i++)
            _noiseLevelSlots[i].IsCurrent = i == slot;
        OnPropertyChanged(nameof(NoiseLevelText));
        OnPropertyChanged(nameof(NoiseLevelFillBrush));
        OnPropertyChanged(nameof(NoiseLevelTextBrush));
    }

    /// <summary>收到新的采样目标进度：启动（或保持）动画，让条平滑逼近目标。</summary>
    private void StartProgressAnimation()
    {
        if (_progressAnimTimer == null)
        {
            _progressAnimTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
            _progressAnimTimer.Tick += OnProgressAnimTick;
        }
        if (!_progressAnimTimer.IsEnabled)
            _progressAnimTimer.Start();
    }

    /// <summary>
    /// 动画帧：把 AnimatedProgress 向目标指数逼近（无过冲），条在两个采样点之间平滑滑动；
    /// 只在跨过档位边界时刷新文字/高亮/填充色，其余帧只动进度条。
    /// </summary>
    private void OnProgressAnimTick(object? sender, EventArgs e)
    {
        var prevSlot = CurrentSlot;
        var delta = _targetProgress - AnimatedProgress;
        if (Math.Abs(delta) < 0.2)
        {
            AnimatedProgress = _targetProgress;
            _progressAnimTimer!.Stop();
        }
        else
        {
            AnimatedProgress += delta * 0.5;   // 指数平滑，视觉上匀速滑动
        }
        if (CurrentSlot != prevSlot)
            UpdateNoiseLevelSlots();
    }

    /// <summary>
    /// 计数区域的分段显示：课程名/一般（黄）/吵闹（红）/保护提示。
    /// </summary>
    public ObservableCollection<NoisyDisplaySegment> NoisyDisplaySegments { get; } = new();

    /// <summary>
    /// 底部记录规则是否显示（鼠标悬停计数区域时）。
    /// </summary>
    public bool ShowCountRules
    {
        get => _showCountRules;
        set { _showCountRules = value; OnPropertyChanged(); }
    }

    // ===== 「正在记录」提示（段起算后实时显示，一般黄 / 吵闹红） =====

    private bool _isRecordingVisible;
    private string _recordingText = "";
    private IBrush _recordingForeground = CountNormalBrush;

    /// <summary>当前主题下的「正在记录」提示色（换主题时由 <see cref="RefreshSemanticColors"/> 重算）。</summary>
    private void RefreshRecordingColor()
    {
        if (!IsRecordingVisible) return;
        RecordingForeground = RecordingText.Contains("吵闹") ? CountNoisy : CountNormal;
    }

    /// <summary>是否显示「正在记录」提示（有段且已起算、上课中）。</summary>
    public bool IsRecordingVisible
    {
        get => _isRecordingVisible;
        set
        {
            if (_isRecordingVisible == value) return;
            _isRecordingVisible = value;
            OnPropertyChanged();
            NotifyRecordingPlacement();
            // 它一亮，「暂未记录到吵闹」就得让位（见 AddIdleSegment），左边计数片段要跟着重算
            UpdateNoisyDisplay();
        }
    }

    /// <summary>
    /// 「正在记录」提示的位置：平时在音量条标题行的正中间（左边「教室音量」、右边当前档位，
    /// 中间正好空着）；音量条被关掉时回退到左下角计数行。两处共用一个 <see cref="RecordingText"/>，
    /// 可见性互补 —— Avalonia 的绑定做不了 AND 与取反，所以在 VM 里算好。
    ///
    /// 为什么占「标题行」而不是另起一行：它一出现/消失，那一整行（连玻璃）的尺寸都会变，
    /// 得重烘一块玻璃，看着就是「没有过渡、直接闪一下」。塞进本来就空着的中间，
    /// 出现/消失一个像素都不挪，一次都不用重烘。
    ///
    /// 为什么不留在计数行：它原本和计数片段共处一个「只缩不放大」的 Viewbox，
    /// 计数一变长就把整行连它一起缩小（字号忽大忽小）。挪出来以后两边互不相干。
    /// </summary>
    public bool RecordingInMeterTitle => IsRecordingVisible && ShowDecibelMeter;

    /// <summary>见 <see cref="RecordingInMeterTitle"/>：音量条关掉时「正在记录」回到计数行。</summary>
    public bool RecordingInCounter => IsRecordingVisible && !ShowDecibelMeter;

    private void NotifyRecordingPlacement()
    {
        OnPropertyChanged(nameof(RecordingInMeterTitle));
        OnPropertyChanged(nameof(RecordingInCounter));
    }

    /// <summary>「正在记录：一般」/「正在记录：吵闹」。</summary>
    public string RecordingText
    {
        get => _recordingText;
        set { _recordingText = value; OnPropertyChanged(); }
    }

    /// <summary>提示颜色：一般=黄，吵闹=红。</summary>
    public IBrush RecordingForeground
    {
        get => _recordingForeground;
        set { _recordingForeground = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 根据检测器的实时段归属刷新「正在记录」提示。
    /// 仅在计数可见、上课时段、且不在上课初期保护期内显示；段起算前/段结束后隐藏。
    /// （保护期内本来就不计数，再挂个「正在记录」会误导同学）
    /// </summary>
    private void UpdateRecording()
    {
        var seg = _decibelService.CurrentSegmentLevel;
        bool show = seg.HasValue && _settings.ShowNoisyCounter
            && !_isInBreak && !string.IsNullOrEmpty(_currentClassName)
            && !IsInProtection();
        IsRecordingVisible = show;
        if (!show) return;
        var noisy = seg == NoiseLevel.Noisy;
        RecordingText = noisy ? "正在记录：吵闹" : "正在记录：一般";
        RecordingForeground = noisy ? CountNoisy : CountNormal;
    }

    /// <summary>左下角计数与「正在记录」提示的字号（设置项）。</summary>
    public int CountFontSize
    {
        get => _countFontSize;
        set { _countFontSize = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 记录规则（鼠标悬停计数区域时以提示显示）。
    /// </summary>
    public string CountRulesText
    {
        get
        {
            var parts = new List<string>
            {
                $"一段噪音累计满 {_settings.NoisySustainSeconds:0.#} 秒记一次",
                $"一段内回落不超过 {_settings.FallWindowSeconds:0.#} 秒并成一次",
                "一段噪音按一般/吵闹的时长占比归属：吵闹占一半及以上记「吵闹」，否则记「一般」",
            };
            if (_settings.SkipFirst3Min) parts.Add($"上课开始后 {_settings.SkipFirstMinutes} 分钟内不记录");
            return string.Join("，", parts);
        }
    }

    /// <summary>
    /// 构建计数区域分段（课程名/一般黄/吵闹红/保护提示）。
    /// 只在上课期间显示；课间/非上课时段内部仍可记录，但显示隐藏。
    /// </summary>
    private List<NoisyDisplaySegment> BuildNoisySegments()
    {
        var list = new List<NoisyDisplaySegment>();
        if (!_settings.ShowNoisyCounter) return list;

        // 课间或非上课（无当前课程）时段：隐藏计数显示
        if (_isInBreak) return list;
        if (string.IsNullOrEmpty(_currentClassName)) return list;

        // 课程名不是「数据」而是标签，用主题正文色（白/黑）。
        // 着色只留给一般/吵闹的计数，否则一整行都是荧光黄，浅底上整行读不出来。
        // 课程信息正显示着时就不再重复写一遍课名：正中那行已经写着「当前课程为 X」，
        // 左边再来一遍既啰嗦，又把大半列宽占掉（课程信息一长，计数就被列宽裁掉）。
        if (!ShowCourseInfo)
            list.Add(new NoisyDisplaySegment { Text = _currentClassName, Foreground = Chrome.TextPrimary });
        AddNoisyCountSegments(list);
        return list;
    }

    /// <summary>追加一般/吵闹计数段（计数为 0 时按设置显示保护提示或「暂未记录」）。</summary>
    private void AddNoisyCountSegments(List<NoisyDisplaySegment> list)
    {
        if (_counter.NormalCount > 0 || _counter.NoisyCount > 0)
        {
            if (_counter.NormalCount > 0)
                list.Add(new NoisyDisplaySegment { Text = $"  一般 {_counter.NormalCount} 次", Foreground = CountNormal });
            if (_counter.NoisyCount > 0)
                list.Add(new NoisyDisplaySegment { Text = $"  吵闹 {_counter.NoisyCount} 次", Foreground = CountNoisy });
        }
        else if (_settings.SkipFirst3Min)
        {
            // 保护期起点与 IsInProtection 同源：按当前课的实际开始时间算。
            // 大屏在上课中途打开时不能从打开时刻重新计时；课表查不到（不在上课）就退回 CI 事件值。
            var start = LookupCurrentClassStart()
                        ?? (_classStartTime == default ? (DateTime?)null : _classStartTime);
            var remaining = start is null
                ? 0
                : _settings.SkipFirstMinutes * 60 - (int)(NowVirtual - start.Value).TotalSeconds;
            if (remaining > 0)
                list.Add(new NoisyDisplaySegment
                {
                    Text = $"上课初期保护中（{remaining / 60}:{remaining % 60:D2} 后开始记录）",
                    Icon = IconGlyph.Of(LucideIconKind.Hourglass),
                    Foreground = Chrome.TextPrimary,
                });
            else
                AddIdleSegment(list);
        }
        else
        {
            AddIdleSegment(list);
        }
    }

    /// <summary>
    /// 「暂未记录到吵闹」那一段。右边的「正在记录」已经亮着时不再显示：
    /// 正记着还说「没记录到」自相矛盾，两句叠一行又把左边的课程名挤到列外被裁掉。
    /// </summary>
    private void AddIdleSegment(List<NoisyDisplaySegment> list)
    {
        if (IsRecordingVisible) return;
        list.Add(new NoisyDisplaySegment { Text = "暂未记录到吵闹", Icon = CheckIcon, Foreground = Chrome.TextPrimary });
    }

    private void UpdateNoisyDisplay()
    {
        var segments = BuildNoisySegments();
        var signature = string.Join("|", segments.Select(s =>
            s.Text + ":" + s.Icon + ":" + (IsCountNoisy(s.Foreground) ? "R"
                : IsCountNormal(s.Foreground) ? "Y" : "P")));
        if (signature == _noisyDisplaySignature) return;

        _noisyDisplaySignature = signature;

        // 增量同步：复用现有片段只改文字/颜色/图标，不清空重建。
        // 清空重建会把鼠标下的 TextBlock 整链移除重建、触发 PointerExited/Entered 循环，
        // 导致上课初期保护期间每秒刷新时记录规则 Popup 一秒闪一次。
        for (var i = 0; i < NoisyDisplaySegments.Count && i < segments.Count; i++)
        {
            NoisyDisplaySegments[i].Text = segments[i].Text;
            NoisyDisplaySegments[i].Icon = segments[i].Icon;
            NoisyDisplaySegments[i].Foreground = segments[i].Foreground;
        }
        while (NoisyDisplaySegments.Count < segments.Count)
            NoisyDisplaySegments.Add(segments[NoisyDisplaySegments.Count]);
        while (NoisyDisplaySegments.Count > segments.Count)
            NoisyDisplaySegments.RemoveAt(NoisyDisplaySegments.Count - 1);
    }
}

/// <summary>
/// 计数区域的单个显示片段（带颜色，用于一般/吵闹不同色）。
/// 实现 INotifyPropertyChanged：UpdateNoisyDisplay 增量同步时只更新已存在项的文字/颜色，
/// 不清空重建列表（重建会触发鼠标下元素的 PointerExited/Entered 循环）。
/// </summary>
public class NoisyDisplaySegment : INotifyPropertyChanged
{
    private string _text = "";
    public string Text
    {
        get => _text;
        set { if (_text != value) { _text = value; OnPropertyChanged(nameof(Text)); } }
    }

    // 兜底值：实际每个片段都由 VM 按主题显式给色（见 BuildNoisySegments）。
    // 这里以前是写死的荧光黄 #ffff44，浅底主题下漏改一处就是一整行看不见。
    private IBrush _foreground = Brushes.White;

    public IBrush Foreground
    {
        get => _foreground;
        set { if (!ReferenceEquals(_foreground, value)) { _foreground = value; OnPropertyChanged(nameof(Foreground)); } }
    }

    /// <summary>图标矢量字形（空 = 该片段不带图标，如「一般 2 次」纯文字）。</summary>
    private string _icon = "";
    public string Icon
    {
        get => _icon;
        set
        {
            if (_icon == value) return;
            _icon = value;
            OnPropertyChanged(nameof(Icon));
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => !string.IsNullOrEmpty(_icon);

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
