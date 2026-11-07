# 03 · ViewModel 与设置系统分析（大屏时钟 / LargeScreenClock）

> 范围：`ViewModels\FullScreenClockViewModel.cs`（2231 行，全项目最大）、`ViewModels\LevelSlotCalculator.cs`、`ViewModels\NoiseLevelSlotItem.cs`、`Models\PluginSettings.cs`（680 行）、`Settings\SettingsPage.axaml(.cs)`（730 / 1515 行）。
> 为说清调用关系，引用 `Models\ProgressCalculator.cs`、`Models\ClassStartSchedule.cs`、`Models\ReminderData.cs`、`Models\ClockTheme.cs`、`Helpers\ClockThemeApplier.cs`、`NoiseDetection\*.cs`、`Services\DecibelMeterService.cs`、`Services\CiWeatherReader.cs`、`Plugin.cs`、`Views\ClockSurface.axaml(.cs)`。
> 所有行号以当前工作区文件为准（Windows CRLF 文件，`read` 行号）。

---

## 0. 速览

| 事实 | 位置 |
|---|---|
| `FullScreenClockViewModel` 注册为 DI 单例，窗口与设置页预览共用同一实例 | `Plugin.cs:68`、`Settings\SettingsPage.axaml.cs:43,140` |
| 主时钟：`System.Timers.Timer(1000)`，Tick 里 `Dispatcher.UIThread.Post(RefreshAll)` | `ViewModels\FullScreenClockViewModel.cs:1670-1677` |
| 音量条动画：`DispatcherTimer` 33 ms，指数逼近 `delta*0.5`，误差 <0.2 停表 | `ViewModels\FullScreenClockViewModel.cs:512-542` |
| 防休眠：每 25 分钟续一次 `SetThreadExecutionState`；停止时显式清成 `ES_CONTINUOUS` | `ViewModels\FullScreenClockViewModel.cs:2106-2125` |
| 提醒刷新节拍：组件 3 s、天气缓存 60 s、预警/摄像头/地震每 1 s（带变更比对） | `ViewModels\FullScreenClockViewModel.cs:1714-1721` |
| 设置落盘：任意 `PropertyChanged` → 400 ms 防抖 → `ConfigureFileHelper.SaveConfig` | `Plugin.cs:22-35,54-62` |
| 预览防抖：200 ms `DispatcherTimer`，`_previewSyncing` 防止自激 | `Settings\SettingsPage.axaml.cs:187-216` |
| 拖动布局滑块：真指针事件 pin（隧道 + handledEventsToo）+ 300 ms 兜底轮询 | `Settings\SettingsPage.axaml.cs:621-717` |
| 第一行合并阈值：`Bounds.Width / 3`；提醒面板最大宽 `Bounds.Width * 2/3` | `Views\ClockSurface.axaml.cs:353-354` |
| ViewModel 内部**没有任何 `ICommand`**；命令只出现在设置页（`CourseSelectionItem.RemoveCommand`） | `Settings\SettingsPage.axaml.cs:1494,1511`、`RelayCommand.cs:10-19` |

---

## 1. `FullScreenClockViewModel` 全景

### 1.1 依赖与字段（`ViewModels\FullScreenClockViewModel.cs:32-129`）

`class FullScreenClockViewModel : INotifyPropertyChanged`（:30）。

| 字段 | 类型 / 作用 | 行 |
|---|---|---|
| `_settings` | 注入的 `PluginSettings`（单例） | :32, :137 |
| `_decibelService` | `DecibelMeterService`，UI 进程内音频管线 | :33, :138 |
| `_cameraService` | `CameraActivityService`，摄像头占用快照 | :34, :139 |
| `_serviceProvider` | 每次刷新时 `GetRequiredService<ILessonsService>()` | :35, :140 |
| `_earthquakeReader` | 反射读「地震预警」插件 | :36, :141 |
| `_counter` | `NoiseCounter`（计数层，纯算法） | :37 |
| `_exactTimeService` | `Lazy<IExactTimeService?>`，取 CI 虚拟时间；异常吞掉返回 null | :38, :144-148 |
| `_updateTimer` | 1 s `System.Timers.Timer` | :39 |
| `_window` | `FullScreenClockWindow`，懒创建 | :40 |
| `_timeOffsetSeconds` | CI `TimeOffsetSeconds` 缓存（正=打铃提前） | :45, :1764-1765 |
| `CountNormalBrush` / `CountNoisyBrush` 及 Light 版 | 计数黄/红语义色，静态缓存 | :48-51, :54-57 |
| `CheckIcon` | 对勾矢量字形（「暂未记录到吵闹」前的图标） | :66 |
| `SlotNames` | 五档名，直接取 `NoiseLevelDisplay.SlotNames` | :70 |
| `SlotBrushes` / `SlotTextBrushesDark` / `SlotTextBrushesLight` / `SlotReachedBrushes*` | 五档轨道色/未达到色/已达到色，静态缓存 | :73-81, :88-105 |
| `SlotFadeDark=0.35` / `SlotFadeLight=0.5` | 非当前档淡化程度 | :84-85 |
| `_noiseLevelSlots` | `ObservableCollection<NoiseLevelSlotItem>` | :107 |
| `_currentTime/_currentDate/_currentCourseName/_currentClassName` | 显示状态 | :109-112 |
| `_classSummaries` | `List<ClassSummary>`（**只写不读**，见 §7） | :113 |
| `_isWindowVisible/_isInBreak/_classStartTime` | 窗口与课程状态机 | :114-116 |
| `_todaySlots` / `_scheduleDate` | 当天课表缓存 + 该缓存属于哪一天 | :118-119 |
| `_antiScreensaverTimer` | 25 min 防休眠续期 | :120 |
| `_noisyDisplaySignature` | 计数区脏标记 | :121 |
| `_reminderData` | 提醒数据聚合（`ReminderData`） | :125 |
| `_weatherAlerts` | 天气预警缓存（60 s 一读） | :127 |
| `_reminderTick` / `_lastWeatherRead` | 3 s / 60 s 节拍 | :128-129 |
| `_showCountRules` | 悬停计数区时显示记录规则浮层 | :122, :552-556 |

### 1.2 构造与事件订阅（:131-158）

- 构造时订阅三条链：`_decibelService.PropertyChanged`、`_decibelService.NoiseEventRaised`、`_settings.PropertyChanged`（:150-152）。
- 建 5 个 `NoiseLevelSlotItem`（:154-155），随后 `ApplySlotTheme()` + `UpdateNoiseLevelSlots()`（:156-157）。
- **`Show()` 里会再退订/重订一次 `PropertyChanged`（:1525-1526）**，是为了「先退订再订阅，保证不累积」。

`_settings.PropertyChanged` 的分发逻辑（`OnSettingsPropertyChanged`，:173-262）是 VM 的「热更新总线」：

| 设置项 | 反应 | 行 |
|---|---|---|
| `ClockFontSize` / `CourseInfoFontSize` / `CountFontSize` | 立即搬到界面（补一条即时通道，避免 200 ms 防抖导致拖字号发顿） | :175-188 |
| `SkipFirst3Min` / `SkipFirstMinutes` / `NoisySustainSeconds` / `FallWindowSeconds` | 只重算 `CountRulesText` | :189-195 |
| `ShowReminderPanel` / `ShowWeatherReminder` / `ShowCountdownReminder` / `ShowTextReminder` / `ShowRainReminder` | `NotifyReminderChanged()`（重算可见性与合并） | :196-203 |
| `ShowEarthquakeReminder` | 立即 `UpdateEarthquakeAlert()` | :204-208 |
| `ShowAlertsReminder` / `ShowCameraReminder` / `CameraAlertFontSize` / `MonitoredCameras` | 摄像头监视启停 + `UpdateAlertItems()` | :209-224 |
| `ShowEmojiSubtitles` | 重算四条副标题 | :225-233 |
| `ShowBellTime` | 立即 `UpdateCourseInfo(NowVirtual)` | :234-238 |
| `BackgroundEnabled` / `BackgroundThemeMode` | `Dispatcher.Post(ApplyThemePalette)`（主题主导权切换） | :239-244 |
| `BackgroundImageFile` / `BackgroundDim` | 仅「自动」背景主题档才重判调色板 | :245-252 |
| `ThemeMode` | `ApplyThemePalette()` + `RefreshAppearanceBindings()` | :253-261 |

### 1.3 属性清单（按功能分组）

