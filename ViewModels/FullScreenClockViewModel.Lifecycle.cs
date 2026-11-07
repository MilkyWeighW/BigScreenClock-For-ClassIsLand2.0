// =============================================================================
// FullScreenClockViewModel（partial）— 窗口生命周期、定时器与每秒刷新循环
// 职责：大屏窗口的显示/隐藏/最小化与最小化暂停恢复、CI 主界面显隐、
// 防止屏幕睡眠（SetThreadExecutionState）、1 秒刷新定时器与 RefreshAll 刷新循环。
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
using EveningSelfStudyClock.Helpers;
using EveningSelfStudyClock.Models;
using EveningSelfStudyClock.NoiseDetection;
using EveningSelfStudyClock.Services;
using EveningSelfStudyClock.Views;
using Microsoft.Extensions.DependencyInjection;

namespace EveningSelfStudyClock.ViewModels;

public partial class FullScreenClockViewModel : INotifyPropertyChanged
{
    public void Show()
    {
        Dispatcher.UIThread.Post(() =>
        {
            // 若上一次退出还在淡出（窗口还没真正 Hide），先取消，避免刚叫回来又被藏掉
            _window?.CancelHideFade();

            // 必须在创建窗口前同步，否则绑定读到的是旧值
            ShowDecibelMeter = _settings.ShowDecibelMeter;
            ShowCourseInfo = _settings.ShowCourseInfo;
            ClockFontSize = _settings.ClockFontSize;
            CourseInfoFontSize = _settings.CourseInfoFontSize;
            CountFontSize = _settings.CountFontSize;
            // 主题：跟随档下 CI 主题可能在别处被改过 → 先按当前生效主题写好颜色，再刷新外观绑定
            ApplyThemePalette();
            RefreshClockFont();
            // 外观颜色属性是直接读 _settings 的 getter，窗口从 Hide 退出后复用不自动刷新，
            // 主动触发通知让绑定重新求值（修复：改完颜色后要重启 CI 才生效的问题）
            RefreshAppearanceBindings();
            RefreshRemindersNow();
            UpdateEarthquakeAlert();   // 正赶上一场地震时，开屏就有（否则等下个整秒的刷新）

            if (_window == null)
            {
                _window = new FullScreenClockWindow { DataContext = this };
                _window.Closed += (_, _) =>
                {
                    IsWindowVisible = false;
                    CancelMinimizePause();
                    _pausedForMinimize = false;
                    StopAntiScreensaver();   // 窗口是用户直接关的（不走 Hide）时，也得把屏幕睡眠限制撤掉
                    StopUpdateTimer();
                    _decibelService.StopMonitoring();
                    _cameraService.StopMonitoring();
                    UnsubscribeToClassEvents();
                    SetMainWindowVisible(true);
                    _window = null;
                };
                // 最小化后从任务栏点回：恢复即触发 Activated（Avalonia 11.3 无 WindowStateChanged 事件），
                // 若 OS 把全屏窗口恢复到 Normal 就在此兜底拉回全屏（守卫见 EnsureFullScreen，Minimized 态不打扰）。
                _window.Activated += (_, _) => EnsureFullScreen();
                // 跟随档：CI 主题在明亮/黑暗间切换时，大屏正开着也即时换配色
                _window.ActualThemeVariantChanged += (_, _) => ApplyThemePalette();
            }

            if (_window.IsVisible)
            {
                // 已最小化时再触发 Show（设置页按钮/托盘菜单）→ 先拉回全屏再刷新
                if (_window.WindowState == WindowState.Minimized)
                {
                    // 先拍一张「大屏还没出现」的干净桌面，供开屏过渡垫底用（不预压暗，压暗由背景层做动画）
                    var restoreShot = ScreenCaptureService.CapturePrimaryScreen(0.0);
                    // 窗口出现前先把内容层收起来，避免第一帧把整个界面「闪」出来
                    _window.PrepareForIntro();
                    _window.WindowState = WindowState.Normal;
                    _window.Show();
                    _window.WindowState = WindowState.FullScreen;
                    // 从任务栏收回也算一次「开屏」：重演一遍过渡，而不是直接闪出来
                    _window.BeginBackgroundIntro(restoreShot);
                    _window.ReassertFullScreen();
                    DispatcherTimer.RunOnce(() => _window?.ReassertFullScreen(), TimeSpan.FromMilliseconds(60));
                }
                // CI 主界面（顶部课表栏）保持显示：它在 Z 序上被置顶守卫压在大屏上面，
                // 不再像旧版那样为了「干净」把它藏掉——用户要求课表栏始终可见。
                ResumeFromMinimizePause();   // 最小化超过设定时间被暂停的话，这里恢复运行
                RefreshAll();
                return;
            }

            // 重置新一轮记录
            _counter.StartNewClass();
            _counter.SetBreak(false);
            _currentClassName = "";
            _isInBreak = false;
            _noisyDisplaySignature = "";
            CourseInfoText = "";
            LoadTodaySchedule();
            // 大屏可能是上课中途才打开的：把保护期起点纠正到这节课真正的开始时刻。
            // CI 的时段事件只在状态切换时发，中途打开收不到，否则保护窗口会从「打开大屏」重新计时。
            _classStartTime = LookupCiClassStart() ?? LookupCurrentClassStart() ?? _classStartTime;
            SubscribeToClassEvents();

            // 重新订阅分贝事件（先退订再订阅，保证不累积）
            _decibelService.PropertyChanged -= OnDecibelPropertyChanged;
            _decibelService.PropertyChanged += OnDecibelPropertyChanged;

            StartUpdateTimer();
            StartAntiScreensaver();
            _decibelService.StartMonitoring();
            // 摄像头占用检测：跟着大屏时钟的显示周期开关（用不上时不占系统资源）
            if (_settings.ShowCameraReminder) _cameraService.StartMonitoring();
            RefreshAll();

            // 先拍干净桌面再显示窗口：开屏过渡的第一拍要看到「原来的画面」，
            // 不能在窗口出现之后再截（那样会把大屏自己截进去）。
            // 压暗不在这里乘：交给 BackgroundLayer 的 IntroVeil 淡入，压暗才有动画。
            var introShot = ScreenCaptureService.CapturePrimaryScreen(0.0);
            // 窗口出现前先把内容层收起来：否则第一帧会把完整界面先渲染出来（用户反馈的「闪一下」）
            _window.PrepareForIntro();
            _window.WindowState = WindowState.FullScreen;
            _window.Show();
            // 复用的窗口 Hide→Show 时状态有概率落回 Normal（窗口化），Show 之后再落一次全屏；
            // 再排一次给平台把状态真正应用完（Windows 上偶尔会晚一拍）。
            _window.WindowState = WindowState.FullScreen;
            _window.BeginBackgroundIntro(introShot);
            // 复用窗口 Show 之后，平台有时晚一拍才把它落回普通窗口：立刻补一次，再各补一拍。
            _window.ReassertFullScreen();
            DispatcherTimer.RunOnce(() => _window?.ReassertFullScreen(), TimeSpan.FromMilliseconds(60));
            DispatcherTimer.RunOnce(() => _window?.ReassertFullScreen(), TimeSpan.FromMilliseconds(320));
            IsWindowVisible = true;
            // 课表栏不藏：置顶守卫会在窗口 Opened/Activated 与每 1.5s 把它排到大屏上面。
        });
    }