**时间/日期/课程**
- `CurrentTime` :308、`CurrentDate` :314、`CurrentCourseName` :320、`ShowCourseInfo` :660、`CourseInfoText` :666、`CourseProgress` :675、`ShowCourseProgress` :684、`CourseInfoFontSize` :697、`WindowTitle` :710（→ `_settings.WindowTitle`）。

**主题/外观**
- `Chrome`（`ClockBrushes`，整批底色/文字画刷）：:344；`ClockFontFamily` :350；`ClockFontFeatures`（`tnum`，秒数跳动不抖）：:353-354；`ClockFontSize` :690。
- 6 个颜色以表达式体直读设置：`BackgroundColor/FontColor/ProgressColor/CourseInfoColor/NoiseTitleColor/RainColor` :333-338；`ProgressFillBrush` :360、`ProgressTrackBrush` :363、`RainColorBrush` :366、`WeatherBrush`（降水天气码→降水色）:369-372。
- `RefreshAppearanceBindings()` :378-394（窗口 `Hide` 复用后表达式 getter 不会自动重算，故手动全量通知）；`RefreshChrome()` :400-408；`RefreshSemanticColors()` :414-435；`ApplySlotTheme()` :438-447；`ApplyThemePalette()` :456-461；`RefreshClockFont()` :464-470。

**音量条**
- `AnimatedProgress`（私有 set）:473-478、`CurrentSlot => SlotFromProgress(AnimatedProgress)` :483、`NoiseLevelText` :486、`NoiseLevelTextBrush` :489、`NoiseLevelSlots` :492、`NoiseLevelFillBrush` :495、`NoiseLevelTrackBrush` :498、`UpdateNoiseLevelSlots()` :501-509。
- 唯一档位源原则：轨道位置、五档高亮、右侧状态文字全部由 `AnimatedProgress` 反推（:69 注释、:482-489）。

**计数区/「正在记录」**
- `NoisyDisplaySegments` :547、`ShowCountRules` :552、`IsRecordingVisible` :572-584、`RecordingText` :610、`RecordingForeground` :617、`RecordingInMeterTitle` :598、`RecordingInCounter` :601、`NotifyRecordingPlacement()` :603-607、`UpdateRecording()` :628-639、`CountNormalForeground`/`CountNoisyForeground` :60-63。
- 「正在记录」站位是**计算属性**（音量条开着→标题行正中；关掉→计数行），因为 Avalonia 绑定做不了 AND+取反（:586-607）。

**提醒**
- 天气：`WeatherIcon` :715、`WeatherText` :734、`WeatherBrush` :372、`RainReminderTitle` :720、`ShowRainReminder`（开关 && 有数据）:733、`ReminderText` :735、`CountdownText`（标题 + 剩余，双空格连接）:738-747。
- 可见性组合：`ShowWeatherRow` :768、`ShowAlertsRow` :770、`ShowCountdownRow` :771、`ShowTextRow` :772、四个合并标志 :945-990、面板项 `ShowRainReminderPanel/ShowCountdownPanel/ShowTextPanel` :775-777、`ShowReminderPanel`（总开关 && 至少一项在面板）:778-779。
- 预警：`AlertItems`（`ObservableCollection<AlertDisplayItem>`）:750、`ExpandedAlertDetail` :754-765、`HasExpandedAlert` :765。
- 布局宽度：`MergeThreshold` :992-1004（默认 640）、`ReminderPanelMaxWidth` :1062-1073（默认 520）、`CourseInfoMaxWidth` :1080-1090（默认 +∞）、`EarthquakeAlertMaxWidth` :1096-1106（默认 +∞）。

**地震**：`ShowEarthquakeAlert` :794、`EarthquakeLine1/2` :797-800、`EarthquakeAlertBrush/Background/Border` :803-809、`EarthquakeLine1FontSize = clamp(ClockFontSize*0.18,26,96)` :812、`Line2 = clamp(*0.12,18,64)` :815。

**位置限位**：`ClockPositionMin/Max`、`VolumePositionMin/Max` :890-898，`SetPositionLimits` :901-907，`ResetPositionLimits` :910，`SetLimit` :912-917。限位由窗口按实际布局算出后回写，设置页滑块直接绑定它们（`Views\FullScreenClockWindow.cs` 中调用；设置页侧见 `Settings\SettingsPage.axaml.cs:721-724`）。

### 1.4 对外交互入口（无 ICommand）

VM 暴露的方法是命令式 API，由窗口、托盘、设置页直接调用：

| 方法 | 作用 | 行 |
|---|---|---|
| `Show()` | 创建/复用窗口，重置计数与状态，建课表、订阅课程事件、启动主循环/防休眠/音量/摄像头 | :1454-1540 |
| `Minimize()` | 最小化 + 拉出 CI 主窗口 + 排「最小化暂停」闹钟 | :1546-1555 |
| `Hide()` | 退出大屏：停全部后台工作、拉出 CI 主窗口 | :1653-1668 |
| `ExitFullScreen()` | 等价 `Hide()` | :2127 |
| `RefreshForPreview()` | 只把显示值从设置重读一遍，不碰窗口/不启监测（设置页预览用） | :1439-1452 |
| `SetPositionLimits/ResetPositionLimits` | 位置限位下行 | :901-910 |
| `VirtualNow` / `GetNextTargetCourseStart()` | 供课前预烘（`BackdropPreloadService`）判断「离点亮剩两分钟」 | :1725-1749 |

设置页按钮则用 Click 事件 + `RelayCommand`（`RelayCommand.cs:10-19`，`CanExecute` 恒 true，无 `RaiseCanExecuteChanged`）。

窗口回调：`_window.Closed` 里把一切停掉并 `_window=null`（:1476-1488）；`Activated → EnsureFullScreen()`（:1491）兜底拉回全屏（Avalonia 11.3 无 `WindowStateChanged`，:1489 注释）；`ActualThemeVariantChanged → ApplyThemePalette()`（:1493）。

### 1.5 定时器与主循环

`StartUpdateTimer()`（:1670-1677）：`new System.Timers.Timer(1000){AutoReset=true}`，Elapsed → `Dispatcher.UIThread.Post(RefreshAll)`。`StopUpdateTimer()`（:1679-1685）Stop+Dispose+置 null。

`RefreshAll()`（:1687-1722）每秒执行：

1. `CurrentTime = now.ToString("HH:mm:ss")`、`CurrentDate = "yyyy年M月d日 dddd"`（:1690-1691）；
2. `UpdateDateSubtitle()`（:1692）；
3. `CurrentCourseName` 取 `ILessonsService.CurrentSubject?.Name`，`try/catch` 吞异常（:1694-1699）；
4. **课程定位用 `NowVirtual`**（:1701-1702）；
5. `UpdateNoisyDisplay()`（脏标记，内容未变不重建，:1704-1705）；
6. `UpdateAlertItems()`（每秒合成摄像头/预警，内容未变直接 return，:1707-1709）；
7. `UpdateEarthquakeAlert()`（横波倒计时每秒变，:1711-1712）；
8. `_reminderTick++`：`%3==0` 刷组件，`(DateTime.Now-_lastWeatherRead) >= 60s` 刷天气（:1714-1721）。

其它定时器：

| 定时器 | 周期 | 行 |
|---|---|---|
| 音量动画 `_progressAnimTimer`（DispatcherTimer） | 33 ms | :516 |
| 防休眠续期 `_antiScreensaverTimer` | 25 min | :2109 |
| 最小化暂停 `_minimizePauseTimer`（System.Timers，AutoReset=false） | `MinimizePauseMinutes` 分钟 | :1563-1585 |
| 设置页预览 `_previewSyncTimer` | 200 ms | `Settings\SettingsPage.axaml.cs:191` |
| 布局玻璃冻结 `_layoutAdjustTimer` / `_layoutPinWatch` | 300 ms | `Settings\SettingsPage.axaml.cs:599,670` |
| 音量看门狗 | 5 s | `Services\DecibelMeterService.cs:194` |

`NowVirtual`（:164-171）：`_exactTimeService.Value?.GetCurrentLocalDateTime() ?? DateTime.Now`，即 CI 的「虚拟本地时间（含时间偏移）」；**真实时钟仍显示 `DateTime.Now`**（:160-163 注释、:1689）。这条分离是有意的，但会让「时钟读数」与「课程/打铃」相差 offset（见 §7）。

### 1.6 进度计算

`Models\ProgressCalculator.cs:15-20`：`total=(end-start).TotalSeconds; if(total<=0) return 0; clamp((now-start)/total*100,0,100)`，纯函数、无依赖、便于单测。

`UpdateCourseInfo` 中：`if (current != null) { CourseProgress = Calc(time,current.Start,current.End); ShowCourseProgress = true; } else { ShowCourseProgress=false; CourseProgress=0; }`（:1861-1872）。上课槽与课间槽用同一公式，故「上课=本课进度、课间=休息进度」。

### 1.7 嵌套/同文件类型

| 类型 | 可见性 | 字段 | 行 |
|---|---|---|---|
| `TimeSlot` | `internal class` | `Start/End/IsClass/SubjectName` | :2134-2140 |
| `ClassSummary` | `internal class` | `ClassName/Normal/Noisy` | :2145-2150 |
| `NoisyDisplaySegment` | `public`，`INotifyPropertyChanged` | `Text/Foreground/Icon/HasIcon`，增量同步用 | :2157-2195 |
| `AlertDisplayItem` | `public`，`INotifyPropertyChanged` | `DisplayText/Icon/Level/Foreground(可写)/Detail/FontSize/IconFontSize=FontSize-1` | :2198-2231 |

`NoisyDisplaySegment` / `AlertDisplayItem` 都可写 Foreground：换主题时**就地改色不重建列表**，因为重建会把鼠标下的元素整链移除，触发 `PointerExited/Entered` 循环（:2152-2156、:2058-2061、:418-419、:1337-1341）。

---

## 2. 课程状态机与打铃对齐

### 2.1 课表来源（`LoadTodaySchedule`，:1751-1823）

- CI 数据目录：`Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..","..",".."))`（:1756，同 `CiDataDir` :1409）。
- 读 `Settings.json` → `SelectedProfile`（默认 `"7.json"`）、`TimeOffsetSeconds`（:1759-1765）。
- 读 `Profiles/{SelectedProfile}`，要求存在 `TimeLayouts` / `ClassPlans` / `Subjects`（:1767-1774）。
- 选今天的 `ClassPlan`：跳过 `IsOverlay==true`，`TimeRule.WeekDay == (int)DayOfWeek`，`IsEnabled==true`，取第一个命中（:1782-1792）。
- 按 `TimeLayouts[tlId].Layouts` 顺序生成 `TimeSlot`：`TimeType==0` 记为上课（:1801-1816），`StartTime/EndTime` 用 `TimeSpan.Parse`（:1802-1803）。
- 课程名映射：**按顺序下标 `ci` 递增**去匹配 `ClassPlan.Classes[ci].SubjectId`，再从 `Subjects` 取 `Name`（:1797-1815）——依赖 CI 布局文件里「上课槽的出现顺序 == Classes 数组顺序」，无校验。
- 异常统一 `Debug.WriteLine("[ViewModel] 加载课表: {ex}")`（:1819-1822）。

### 2.2 事件驱动状态机（`OnTimeStateChanged`，:1964-2004）

订阅：`SubscribeToClassEvents()`（:1911-1923）先退订再订阅（幂等，注释标「P0 修复」:1916），订阅后立即 `Post(OnTimeStateChanged)` 检查当前状态（:1919-1920）；`UnsubscribeToClassEvents()` :1925-1933。

| CI 状态 | 动作 | 行 |
|---|---|---|
| `TimeState.OnClass` | 若上一节课程名不同且计数 >0 → 存 `_classSummaries`；`_currentClassName=subject`；`_counter.SetBreak(false)`；`StartNewClass()`（清零）；`_classStartTime = LookupActualClassStart(subject) ?? NowVirtual`；`_isInBreak=false`；刷计数与「正在记录」 | :1974-1989 |
| `TimeState.Breaking` | 若计数 >0 存摘要；`_counter.SetBreak(true)`；`_isInBreak=true`；刷新 | :1990-2000 |
| 其它（`Preparing` 等） | 不处理（保持原状态） | — |

`LookupActualClassStart(subject)`（:1939-1952）：在 `_todaySlots` 里找 `IsClass && SubjectName==subject && now.TimeOfDay ∈ [Start,End)`，返回 `now.Date + slot.Start`；找不到返回 null → 保护期从「进屏时刻」起算。

`BuildSummary()` :1954-1962；`Show()` 里重置 `_counter`/`_currentClassName`/`_classSummaries`/`_isInBreak`（:1513-1521）。

### 2.3 显示与进度（`UpdateCourseInfo`，:1825-1873）

```
for i in slots: if time ∈ [s.Start, s.End): current=s; 从 i+1 起找第一个 IsClass && SubjectName 非空 → nextClass; break
if current.IsClass && SubjectName非空:  "当前课程为 {name}   {Start:hh\:mm} — {End:hh\:mm}{FormatBellTime}"
else if _isInBreak && nextClass != null: "下节课程为 {name}   ...{FormatBellTime}"
else: ""
```
（:1831-1859）

注意：
- `nextClass` **只在命中当前槽时才计算**（:1837-1841），所以不在任何时段内时永远没有「下节课」文案。
- 课间时 `current` 通常是 `IsClass=false` 的课间槽，故落入第二个分支（:1851）。
- 进度条对「当前槽」无条件显示，课间即休息进度（:1861-1872）。

### 2.4 时间偏移与打铃（`FormatBellTime`，:1879-1887）

```
if (!_settings.ShowBellTime || _timeOffsetSeconds == 0) return "";
offset = TimeSpan.FromSeconds(_timeOffsetSeconds);
bellStart = slot.Start - offset; bellEnd = slot.End - offset;
fmt = offset%60==0 ? hh\:mm : hh\:mm\:ss;
return "（实际打铃 {bellStart} — {bellEnd}）";
```

- 语义：「正 = 打铃提前」（:43-44），所以 `实际打铃 = 课表时间 − offset`（:1877 注释）。
- 只在 `ShowBellTime` 开且 offset≠0 时追加（`Settings\SettingsPage.axaml:51-55` 是它的子开关）。
- **偏移只在 `LoadTodaySchedule` 时读一次**（:1764-1765）；改 CI 时间偏移后需重开大屏或跨天（见 §7）。

### 2.5 状态小结

| 变量 | 含义 | 何时改 |
|---|---|---|
| `_isInBreak` | 当前是否课间 | `OnTimeStateChanged` :1986/:1997；`Show()` :1518 |
| `_currentClassName` | 当前课名 | :1982（上课分支） |
| `_classStartTime` | 本节「实际开始」时刻 | :1985 |
| `_counter`（Normal/Noisy） | 本节计数 | `StartNewClass()` :1984；`OnEvent` :298 |
| `_todaySlots/_scheduleDate` | 当日课表 | `LoadTodaySchedule` :1751；`GetNextTargetCourseStart` 跨天重读 :1735-1740 |
| `_timeOffsetSeconds` | CI 时间偏移 | :1764-1765 |

---

## 3. 提醒合并、排序与预警展示

### 3.1 数据来源与刷新

| 数据 | 来源 | 频率 | 行 |
|---|---|---|---|
| 倒计时 / 文本 | CI `IComponentsService.CurrentComponents`，递归遍历 `Lines→Children`，`CiComponentsReader.Extract`（**不过滤 `IsActive`**，注释说明实测不可靠） | 3 s | :1254-1295（:1277-1280 注释） |
| 天气码/温度/预警 | CI `data\Settings.json` 的 `LastWeatherInfo`（`current.weather`、`current.temperature.value`、`alerts[]`） | 60 s | :1298-1335；`Services\CiWeatherReader.cs:62-101` |
| 降雨提醒 | `LastWeatherInfo.minutely.precipitation` 优先，`forecastHourly.weather` 次选 | 60 s | :1330-1331；`CiWeatherReader.cs:121-211` |
| 摄像头占用 | `CameraActivityService.Occupancy` 快照 + `CameraAlertBuilder.Build` | 1 s | :1342-1352 |
| 地震 | 反射读「地震预警」插件 | 1 s | :835-875 |

天气图标：`CiWeatherReader.WeatherIcons`（weathercn 码→Lucide 名）→ `IconGlyph.Of`；未收录时温度计兜底（:1320-1326）。

### 3.2 可见性组合（:767-779）