    /// <summary>
    /// 最小化到任务栏（收进任务栏可点回）。收起本身不停运行；只有按设置「最小化超过 N 分钟」
    /// 才暂停（见 <see cref="ScheduleMinimizePause"/>），点回任务栏图标即恢复全屏与运行。
    /// </summary>
    public void Minimize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_window == null || !IsWindowVisible) return;
            _window.WindowState = WindowState.Minimized;
            ShowCiMainWindow();
            ScheduleMinimizePause();
        });
    }

    // ===== 最小化暂停 =====

    private System.Timers.Timer? _minimizePauseTimer;
    private bool _pausedForMinimize;

    /// <summary>按设置排一个「最小化够久了就暂停」的闹钟。没开这项设置就直接不排。</summary>
    private void ScheduleMinimizePause()
    {
        CancelMinimizePause();
        if (!_settings.PauseWhenMinimized) return;

        var minutes = Math.Clamp(_settings.MinimizePauseMinutes, 1, 30);
        var timer = new System.Timers.Timer(minutes * 60_000.0) { AutoReset = false };
        timer.Elapsed += (_, _) =>
        {
            timer.Dispose();
            Dispatcher.UIThread.Post(() =>
            {
                // 中途点回任务栏会把这支闹钟取消掉（CancelMinimizePause），落地前再确认一次
                if (!ReferenceEquals(_minimizePauseTimer, timer)) return;
                _minimizePauseTimer = null;
                if (_window is null || !IsWindowVisible) return;
                if (_window.WindowState != WindowState.Minimized) return;
                PauseForMinimize();
            });
        };
        _minimizePauseTimer = timer;
        timer.Start();
    }

    private void CancelMinimizePause()
    {
        _minimizePauseTimer?.Dispose();
        _minimizePauseTimer = null;
    }

    /// <summary>
    /// 停掉所有「跟着大屏跑」的后台工作，**不关窗口、不动任务栏入口** ——
    /// 与 <see cref="Hide"/>（退出大屏）的区别就在这里：人暂时不在，先歇着，点回就继续。
    /// </summary>
    private void PauseForMinimize()
    {
        if (_pausedForMinimize) return;
        _pausedForMinimize = true;

        StopUpdateTimer();
        StopAntiScreensaver();
        _decibelService.StopMonitoring();
        _cameraService.StopMonitoring();
        UnsubscribeToClassEvents();
        _progressAnimTimer?.Stop();
    }

    /// <summary>点回任务栏（或从托盘/设置页再次打开）时，把最小化期间暂停掉的运行恢复回来。</summary>
    private void ResumeFromMinimizePause()
    {
        if (!_pausedForMinimize) return;
        _pausedForMinimize = false;
        CancelMinimizePause();

        LoadTodaySchedule();          // 期间可能已经换课
        // 最小化期间可能已经换课：保护期起点同样按课表纠正（与 Show 首次打开同一规则），
        // 否则恢复后仍用暂停前的旧起点（甚至 default）算保护窗口
        _classStartTime = LookupCiClassStart() ?? LookupCurrentClassStart() ?? _classStartTime;
        SubscribeToClassEvents();     // 顺带立刻按当前时间状态重算一次
        StartUpdateTimer();
        StartAntiScreensaver();
        _decibelService.StartMonitoring();
        if (_settings.ShowCameraReminder) _cameraService.StartMonitoring();
        RefreshAll();
    }

    /// <summary>
    /// 收起大屏时钟后把 ClassIsland 主界面拉出来。以前只最小化自己，屏幕一片空，
    /// 得自己去托盘里把主界面点回来。主界面若被隐藏/最小化就显示并还原，再提到最前。
    /// </summary>
    private static void ShowCiMainWindow()
    {
        if (AppBase.Current?.MainWindow is not { } main) return;
        main.Show();
        main.WindowState = WindowState.Normal;
        main.Activate();
    }

    /// <summary>
    /// 兜底拉回全屏：窗口可见、非全屏、非最小化时（如任务栏点回落到 Normal），
    /// Show() 后设回 FullScreen。最小化/不可见/退出流程一律短路，不打扰、不自激。
    /// </summary>
    private void EnsureFullScreen()
    {
        if (_window == null || !IsWindowVisible || !_window.IsVisible) return;
        if (_window.WindowState is WindowState.FullScreen or WindowState.Minimized) return;
        ResumeFromMinimizePause();   // 从任务栏点回：最小化暂停过的运行在这里恢复（没暂停则什么都不做）
        _window.Show();   // 幂等：窗口已可见时再次 Show 把后台/非活动窗口置前
        _window.WindowState = WindowState.FullScreen;
        // 课表栏保持在最上面，由 FullScreenClockWindow 的置顶守卫负责，这里不动它。
    }

    public void Hide()
    {
        Dispatcher.UIThread.Post(() =>
        {
            CancelMinimizePause();
            _pausedForMinimize = false;

            // 退出时整窗淡出（时长跟着设置里的动画速率），淡完再真正 Hide。
            // 服务可以先停：淡出期间画面是静态的，不需要继续采集。
            if (_window is { } window)
            {
                // 先停置顶守卫：淡出的这半秒别再抬课表栏，否则退出瞬间会闪一下
                window.PrepareForExit();
                var speed = Math.Clamp(_settings.AnimationSpeed, 0.5, 2.0);
                window.HideWithFade((int)Math.Round(480 / speed));
            }

            StopUpdateTimer();
            StopAntiScreensaver();
            _decibelService.StopMonitoring();
            _cameraService.StopMonitoring();
            UnsubscribeToClassEvents();
            IsWindowVisible = false;
            SetMainWindowVisible(true);
        });
    }

    private void StartUpdateTimer()
    {
        if (_updateTimer != null) return;
        _updateTimer = new System.Timers.Timer(1000); // 时间只显示到秒，1s 足够
        _updateTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(RefreshAll);
        _updateTimer.AutoReset = true;
        _updateTimer.Start();
    }

    private void StopUpdateTimer()
    {
        if (_updateTimer == null) return;
        _updateTimer.Stop();
        _updateTimer.Dispose();
        _updateTimer = null;
    }

    private void RefreshAll()
    {
        var now = DateTime.Now;

        // 跨午夜课表重载：_todaySlots 只由 LoadTodaySchedule 重建，
        // 原先只有预烘/打铃路径（GetNextTargetCourseStart）会触发 → 过了零点课程/下一节/打铃仍在用昨天的课表。
        // 每秒检查真实日期；_scheduleDate 由 LoadTodaySchedule 自己维护（失败也会置，避免重试风暴）。
        if (_scheduleDate != now.Date) LoadTodaySchedule();

        CurrentTime = now.ToString("HH:mm:ss");
        CurrentDate = now.ToString("yyyy年M月d日 dddd");
        UpdateDateSubtitle();   // 日期下方副标题（深夜/周五/月初），每秒刷新

        try
        {
            var lessonsService = _serviceProvider.GetRequiredService<ILessonsService>();
            CurrentCourseName = lessonsService.CurrentSubject?.Name ?? "";
        }
        catch { }

        // 课程定位用 CI 虚拟时间（与实际打铃对齐）
        UpdateCourseInfo(NowVirtual);

        // 刷新计数区域（脏标记：内容未变不重建；保护倒计时需要每秒刷新）
        UpdateNoisyDisplay();

        // 摄像头占用：占用快照由后台监视器事件驱动更新，这里每秒取一次并合成提醒条目
        // （内容没变时 UpdateAlertItems 直接返回，不会打断正在看的预警详情弹幕）
        UpdateAlertItems();

        // 地震提醒：横波倒计时每秒都在变，跟着这一秒的节拍刷新
        UpdateEarthquakeAlert();

        // 提醒面板：组件配置每 3 秒同步一次（用户改 CI 组件自动跟随），天气缓存每 60 秒读一次
        _reminderTick++;
        if (_reminderTick % 3 == 0) RefreshComponents();
        if ((DateTime.Now - _lastWeatherRead).TotalSeconds >= 60)
        {
            _lastWeatherRead = DateTime.Now;
            RefreshWeather();
        }
    }

    private void SetMainWindowVisible(bool visible)
    {
        try
        {
            var lifetime = Avalonia.Application.Current?.ApplicationLifetime;
            if (lifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                foreach (var win in desktop.Windows)
                {
                    if (win == _window) continue;
                    var typeName = win.GetType().FullName ?? "";
                    if (typeName.Contains("MainWindow"))
                    {
                        // 只在状态真的需要变时动它：对已显示的窗口再 Show() 会让课表栏
                        // 重新走一遍显示流程（退出大屏时表现为闪一下）。
                        if (visible && !win.IsVisible) win.Show();
                        else if (!visible && win.IsVisible) win.Hide();
                        break;
                    }
                }
            }
        }
        catch { }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint SetThreadExecutionState(uint esFlags);
    private const uint ES_CONTINUOUS = 0x80000000;
    private const uint ES_DISPLAY_REQUIRED = 0x00000002;
    private const uint ES_SYSTEM_REQUIRED = 0x00000001;

    /// <summary>
    /// 大屏开着期间别让屏幕睡过去。注意：这个 API **挡不住屏保**（屏保另有系统策略），
    /// 只能阻止「显示器/系统进入睡眠」—— 所以文案上别写成「防屏保」。
    ///
    /// ES_CONTINUOUS 记的是**调用线程**的执行状态：在 A 线程设、B 线程清是清不掉的。
    /// 所以刷新必须回到 UI 线程，与 Start/Stop 同一个线程，才能保证停的时候真能撤掉。
    /// </summary>
    private void StartAntiScreensaver()
    {
        _antiScreensaverTimer?.Dispose();
        _antiScreensaverTimer = new System.Timers.Timer(25 * 60 * 1000); // 每 25 分钟续一次
        _antiScreensaverTimer.Elapsed += (_, _) => Dispatcher.UIThread.Post(ApplyAntiScreensaver);
        _antiScreensaverTimer.Start();
        ApplyAntiScreensaver();   // 立即执行一次
    }

    private static void ApplyAntiScreensaver()
        => SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED);

    private void StopAntiScreensaver()
    {
        _antiScreensaverTimer?.Dispose();
        _antiScreensaverTimer = null;
        // 光停定时器是不够的：之前设的执行状态会一直挂在系统上，屏幕从此不许关。
        // 必须显式清成 ES_CONTINUOUS（= 不再阻止睡眠）。
        SetThreadExecutionState(ES_CONTINUOUS);
    }

    public void ExitFullScreen() => Hide();
}