```
ShowWeatherRow    = ShowWeatherReminder && HasWeather
ShowAlertsRow     = _reminderData.HasAlerts          // 开关过滤在 UpdateAlertItems 里做
ShowCountdownRow  = ShowCountdownReminder && HasCountdown
ShowTextRow       = ShowTextReminder && HasText
ShowRainReminder  = ShowRainReminder && RainReminderTitle 非空   (:733)
ShowRainReminderPanel     = ShowRainReminder    && !ShowRainMergedInFirstRow
ShowCountdownPanel        = ShowCountdownRow    && !ShowCountdownMergedInFirstRow
ShowTextPanel             = ShowTextRow         && !ShowTextMergedInFirstRow
ShowReminderPanel         = settings.ShowReminderPanel && (Alerts || 任一面板项)
```
天气**永不在面板**、预警**始终在面板**（:769、:774 注释）。

### 3.3 第一行合并算法（`UpdateReminderMerge`，:1007-1048）

设计规则原文（:931-937）：
- 天气固定第一行，与弹幕同行；
- 合并优先级 **降雨 > 倒计时 > 文本**，总宽 ≤ 屏幕 1/3 才并入；放不下的留在提醒面板；
- 无天气时主位由倒计时顶替，再无就文本框；
- 天气预警始终单开一行/多行；
- 颜文字显示在主提醒正下方。

实现（伪代码）：

```
weather = ShowWeatherRow; rain = ShowRainReminder; countdown = ShowCountdownRow; text = ShowTextRow
spacing = 36                                  // 与 XAML 第一行 StackPanel.Spacing 对齐（玻璃底要占位）
if weather:
    used = IconWidth(20) + 6 + TextWidth(WeatherText, 19)
    TryMerge(used, rain,      IconWidth(20)+6+TextWidth(RainReminderTitle,19), …)   // 优先级 1
    TryMerge(used, countdown, IconWidth(20)+6+TextWidth(CountdownText,19),   …)   // 优先级 2
    TryMerge(used, text,      TextWidth(ReminderText, 16),                    …)   // 优先级 3
else if countdown: countdownMerged = true      // 无天气：倒计时顶主位
else if text:      textMerged = true           // 再无：文本框顶主位
ShowWeatherInFirstRow = weather（:1038）
```

- `TryMerge`（:1051-1060）：`!candidate || candidateWidth<=0` 直接放弃；`used+spacing+candidateWidth <= threshold` 才 `used+=…; merged=true`（**不可回退、按优先级贪心**）。
- 阈值 `MergeThreshold` 由视图写入：`Views\ClockSurface.axaml.cs:354` `vm.MergeThreshold = Bounds.Width/3.0`；属性 setter 变化时立刻重算（:993-1003），默认 640（:1004）。
- 面板最大宽由 `Views\ClockSurface.axaml.cs:353` 设为 `Bounds.Width*2/3`。
- 内容/开关变化统一经 `NotifyReminderChanged()`（:1386-1406）→ `UpdateReminderMerge()`（:1404）触发。该函数末尾 `OnPropertyChanged(ShowReminderPanel)` 写了两次（:1399、:1405，小冗余）。

**宽度测量**：`TextWidth` 用 `TextLayout` + 硬编码 `Typeface("Microsoft YaHei")` 与字号 19/16（:1234-1238）；图标宽直接按字号（`IconWidth(fontSize)=>fontSize`，因图标字形在私有区，用中文字体量会错，:1240-1244）。→ 与真实渲染字号/字体不完全一致，属技术债（§7）。

### 3.4 颜文字副标题

| 副标题 | 规则 | 行 |
|---|---|---|
| `RainSubtitle` | `_rainHours ∈ [0,6]` 才显示「记得带伞哦」，颜文字开关决定是否带 `(ノω≦)` | :1112-1125 |
| `WeatherSubtitle` | 雪码 → 保暖；雾霾码 → 戴口罩；否则按温度：≥36 多喝水 / ≥33 防晒 / ≤0 保暖 / ≤8 多穿 | :1130-1133, :1177-1197 |
| `DateSubtitle` | 周四「撑住！明天就能回家了！」/ 1 号「新的一个月，加油」（注释称「深夜>周五>月初」，实现无深夜分支） | :1199-1210 |
| `CountdownSubtitle` | 剩余天数 ∈ (0,30] → 「冲刺啦，冲鸭」 | :1212-1223 |

均由 `ShowEmojiSubtitles` 总开关控制（:1181/:1203/:1216），XAML 中放在各提醒块正下方（`Views\ClockSurface.axaml:54-55,65-66,76-77,96-97`）。

### 3.5 预警 / 摄像头 / 弹幕

`UpdateAlertItems()`（:1342-1376）：
1. `cameraAlerts = ShowCameraReminder ? CameraAlertBuilder.Build(MonitoredCameras, Occupancy, CameraAlertFontSize) : empty`（:1345-1348）；
2. `merged = ShowAlertsReminder ? _weatherAlerts : empty` + `cameraAlerts`（:1350-1352）——**摄像头条目不收「预警」开关影响**；
3. 变更比对：数量、逐项 `Title/Level/Detail/FontSize`（:1354-1358），**不变直接 return**（避免重建列表打断悬停弹幕，:1337-1341）；
4. 变了才 `AlertItems.Clear()`、`ExpandedAlertDetail=null`（:1360-1361）、逐条新建 `AlertDisplayItem`（:1362-1371）、同步 `_reminderData.Alerts` 并 `NotifyReminderChanged()`（:1373-1375）。
5. 颜色：`AlertBrush(level, isLight)`（:1379-1383，蓝/黄/橙/红，浅底压暗版）；换主题时就地改 `Foreground`（:419-421）。

弹幕（预警详情横向滚动）：VM 侧只有 `ExpandedAlertDetail` 字符串（:754-765），视图侧悬停某条 → `vm.ExpandedAlertDetail = item.Detail`（`Views\ClockSurface.axaml.cs:525,549`），移出/收起 → null（:588,688），顶部 `TextBlock` 绑定它（`Views\ClockSurface.axaml:90`）。

### 3.6 地震提醒（:781-929）

- `UpdateEarthquakeAlert()` 每秒读一次（:835-875）：「粘住」策略——一旦显示就记下横波到达时刻 `_earthquakeEndTime`，中途读不到数据也自行倒数；但对方换场（`EventKey` 变化）立即收起（:824-834 注释、:856-862）。
- 内容未变不通知（`next == _earthquakeAlert` 提前 return，:850）。
- 颜色按本地烈度分档（`SemanticColors.EarthquakeHex`，:819），背景/描边用同色不同 alpha（:806-809）。

### 3.7 README 与实现对照

| README 条目 | 实现 | 结论 |
|---|---|---|
| `README.md:22` 天气固定第一行主位 | `ShowWeatherInFirstRow = weather`（:1038） | ✅ |
| `README.md:22` 降雨 > 倒计时 > 文本，能放进 1/3 屏并入同行 | :1018-1027 顺序与贪心 `TryMerge` | ✅（但仅当**有天气**时尝试；见 §7） |
| `README.md:22` 无天气时主位由倒计时顶替 | :1029-1032 | ✅ |
| `README.md:22` 颜文字在主提醒正下方 | 各块 `Spacing=2` + 副标题 TextBlock | ✅（视图：`Views\ClockSurface.axaml:47-97`） |
| `README.md:22` 天气预警始终单开一行/多行 | `ShowAlertsRow` 独立于第一行合并（:770） | ✅ |
| `README.md:20` 悬停预警滚动详情 | `ExpandedAlertDetail` + 视图 hover | ✅ |
| `README.md:13` 课间显示下节课程 | :1851-1855 | ✅（前提：处于某个时段槽内） |

---

## 4. 噪音统计段模式与音量五档联动

### 4.1 管线总览（音频线程 → 检测器 → 事件 → UI）

```
NAudio WaveInEvent（默认设备0，失败逐台回退）
  └ OnDataAvailable（Services\DecibelMeterService.cs:260-321）
       ├ 原始 RMS = sqrt(sum(abs²)/samples)（:271-279）
       ├ 每块把设置同步进检测器（阈值/起算底线/回落窗口）（:282-287）
       ├ dt = samples / SampleRate（不假设 100ms）（:291-293）
       ├ NoiseEventDetector.Process(rawRms, dt)（:294）
       ├ NoiseLevelDisplay.ProgressOf(level, smooth, 三阈值) → Post(NoiseLevelProgress)（:299-301）
       ├ result.EventFired → NoiseEventRaised 事件（:304-308）
       ├ SegmentLevel 变化 → Post(CurrentSegmentLevel)（:311-316）
       └ WriteDebugLog（:318）
  └ VM.OnDecibelPropertyChanged（ViewModels\FullScreenClockViewModel.cs:267-287）
       ├ NoiseLevelProgress → _targetProgress + StartProgressAnimation()
       ├ IsMonitoring / NoiseLevelText → 直接刷右侧文字与画刷
       └ CurrentSegmentLevel → UpdateRecording()
  └ VM.OnNoiseEventRaised（:294-302）→ Post UI → _counter.OnEvent(level, IsInProtection())
```

### 4.2 检测器段状态机（`NoiseDetection\NoiseEventDetector.cs`）

参数默认（:50-72）：

| 参数 | 默认 | 设置项 | 说明 |
|---|---|---|---|
| `QuietThreshold` | 0.005 | `DecibelQuietThreshold` | 安静上界 |
| `GoodThreshold` | 0.015 | `DecibelGoodThreshold` | 良好上界 |
| `NormalThreshold` | 0.04 | `DecibelNormalThreshold` | 一般下界 |
| `NoisyThreshold` | 0.08 | `DecibelNoisyThreshold` | 吵闹下界 |
| `SmoothAlpha` | 0.2 | —（未暴露） | 100 ms 块下的 EMA 系数，按块长折算保持等效时间常数（:124-127） |
| `HysteresisFactor` | 0.8 | —（未暴露） | 迟滞：降级用阈值×0.8（:196-228） |
| `SustainSeconds` | 1.0 | `NoisySustainSeconds`（0.5~5） | 起算底线（:57-61） |
| `FallWindowSeconds` | 5.0 | `FallWindowSeconds`（0~15） | 回落窗口（:63-67） |
| `NoisyRatioThreshold` | 0.5 | —（未暴露） | 段内吵闹占比 ≥0.5 记吵闹（:69-72） |

段状态字段（:79-84）：`_segmentActive`、`_segmentNormalSeconds`、`_segmentNoisySeconds`、`_fallSeconds`、`_segmentLevel`。

`Process`（:112-188）核心规则：
1. 进入可计等级（Normal/Noisy，`IsCountable` :190-191）：若无段则开段清零（:138-144）；`_fallSeconds=0`；按当前等级累加有效时长（:147-149）；`total ≥ SustainSeconds` 后按占比给出**实时归属** `_segmentLevel`（:152-157）——这就是 UI「正在记录：一般/吵闹」。
2. 掉到不可计等级：`_fallSeconds += dt`；**超过** `FallWindowSeconds` 才段结束（:159-176），结算时只有 `total ≥ SustainSeconds` 才 `fired`（:165-170）；否则丢弃（喷嚏/咳嗽）。
3. 每段**至多一次事件**，只在段结束时产（:43-45 注释、:163-170）。

迟滞分级 `Classify`（:196-228）：升级用名义阈值，降级用 `×0.8`，`Normal→` 掉到 `normalDown` 以下时还要再看 `goodDown` 决定到 Good 还是 Quiet（:214-218）。

`Reset()` :95-106（每次 `StartMonitoring` 调用，`DecibelMeterService.cs:124`）。

### 4.3 计数层（`NoiseDetection\NoiseCounter.cs`）

- `SetBreak(bool)` :26；`StartNewClass()` 清零 :29-33。
- `OnEvent(level, isProtected)`（:39-53）：`_inBreak || isProtected → None`；`Noisy++/Normal++`；其余 None。**V2.1 无冷却**（:14-17 注释）。

VM 侧保护判定 `IsInProtection()`（:304-306）：
```
_settings.SkipFirst3Min && (NowVirtual - _classStartTime).TotalSeconds < _settings.SkipFirstMinutes * 60
```
`_classStartTime` 来自 `LookupActualClassStart`，即**课表上的真实上课时刻**（:1985），不是进屏时刻——因此「上课初期保护」与打铃对齐。

### 4.4 「正在记录」与计数显示

- `UpdateRecording()`（:628-639）：`show = CurrentSegmentLevel.HasValue && ShowNoisyCounter && !_isInBreak && className 非空 && !IsInProtection()`；文案「正在记录：一般/吵闹」；颜色黄/红。
- 站位：`RecordingInMeterTitle = IsRecordingVisible && ShowDecibelMeter`，`RecordingInCounter = IsRecordingVisible && !ShowDecibelMeter`（:598-601）——为不引起玻璃重烘，塞进音量条标题行已有的空位（:586-597 长注释）。
- 计数区 `BuildNoisySegments()`（:2010-2027）：`ShowNoisyCounter` 关闭 / 课间 / 无课名 → 空；课名非「数据」而是标签，仅在 `ShowCourseInfo` 关闭时才显示课名（:2023-2024）。
- `AddNoisyCountSegments()`（:2030-2056）：有计数 → 「一般 N 次」「吵闹 N 次」；否则保护期内显示「上课初期保护中（m:ss 后开始记录）」+ 沙漏图标（:2039-2048），过了保护期或未开启 → 「暂未记录到吵闹」+ 对勾（:2062-2066），但右侧「正在记录」亮着时不显示（:2064）。
- 增量同步：`UpdateNoisyDisplay()`（:2068-2091）用 `Text|Icon|颜色类别(Y/R/P)` 组成签名，变化才就地改已有片段，**不清空重建**（避免悬停 Popup 一秒闪一次，:2078-2080）。

### 4.5 音量五档与阈值联动

两套常量必须恒等（单测锁死，注释 `ViewModels\LevelSlotCalculator.cs:5-8`）：

| 档位 | 进度区间 | 检测器分级 | 轨道色 |
|---|---|---|---|
| 0 安静 | `<20` | Quiet | `0xFF4CAF50` |
| 1 良好 | `<40` | Good | `0xFFAED581` |
| 2 一般 | `<60` | Normal | `0xFFFFDE02` |
| 3 吵闹 | `<80` | Noisy 且 `<1.3×noisy` | `0xFFFF9933` |
| 4 嘈杂 | `_` | Noisy 且 `≥1.3×noisy` | `0xFFFF5555` |

- `LevelSlotCalculator.SlotFromProgress`（:12-19）；`SlotColors` :22-23；`SlotTextColorsDark/Light` :32-39；`ReachedColorOf`（只有「一般」已达到色单独亮黄）:49-50。
- `NoiseLevelDisplay.ProgressOf`（`NoiseDetection\NoiseLevelDisplay.cs:44-57`）把每档映射到各自的 20% 段：安静 0-20、良好 20-40、一般 40-60、吵闹 60-80、嘈杂 80-100，`Band` 夹在端点保证不越档（:60-65）。`SlotOf`（:24-31）用 `VeryNoisyFactor=1.3`（:19）细分嘈杂。
- VM 动画：收到 `NoiseLevelProgress` 只记目标并启动 33 ms 表（:271-275, :512-521），`OnProgressAnimTick` 指数逼近（`+= delta*0.5`，:538），`|delta|<0.2` 直接对齐并停表（:531-535）；**只有跨档位才刷文字/高亮/填充色**（:540-541）。
- `NoiseLevelSlotItem`（:11-85）：`TextBrush = IsCurrent ? CurrentBrush : Brush`（:43）；`TextOpacity` 当前 1.0 / 其余 `FadeOpacity`（:76）；`TextFontSize` 当前 19 / 其余 15（:79）；`SetStyle` 换主题（:53-58）。

设置页的五档阈值联动：对数刻度 `-3~0` 映射 `0.001~1.0`（`Settings\SettingsPage.axaml.cs:909-924`），后一级滑块的 `Minimum` 绑定前一级当前值（`Settings\SettingsPage.axaml:285,290,295`，属性 :922-924），再加**链式钳制** `Good=max(Good,Quiet)`、`Normal=max(Normal,Good)`、`Noisy=max(Noisy,Normal)`（`SettingsPage.axaml.cs:1002-1007`）——保证「永远不会乱序」（README:12）。

### 4.6 起算/回落/保护 三处参数的实际入口

| UI 项 | 设置属性 | 作用点 |
|---|---|---|
| 起算底线 | `NoisySustainSeconds` :414-418 | 检测器 `SustainSeconds`（`DecibelMeterService.cs:286`、检测器 :153,166） |
| 回落窗口 | `FallWindowSeconds` :424-428 | 检测器 `FallWindowSeconds`（`DecibelMeterService.cs:287`、检测器 :163） |
| 采样间隔 | `SamplingIntervalSeconds` :435-439 | `bufferMs`（`DecibelMeterService.cs:145`），决定采样频率与 dt |
| 上课初期保护 | `SkipFirst3Min` :395-399 + `SkipFirstMinutes` :404-408 | VM `IsInProtection` :304-306 → 计数层 :41 |
| 记录规则浮层文案 | `CountRulesText` :1414-1427 | 悬停计数区（`Views\ClockSurface.axaml.cs:767-773`） |

---

## 5. `PluginSettings` 配置模型

`Models\PluginSettings.cs`，`class PluginSettings : INotifyPropertyChanged`（:9）。构造函数给两个 `ObservableCollection` 挂 `CollectionChanged → OnPropertyChanged`（:72-76）——集合内容变化也触发保存防抖。

### 5.1 逐项说明（分组 / 默认值 / 变更行为）

**A. 触发课程（:81-85, :678-679）**
- `TargetCourseNames: ObservableCollection<string>`，默认 `{ "晚自习" }`（:11）。
- `IsTargetCourse(name) => CourseMatch.IsMatch(...)`（:678-679）：**精确匹配**（去首尾空白、忽略大小写；`Models\CourseMatch.cs:14-26`）。历史从「包含匹配」改为精确（`CourseMatch.cs:6-9` 注释，issue #1）。

**B. 分贝阈值（:90-121）** 默认 0.005 / 0.015 / 0.04 / 0.08；setter 只赋值 + 通知，**钳制在设置页做**（`SettingsPage.axaml.cs:1002-1007`）。

**C. 颜色（:126-332）** 默认：背景 `#000000`、字体 `#ffffff`、进度 `#4FC3F7`、课程信息 `#88ccff`、音量标题 `#bbffffff`、降水 `#7FD4FF`（:16-21）。均为 HEX 字符串，直接 `Color.Parse`。

**D. 主题（:145-161）**
- `ThemeMode`：0 跟随 CI / 1 明亮 / 2 黑暗（背景开启时失效；Auto=3 仅用于 `BackgroundThemeMode` 转换，见 `Models\ClockTheme.cs:4-22`）。
- `AppliedPaletteVariant`：当前颜色属于哪套调色板（`VariantUnknown=-1` 作「老配置未迁移」标记，`ClockTheme.cs:105-112`；`Helpers\ClockThemeApplier.cs:62`）。

**E. 背景与玻璃（:174-297）**
- `BackgroundEnabled`（默认 **false**，:171-173 注释：升级配置没有该字段，默认开会在用户没操作时换掉主题）；
- `BackgroundSectionExpanded`（纯界面状态但存盘，:185-189）；
- `BackgroundThemeMode`（0 自动/1 暗底白字/2 亮底黑字，钳 0~2，:219-223）；
- `BackgroundImageFile`（只存副本文件名，空=纯色，:228-232）；
- `BackgroundImageStretch`（0 铺满裁切/1 完整显示/2 拉伸，钳 0~2，:237-241）；
- `BackgroundDim`（-100 压暗~100 提亮，钳，:247-251）；
- `GlassMode`（0 关闭/1 半透明/2 磨砂/3 真折射，钳 0~3，:256-260）；
- `GlassOpacity`（0~100，:265-269）、`GlassBlurRadius`（0~64，:274-278）、`GlassRefraction`（0~100，:283-287）、`GlassFrost`（0~100，:293-297）。

**F. 显示开关（:348-352, :358, :367-390）** `ShowDecibelMeter`/`ShowCourseInfo`/`ShowBellTime`/`ShowNoisyCounter` 默认 true；`LetterDismissed` 默认 false（:26, :358-362）。

**G. 记录参数（:395-439）** `SkipFirst3Min` true、`SkipFirstMinutes` 3（钳 0~10）、`NoisySustainSeconds` 1.0（钳 0.5~5）、`FallWindowSeconds` 5.0（钳 0~15）、`SamplingIntervalSeconds` 0.2（钳 0.1~1.0）。

**H. 调试日志（:444-481）** `EnableNoiseDebugLog` false、`LogRetentionDays` 3（1~5）、`LogKeepByDuration` true、`LogKeepMinutes` 10（1~120）、`LogKeepMinKb` 100（10~3072）。

**I. 布局字号（:486-528）** `ClockFontSize` 180（**无钳制**）、`CourseInfoFontSize` 28、`CountFontSize` 24、`ClockPositionPercent` 30（钳 0~100）、`VolumePositionPercent` 80（钳 0~100）。

**J. 提醒（:540-586）** `ShowReminderPanel`/`ShowWeatherReminder`/`ShowAlertsReminder`/`ShowCountdownReminder`/`ShowTextReminder`/`ShowRainReminder`/`ShowEmojiSubtitles` 默认 true。

**K. 运行（:589-610）** `ShowTrayMenuEntry` true、`PauseWhenMinimized` true、`MinimizePauseMinutes` 5（1~30）。

**L. 摄像头/地震（:613-666）** `ShowCameraReminder` true、`CameraAlertFontSize`（钳 `MinCameraAlertFontSize=AlertInfo.DefaultFontSize=15` ~ `Max=19`，:623-637）、`ShowEarthquakeReminder` true、`MonitoredCameras` 集合、`CameraListFilled` false。

**M. 不落盘的界面态** `AdjustingLayout`（`[JsonIgnore]`，:191-213）——拖动布局期间收起玻璃，见 `Views\BackdropLayer` 注释；`Plugin.Initialize` 强制归零（`Plugin.cs:44-46`）。

**N. 两套主题颜色槽** `LightColors` / `DarkColors`（:340-343，默认 `ThemeColorSet.FromPalette(ClockTheme.Light/Dark)`）——切主题时先把当前色存回旧槽、再读新槽（`ClockThemeApplier.cs:16-25,74-95`）。

### 5.2 保存链路（`Plugin.cs`）

1. `ConfigureFileHelper.LoadConfig<PluginSettings>(ConfigFolder/Settings.json)`（:41-42）；
2. 若配置文件已存在 → `ClockThemeApplier.MigrateLegacySettings(Settings)`（:50-51）；
3. 任意 `Settings.PropertyChanged` → 400 ms 防抖计时器（`AutoReset=false`）→ `SaveSettings()` → `ConfigureFileHelper.SaveConfig`（:22-35, :54-62）。
4. 需要「立即落盘」的地方显式调用 `Plugin.SaveSettings()`：课程列表（`SettingsPage.axaml.cs:816`）、摄像头勾选（:871, :897）。

迁移语义（`ClockThemeApplier.MigrateLegacySettings` :60-67）：仅当 `AppliedPaletteVariant == VariantUnknown` 时执行一次——把老颜色原样收进黑暗槽、`ThemeMode=Dark`、标记变体为 Dark。避免「跟随 CI」在首启把老用户的外观换掉。

---

## 6. `SettingsPage` 交互实现模式

### 6.1 结构

- `[SettingsPageInfo("com.bigscreen.clock.settings", "大屏时钟", SettingsPageCategory.External)]` + `[FullWidthPage]`（`Settings\SettingsPage.axaml.cs:27-30`）：全宽页，标题固定靠左以对齐卡片。
- `class SettingsPage : SettingsPageBase, INotifyPropertyChanged`（:31）；构造函数 `InitializeComponent()` → `_settings = Plugin.Settings!` → 查地震插件 → 取单例 VM（:38-43）→ `DataContext = this`（:56）。
- `Loaded` 挂三组 hook：位置限位、布局滑块、共享事件订阅 + 弹「致用户的一封信」；`Unloaded` 全部摘除（:45-54）——注释说明 CI 每次打开都可能新建页面，不摘会泄漏（:45-47）。
- 左右两栏：`<Grid ColumnDefinitions="*,360">`，左栏 `ScrollViewer` 里若干「卡片 Border」（用 CI 的 `CardBackgroundFillColorDefaultBrush` / `CardStrokeColorDefaultBrush` / 圆角 4），右栏预览 + 「进入全屏时钟」按钮（`Settings\SettingsPage.axaml:15-24, 699-728`）。

### 6.2 绑定模式

- 视图绑的是 **SettingsPage 自己的包装属性**（`DataContext = this`），每个 setter 的模式固定为：
  `if (Math.Abs(old-value) < eps) return; _settings.X = value; OnPropertyChanged(nameof(X)); OnPropertyChanged(nameof(XText));`（如 `GlassOpacity` :469-482、`BackgroundDim` :340-351）。
- 只读派生属性负责显示：`GlassOpacityText` :482、`BackgroundDimText` :353-358、`ClockFontSizeText` :1433、`LogKeepLabel/Minimum/Maximum/Tick/ValueText` :1159-1164、`IsOpacityRowVisible/IsGrainRowVisible/IsLiquidRowVisible` :432-434、`IsAppearanceThemeEnabled` :244、`BackgroundSectionOpacity`（关闭时整组 0.4 压暗，因为 `IsEnabled=false` 不会让自写 Foreground 的说明文字变灰，:266-271）。
- 反向同步：`OnSettingsPropertyChanged`（:115-130）只特殊处理**被大屏回写的两个位置**（`ClockPositionPercent`/`VolumePositionPercent`），其余一律 `SchedulePreviewSync()`。
- 双 RadioButton 共用一个 bool 设置：`LogKeepByDuration` / `LogKeepBySize`（取反视图，setter 只在 `value==true` 时写，:1110-1132），两者共用一个滑块（`LogKeepValue`，:1147-1157）。
- 列表项交互：`CourseSelectionItem.SelectedName` 变 → `_parent.OnCourseSelectionChanged()` → `SaveCourses()`（:1496-1512, :800, :810-817）；摄像头 `CameraDeviceItem.IsSelected` 变 → `SaveCameraSelection()`（:1465-1475, :891-898）。

### 6.3 实时预览

- 预览不是重画的小样，而是把大屏的 `ClockSurface` 本身等比塞进 `Viewbox`（`SettingsPage.axaml:699-716`），`DataContext = PreviewVm`（:140，单例 VM）。
- 尺寸按大屏所在屏幕的**逻辑尺寸**（物理 ÷ `Scaling`）算（:150-178），预览框 `Width/Height` 用 `PreviewWidth/Height`（:145-148）。
- 刷新：`StartPreviewSync()`（:187-203）建 200 ms `DispatcherTimer`，Tick 里 `_previewSyncing=true; _clockViewModel.RefreshForPreview(); finally false`；`RefreshForPreview()`（`ViewModels\FullScreenClockViewModel.cs:1439-1452`）把字号/开关/颜色/字体/提醒数据重读一遍且不动窗口、不启监测。
- 防自激：`_previewSyncing` 时 `SchedulePreviewSync()` 直接 return（:207-216）。
- 主题：构造末尾 `ClockThemeApplier.Apply` + `ApplyThemeFromSettings` + `NotifyAllColorsChanged`（:58-63），并监听 `ActualThemeVariantChanged`（:70-71）。

### 6.4 HSV 颜色选择器

- XAML 用 `Avalonia.Controls.ColorPicker` 的 `cp:ColorPicker`（`SettingsPage.axaml:3, 596-664`），`Color="{Binding ...ColorValue}"` 双向（默认 TwoWay），`IsAccentColorsVisible="False"`，`Content` 里塞一个 `Border` 作为大色块预览。
- 代码侧做 `Color ↔ HEX string` 转换：`ColorValue` 的 get 用 `DisplayColor`（解析失败按黑，:1348-1352），set 用 `value.ToString()`（`#AARRGGBB`，:1330-1412）；另暴露 `XxxColorBrush` 与 `XxxColorHex`（`#RRGGBB`，去 alpha 显示更友好，`ToHexRgb` :1431）。
- 背景色不可调（由主题决定），所以设置页只有 5 个选择器（`SettingsPage.axaml:587-589` 注释）。

### 6.5 阈值与联动控件

见 §4.5：`DecibelXxxThresholdIndex`（对数刻度，:926-965）、`DecibelXxxThresholdText`（直接填数，`SetThreshold` :988-996）、`XxxThresholdSliderMin`（动态最小值 :922-924）、`NotifyThresholdsChanged()`（一次通知 12 个属性 :1012-1025）。

### 6.6 布局拖动：玻璃冻结（pin）

问题背景：拖位置/字号时每帧重排，玻璃层每帧出多个整屏中间面，内存在 CI 看门狗上限附近（注释 :568-576；`PluginSettings.AdjustingLayout` :191-213）。

实现要点：
- `BeginLayoutAdjust()`：`_settings.AdjustingLayout=true` + 重启 300 ms `DispatcherTimer`（:589-595）。
- 定时器 Tick：若 `_layoutPinned` 则只启动兜底轮询、不停表；否则停表并 `AdjustingLayout=false`（:597-613）。
- `HookLayoutSliders()`（:621-637）给 6 根滑块（时钟/课程/计数字号、两个位置、摄像头字号）挂 `PointerPressed/Released/CaptureLost`，**路由策略必须是 Tunnel + `handledEventsToo:true`**，因为 Slider 模板里的 Thumb 会把事件标 Handled（:615-620 注释）。
- 兜底轮询 `IsLayoutPointerBusy()`（:688-697）：检查指针是否仍被这几根滑块的视觉后代捕获；不是就当作已松手（宁可早放不能晚放，:681-687）。
- `StopLayoutAdjust()`（:712-717）：页面收起时清 pin 并解开冻结，同时停掉两个 `DispatcherTimer`（防泄漏）。

### 6.7 位置限位下发

- 设置页把 `ClockPositionMin/Max`、`VolumePositionMin/Max` 直接绑成 Slider 的 `Minimum/Maximum`（`SettingsPage.axaml:570-586`；属性 :721-724），数据来自单例 VM（:729-737 订阅 `PropertyChanged`，:745-759 分发）——避免滑块能拖进「屏幕上早已顶住」的死区（:719-720 注释）。

### 6.8 其它交互

| 功能 | 实现 | 行 |
|---|---|---|
| 背景图选择/清除 | `StorageProvider.OpenFilePickerAsync`（jpg/jpeg/png/bmp/webp/gif）→ `BackgroundImageStore.Replace/Clear` | :360-406 |
| 课程下拉 | `ComboBox` 绑 `AllCourses` + `SelectedItem`，删除按钮 `Command="{Binding RemoveCommand}"` | `SettingsPage.axaml:196-212`；`CourseSelectionItem` :1489-1513 |
| 摄像头 | 枚举设备、首次自动全勾选（`CameraListFilled`）、刷新按钮 | :853-882, :891-898；axaml :141-168 |
| 地震插件可用性 | `CheckEarthquakePlugin()` 反射探测，没装则开关置灰 + 提示 | :1283-1295；axaml :123-137 |
| 打开日志目录 | `Process.Start(UseShellExecute=true)` | :1167-1181 |
| 致用户信 | `LetterWindow.ShouldShow()` / `MarkShown()`，`DispatcherPriority.Background` 延后弹 | :79-96 |
| 进入全屏 | `TestButton_Click` 从 DI 取单例 VM `.Show()` | :903-907 |

---

## 7. 问题与技术债（按严重度）

**P1 · 逻辑与文档/需求不一致**

1. **无天气时降雨永不并入第一行**：`UpdateReminderMerge` 只在 `if (weather)` 分支里 `TryMerge(rain…)`（`ViewModels\FullScreenClockViewModel.cs:1018-1027`），无天气时只处理 countdown/text（:1029-1036）。于是「未来有雨但天气数据缺失」时，降雨提醒会落到提醒面板，而 README 的优先级语义（`README.md:22` 降雨 > 倒计时 > 文本）暗示降雨应最先占用第一行。属需求/实现缺口（或 README 表述过宽）。
2. **同一课程连续两节丢计数**：`OnClass` 分支只在 `_currentClassName != subjectName` 时才 `_classSummaries.Add`（:1977-1981），随后无条件 `_counter.StartNewClass()` 清零（:1984）。若课表两节同名（如「晚自习」连堂），第一节计数被静默丢弃。
3. **`_classSummaries` 只写不读**（死代码）：字段 :113，写入 :1979/:1994，`Show()` 清空 :1517，全项目无任何读取/显示；但 `ClassSummary` 的注释称「课间/换课时显示用」（:2142-2144）。要么补 UI，要么删。
4. **`DateSubtitle` 注释与实现不符**：注释称「深夜 > 周五 > 月初」（:1199），实现只有周四（:1206）与 1 号（:1207），没有深夜分支；README:23 的「别熬啦」文案也从未出现。

**P1 · 时间/日期状态易陈旧**

5. **课表只在特定时机重载**：`LoadTodaySchedule()` 仅在 `Show()`（:1521）、`ResumeFromMinimizePause()`（:1617）、以及 `GetNextTargetCourseStart()` 发现跨天（:1735-1740）时调用；`RefreshAll`/`UpdateCourseInfo` 不检查 `_scheduleDate`（:1687-1873）。大屏整晚开着跨过午夜后，`_todaySlots` 仍是昨天的课表（进度条/课程文案会错），且 `_isInBreak` 由 CI 事件驱动会继续变，二者可能打架。
6. **时间偏移只读一次**：`_timeOffsetSeconds` 在 `LoadTodaySchedule` 里读（:1764-1765），此后 CI 改偏移/切课表档案都不会反映到「实际打铃」行，直到重开大屏或跨天。
7. **`Show()` 后课表可能被读两遍**：`Show()` 调 `LoadTodaySchedule()`（:1521）但未设 `_scheduleDate`（默认 `DateTime.MinValue`）；随后首次 `GetNextTargetCourseStart()` 判定日期不同再 `LoadTodaySchedule()`（:1735-1740）。多一次 JSON 解析，无功能危害。
8. **时钟与课程用两口钟**：`CurrentTime` 取 `DateTime.Now`（:1689），课程/进度/保护取 `NowVirtual`（:1702/:1985/:304-306）。设计如此（注释 :160-163），但两者差 `TimeOffsetSeconds` 时，「显示的时间」与「进度条/保护倒计时」不对齐，容易被当成 bug 反馈。

**P2 · 健壮性/耦合**

9. **主窗口靠类名反射定位**：`SetMainWindowVisible` 在 `desktop.Windows` 里找 `typeName.Contains("MainWindow")`（:1899-1903）。CI 改类名/多窗口时会失效，且无日志。
10. **`UpdateReminderMerge` 用硬编码字体/字号量文本宽**：`TextWidth` 固定 `Typeface("Microsoft YaHei")`，调用处字号写死 19/16（:1019-1027, :1234-1238），与真实 XAML（CI 字体、主题字号、Viewbox 缩放）不一致；合并判定在字体/缩放下可能偏宽或偏窄。图标宽直接用字号近似（:1244）。
11. **每秒重复分配**：`UpdateAlertItems` 每秒重建摄像头 `List<AlertInfo>` 并 `Zip` 比较（:1342-1358, 调用点 :1709）；`ProgressFillBrush`/`RainColorBrush`/`AlertBrush` 每次 get 都 `new SolidColorBrush`（:360, :366, :1379-1383）；长跑时有持续 GC 压力（虽已对高频项做了静态画刷缓存，:72-81）。
12. **`PluginSettings` 主题槽不通知**：`LightColors`/`DarkColors` 是普通自动属性（:340-343），`ClockThemeApplier.Capture/Restore` 直接改它们的字段（`Helpers\ClockThemeApplier.cs:74-95`）**不触发 `PropertyChanged`**，因此不会触发 400 ms 保存；目前靠同一流程里改 6 个颜色属性「搭便车」落盘。若将来只改槽位就会丢配置。
13. **集合替换后丢失通知**：`TargetCourseNames`/`MonitoredCameras` 的 `CollectionChanged` 只在构造函数里对初始实例挂接（:72-76），若反序列化/外部赋新集合，后续 `Add` 不再触发保存（现有路径靠显式 `Plugin.SaveSettings()` 兜底：`SettingsPage.axaml.cs:816,897`）。
14. **「正在记录」颜色靠文案匹配**：`RecordingText.Contains("吵闹")`（:568），任何文案改动都会让颜色错档。
15. **计数区脏标记用颜色类别字符串**：`signature` 只区分 Y/R/P（:2071-2073），换主题时靠清空签名强制重建（:428-429）。约定脆弱，新增语义色就要记得同步改这两处。
16. **保护期起点可能退化为「进屏时刻」**：`_classStartTime = LookupActualClassStart(...) ?? NowVirtual`（:1985）；课表未加载/课名不匹配时保护期从打开大屏那一刻算起，与实际打铃不对齐（`LookupActualClassStart` :1939-1952）。
17. **`ShowAlertsRow` 不直接乘开关**：`_reminderData.HasAlerts`（:770）的正确性依赖 `UpdateAlertItems()` 先把被过滤的列表写回 `_reminderData.Alerts`（:1351, :1373-1374）。任何绕过 `UpdateAlertItems` 修改 `_weatherAlerts` 的路径都会让面板总开关误判。
18. **地震读数每秒反射**：`UpdateEarthquakeAlert` 每秒 `_earthquakeReader.Read`（:835, :1712），且只要 `reading.Alert` 存在就重置 `_earthquakeEndTime = now+SecondsLeft`（:849）；对方数据抖动时会不断把倒计时往后拉。
19. **`SetThreadExecutionState` 线程归属**：注释明确 `ES_CONTINUOUS` 记的是调用线程（:2103-2105），Start/Stop 与 25 min 续期都 Post 到 UI 线程才安全（:2110, :2115-2124）。当前正确，但属易被改坏的隐性约束。

**P3 · 小问题/风格**

20. **`LevelSlotCalculator` 与 `NoiseLevelDisplay` 的 20/40/60/80 是两份独立常量**（`ViewModels\LevelSlotCalculator.cs:12-19` vs `NoiseDetection\NoiseLevelDisplay.cs:38-57`），只靠注释与单测保证一致，无编译期约束。
21. **阈值文本解析用当前区域文化**：`double.TryParse` 未指定 `CultureInfo.InvariantCulture`（`SettingsPage.axaml.cs:990`），逗号小数点的系统上填 `0.01` 可能解析失败。
22. **RadioButton 取反视图的 setter 忽略 false**（`SettingsPage.axaml.cs:1117,1127-1128`）：这是 Avalonia RadioButton 的常见 workaround，但外部代码/绑定无法直接把 `LogKeepBySize` 置 false。
23. **`UpdateReminderMerge` 末尾重复通知** `ShowReminderPanel` 两次（:1399, :1405）。
24. **`OnSettingsPropertyChanged` 覆盖不完整**：玻璃、背景图填充方式、拉伸等设置变更只走 `SchedulePreviewSync()`（`SettingsPage.axaml.cs:115-130`），大屏若正开着需等 `ClockSurface` 自身绑定/重绘才生效；未见专门的即时刷新分支（VM 只处理了背景开关/主题/图片/明暗，:239-252）。
25. **课表解析无校验**：`ClassPlan.Classes[ci]` 按顺序下标映射（:1797-1815），`TimeType` 非 0 的槽不消耗 `ci`；若 CI 布局里出现「半天/自习」等特殊 `TimeType`，映射可能整体错位（无断言/日志，异常只 `Debug.WriteLine`）。
26. **`StartUpdateTimer` 不防重入但有判空**（:1670-1677）；`Hide()` 与 `PauseForMinimize()` 都会 `StopUpdateTimer`（:1602, :1660），恢复路径分散在 `Show`/`ResumeFromMinimizePause`/`EnsureFullScreen` 三处（:1508, :1619, :1646），状态机分散易漏（例如 `EnsureFullScreen` 只恢复暂停、不重启已停的定时器——若窗口被外部关闭后 `_window=null`，`Activated` 已无窗口可触发）。

---

### 附：与本报告相关的关键文件

| 文件 | 作用 |
|---|---|
| `ViewModels\FullScreenClockViewModel.cs` | 大屏 VM：时钟循环、课程状态机、提醒合并、噪音显示、窗口生命周期 |
| `ViewModels\LevelSlotCalculator.cs` / `NoiseLevelSlotItem.cs` | 五档纯计算 / 单档数据项 |
| `NoiseDetection\NoiseEventDetector.cs` | EMA + 迟滞 + 段状态机（起算底线/回落窗口/占比归属） |
| `NoiseDetection\NoiseCounter.cs` | 计数层：课间/保护期不计数，无冷却 |
| `NoiseDetection\NoiseLevelDisplay.cs` | 分级 + 平滑音量 → 0-100 进度（与五档边界一一对应） |
| `Services\DecibelMeterService.cs` | NAudio 采集、看门狗重连、调试日志、事件源 |
| `Models\PluginSettings.cs` | 全部配置项与默认值/钳制 |
| `Settings\SettingsPage.axaml(.cs)` | 设置页：包装属性绑定、预览、阈值联动、布局 pin、颜色选择器 |
| `Plugin.cs` | 设置加载/迁移/400 ms 防抖保存、DI 注册 |
| `Models\ProgressCalculator.cs` / `ClassStartSchedule.cs` | 纯算法：进度百分比 / 下一节目标课 |
| `Helpers\ClockThemeApplier.cs` / `Models\ClockTheme.cs` | 主题档位解析、两套调色板槽位与迁移 |
| `Services\CiWeatherReader.cs` | CI 天气缓存读取、降雨判定、码表 |
| `Views\ClockSurface.axaml.cs` | 合并阈值/面板宽度下发、弹幕 hover、记录规则 hover、尺寸变化 |
