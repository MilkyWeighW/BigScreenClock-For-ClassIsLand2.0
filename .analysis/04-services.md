# 04 服务层与外部集成分析（task-3）

> 范围：`Services/`、`NoiseDetection/`、`Models/` 中与服务层耦合的模型，以及它们在 `ViewModels/FullScreenClockViewModel.cs` 中的集成点。仅静态阅读，未运行程序；涉及系统 API 语义（winmm 设备编号、Media Foundation 释放顺序）的结论标注了「需实测」。
>
> 项目：ClassIsland 2.0 插件「大屏时钟」，net8.0-windows / Avalonia 11.3，`manifest.yml:10` apiVersion 2.0.0.0，仅 Windows（`manifest.yml:12-13`）。

---

## 0. 结论速览（按重要性）

1. **麦克风采集链是「逐设备试探 + 5 s 看门狗」的自愈结构**，但重启动作通过 `Dispatcher.UIThread.Post` 排队时不再复查 `_shouldMonitor`（`Services/DecibelMeterService.cs:191-208`），存在「Hide 之后被排队的看门狗重新拉开麦克风」的竞态；一旦发生，后续 `StartMonitoring` 会因 `_isMonitoring==true` 直接早退（`Services/DecibelMeterService.cs:120-122`），看门狗也不再启动，等于麦克风常开且失去自愈。详见 §2.4、§9-P1。
2. **设置页暴露的「安静阈值」不参与分级判定**：`DecibelQuietThreshold` 会被写入检测器（`Services/DecibelMeterService.cs:282`），但 `NoiseEventDetector.Classify` 只用到 Good/Normal/Noisy 三档（`NoiseDetection/NoiseEventDetector.cs:196-228`），`QuietThreshold` 属性（`NoiseDetection/NoiseEventDetector.cs:50`）在全仓库再无读取方；它唯一的实际作用是当「良好阈值滑块」的左端点与 clamp 下界（`Settings/SettingsPage.axaml.cs:922,1004`）。用户调「安静」档只会挪动滑块范围，安静/良好的实际分界恒为 `GoodThreshold`。§9-P1。
3. **摄像头占用检测整体建立在未公开的 Media Foundation 传感器 API + CsWin32 上**，`CameraActivityService` 的 COM 生命周期不完整（`Services/CameraActivityService.cs:256-266`），并且每次活动报告都用 `Process.GetProcessById(...).ProcessName` 而不 `Dispose`（`Services/CameraActivityService.cs:209-213`），属于持续性的句柄/引用泄漏。§3、§9-P2。
4. 与 ClassIsland 的集成有两条截然不同的路径：**DI 服务（ILessonsService / IComponentsService / IExactTimeService / ITaskBarIconService / INotificationHostService）** 与 **直接读 CI 数据目录的 JSON + 反射**。后者（天气、课程表、地震、组件）不依赖 CI 版本，但依赖文件布局/内部字段名，任何一侧改名都会静默降级。§4、§9-P2。
5. 地震提醒是每秒一次的完整反射链（`Services/EarthquakeReader.cs:62-114`，由 `ViewModels/FullScreenClockViewModel.cs:1712` 每秒触发），失败的异常被吞成「没有地震」，历史上已因此静默失效过一次（`Services/EarthquakeReader.cs:161-168` 注释）。§5。

---

## 1. 服务清单与生命周期

注册在 `Plugin.cs:64-74`：

| 类型 | 注册方式 | 生命周期/启动时机 | 释放 | 线程 |
|---|---|---|---|---|
| `PluginSettings` | `AddSingleton(Settings)`（`Plugin.cs:64`） | 插件 `Initialize` 时从 `Settings.json` 加载（`Plugin.cs:41-42`），改属性 400 ms 防抖存盘（`Plugin.cs:22-23,54-62`） | — | UI 线程改，服务层只读 |
| `DecibelMeterService` | `AddSingleton`（`Plugin.cs:66`） | 构造即注入，但硬件只在 `Show()` 时开（`ViewModels/FullScreenClockViewModel.cs:1530`） | `IDisposable`（`Services/DecibelMeterService.cs:486-490`）→ `StopMonitoring`；由 DI 容器在宿主关闭时调用 | 音频回调线程（winmm/WaveInEvent 线程池）+ UI 线程（状态、看门狗 Post） |
| `CameraActivityService` | `AddSingleton`（`Plugin.cs:67`） | 同上，`Show()` 时若开提醒才 `StartMonitoring`（`ViewModels/FullScreenClockViewModel.cs:1532`） | `IDisposable`（`Services/CameraActivityService.cs:256-266`） | MF 回调线程写 `_occupancy`（`volatile`，`Services/CameraActivityService.cs:38`），UI 线程读 |
| `FullScreenClockViewModel` | `AddSingleton`（`Plugin.cs:68`） | 由 `AutoTriggerService` 的构造依赖触发实例化（宿主启动时） | 自身实现 `IDisposable`（VM 类声明） | 所有公开入口用 `Dispatcher.UIThread.Post` 切 UI 线程 |
| `AutoTriggerService` | `AddSingleton` + `AddHostedService`（`Plugin.cs:69,72`） | `BackgroundService`，延迟 1 s 后订阅课程事件（`Services/AutoTriggerService.cs:43,62-64`） | `finally` 里退订（`Services/AutoTriggerService.cs:77-82`） | 线程池启动，回调在 CI 事件线程 → 直接调 `Show/Hide`（内部自带 Post） |
| `ServiceProviderCapture` | `AddHostedService`（`Plugin.cs:71`） | 构造时把 `IServiceProvider` 写进 `Plugin.ServiceProvider`（`Services/ServiceProviderCapture.cs:11-14`） | — | 启动线程 |
| `BackdropPreloadService` | `AddHostedService`（`Plugin.cs:73`） | `BackgroundService`，5 s 后进入 20 s 轮询（`Services/BackdropPreloadService.cs:62-71`） | `finally` 清缓存并退订设置事件（`Services/BackdropPreloadService.cs:77-81`） | 后台 + `Dispatcher.UIThread.InvokeAsync` 取课表/屏幕信息（`Services/BackdropPreloadService.cs:93-94`） |
| `TrayMenuService` | `AddHostedService`（`Plugin.cs:74`） | `StartAsync` 立即返回，2 s 后 `Post` 注册菜单（`Services/TrayMenuService.cs:41-58`） | `StopAsync` 空实现（`Services/TrayMenuService.cs:60`） | UI 线程（`Dispatcher.UIThread.Post`） |

要点：
- **服务从不自启硬件**：麦克风、摄像头都绑定「大屏可见」这一生命周期（`ViewModels/FullScreenClockViewModel.cs:1530-1532`），`Hide()`/窗口 `Closed`/最小化超时暂停都会关掉（`ViewModels/FullScreenClockViewModel.cs:1476-1488,1597-1608,1653-1668`）。这是本项目资源策略的核心，比「常驻后台采集」保守。
- **`AutoTriggerService` 被注册了两次**（`Plugin.cs:69` 单例 + `Plugin.cs:72` hosted）。`AddHostedService<T>` 走 `TryAddEnumerable(Singleton<IHostedService,T>)`，得到的是**另一个实例**；`AddSingleton<AutoTriggerService>` 只是让手动解析可用（`ManualShow/ManualHide`），宿主不会调用它的 `ExecuteAsync`。目前无人解析这个单例，所以只是冗余注册 + 多构造一次（构造函数还会写一次 `Plugin.ServiceProvider`，`Services/AutoTriggerService.cs:37`）；若将来有代码 `GetService<AutoTriggerService>()` 去期待「自动触发已接好事件」，会拿到一个没有订阅事件的空壳。
- **异常兜底风格统一为「吞掉 + Debug.WriteLine」**：`AutoTriggerService.cs:49-53`、`BackdropPreloadService.cs:66-68`、`TrayMenuService.cs:52-55,100-103`、`Services/DecibelMeterService.cs:320`、`Services/CameraActivityService.cs:190-193`。生产环境（无 Debugger）这些异常完全不可见，只有 `StatusMessage`（麦克风）和设置页 `CameraHint`（`Settings/SettingsPage.axaml.cs:881`）两条用户可见通道。

---

## 2. 麦克风采集链（NAudio WaveIn → 检测器 → 计数）

### 2.1 状态机

字段：`_waveIn`（当前设备）、`_isMonitoring`（对外）、`_shouldMonitor`（用户意图）、`_lastDataReceived`（`Services/DecibelMeterService.cs:29-42`）。

- `StartMonitoring()`（`Services/DecibelMeterService.cs:120-130`）：`_isMonitoring` 为真则**直接 return**；置 `_shouldMonitor=true`，`_detector.Reset()`，`EnsureLogWriter()`，`StartMonitoringInternal()`，`StartWatchdog()`。
- `StartMonitoringInternal()`（`Services/DecibelMeterService.cs:132-184`）：
  - `WaveInEvent.DeviceCount == 0` → `StatusMessage="未检测到麦克风"` 并返回（`:136-141`）；
  - 缓冲：`bufferMs = Clamp(SamplingIntervalSeconds*1000, 100, 1000)`（`:145`），默认 0.2 s → 200 ms；
  - 从 `idx=0` 开始逐台尝试：`new WaveInEvent { DeviceNumber = idx, WaveFormat = new WaveFormat(44100,16,1), BufferMilliseconds = bufferMs }`，挂 `DataAvailable`/`RecordingStopped`，`StartRecording()` 成功后保存并 `IsMonitoring=true`（`:151-165`）；**第一台能开的就赢，不判断它是不是系统默认设备**；
  - 单台失败即 `StopRecording()+Dispose()` 清理（`:167-174`）；全部失败 → `StatusMessage="无法打开任何麦克风"`（`:177`）。
- `StopMonitoring()`（`:217-223`）：`_shouldMonitor=false` → `StopWatchdog()` → `StopMonitoringInternal()` → `CloseLogWriter(judge:true)`。
- `StopMonitoringInternal()`（`:225-242`）：先把 `_waveIn` 摘下来再停，避免 `RecordingStopped` 误判当前设备。
- `OnRecordingStopped`（`:244-258`）：用 `ReferenceEquals(sender, _waveIn)` 过滤旧设备的延迟事件（`:249`），然后**直接写 `_isMonitoring=false`**（`:251`）再 Post 通知，异常写 `StatusMessage`（`:256`）。设备被拔/被独占时靠它把状态纠正回来。

### 2.2 数据路径

`OnDataAvailable`（`Services/DecibelMeterService.cs:260-321`），运行在 winmm 回调线程：

1. `_lastDataReceived = DateTime.Now`（`:262`，看门狗据此判活）；
2. 按 16 bit little-endian 逐样本读 `abs`，累加平方和并记录峰值：`sum += v*v; peak = Max(peak, v)`（`:271-277`）；
3. `rawRms = Sqrt(sum / samples)`，`samples = BytesRecorded / 2`（`:279`）；
4. **每个数据块都从 `_settings` 重新同步 6 个参数**（`:282-287`）：`QuietThreshold`（无效，见 §9-P1）、`Good/Normal/NoisyThreshold`、`SustainSeconds`、`FallWindowSeconds`；
5. `dtSeconds = samples / WaveFormat.SampleRate`（`:291-293`）——用真实样本数算时长，不依赖 `BufferMilliseconds`，可容忍系统给块不匀；
6. `result = _detector.Process(rawRms, dtSeconds)`（`:294`）；
7. `NoiseLevelDisplay.ProgressOf(result.Level, result.SmoothedRms, 阈值...)` → `Post` 到 UI 更新 `NoiseLevelProgress`（`:299-301`）；
8. `result.EventFired` → 触发 `NoiseEventRaised`（`:304-308`）——**事件在音频线程触发**，VM 在 `OnNoiseEventRaised` 里再 Post（`ViewModels/FullScreenClockViewModel.cs:294-302`）；
9. `SegmentLevel` 变化 → Post `CurrentSegmentLevel`（`:311-316`）；
10. `WriteDebugLog(...)`（`:318`）；整段包在 `catch { }` 里（`:320`）。

### 2.3 阈值与时长参数

| 参数 | 默认 | 生效位置 | 说明 |
|---|---|---|---|
| `DecibelQuietThreshold` | 0.005 | `Services/DecibelMeterService.cs:282` 写入 | **不参与分类**；仅作为 Good 滑块的左端点/下界（`Settings/SettingsPage.axaml.cs:922,1004`） |
| `DecibelGoodThreshold` | 0.015 | `NoiseDetection/NoiseEventDetector.cs:51`、`:196-228` | 低于它 = Quiet |
| `DecibelNormalThreshold` | 0.04 | 同上 | Good/Normal 分界 |
| `DecibelNoisyThreshold` | 0.08 | `NoiseDetection/NoiseEventDetector.cs:53,72` | Normal/Noisy 分界；Noisy 还要求 `SmoothedRms > 阈值×1.3` 才显示「嘈杂」槽（`NoiseDetection/NoiseLevelDisplay.cs:19`） |
| `HysteresisFactor` | 0.8 | `NoiseDetection/NoiseEventDetector.cs:55` | 降级用 `阈值×0.8`，升级用名义阈值 |
| `SmoothAlpha` | 0.2 | `:54,126` | EMA 系数按 `dt/0.1 s` 幂次归一：`alpha = 1-(1-0.2)^(dt/0.1)` |
| `NoisySustainSeconds` | 1.0 s | `:61`；设置范围 0.5–5（`Models/PluginSettings.cs:417`） | 累计到该时长才定段等级 |
| `FallWindowSeconds` | 5.0 s | `:67`；范围 0–15（`Models/PluginSettings.cs:427`） | 回落到可计等级以下持续该时长才「结算一次事件」 |
| `NoisyRatioThreshold` | 0.5 | `:72` | 段内 Noisy 占比 ≥0.5 判 Noisy，否则 Normal |
| `SamplingIntervalSeconds` | 0.2 s | `Services/DecibelMeterService.cs:145`；范围 0.1–1（`Models/PluginSettings.cs:438`） | 只影响 `BufferMilliseconds` |

### 2.4 看门狗

`StartWatchdog`（`Services/DecibelMeterService.cs:191-208`）：`System.Timers.Timer(5000)`，`Elapsed` 在**线程池线程**上：

- `!_shouldMonitor` → return（`:197`）；
- `_isMonitoring && (Now - _lastDataReceived) <= 5 s` → return（`:198`，设备活着且 5 s 内来过数据）；
- 否则 `Dispatcher.UIThread.Post(() => { StopMonitoringInternal(); StartMonitoringInternal(); })`（`:201-205`）。

设计意图清楚（设备休眠/独占后自动重连），但有两点风险：
- **Post 的 lambda 内不复查 `_shouldMonitor`**：若 `StopMonitoring()` 恰在「检查通过」与「Post 落地」之间执行，排队的重启会在 Hide 之后把设备重新拉开；
- 重启后 `_isMonitoring=true`，而 `StartMonitoring()` 对 `_isMonitoring` 早退（`:120-122`）且不会 `StartWatchdog()`——`StopMonitoring` 已经 `StopWatchdog`，于是这个会话里看门狗永久失效。两处叠加是 §9-P1 的完整事故链。
- `_isMonitoring`、`_lastDataReceived`、`_shouldMonitor` 均无 `volatile`/锁（`:29-42`、`:251`），跨线程可见性依赖 x64 内存模型；`DateTime` 是 8 字节结构，32 位进程理论上可能撕裂（CI 通常 x64，风险低）。

### 2.5 调试日志

`Services/DecibelMeterService.cs:323-477`，默认关闭（`EnableNoiseDebugLog=false`，`Models/PluginSettings.cs:32`）：
- 目录为 `Plugin.ConfigFolder`，文件名 `NoiseDebugLog-yyyy-MM-dd-HH-mm-ss.csv`（`:358-361`），表头含当次阈值（`:382-390`）；
- 启动时清理超过 `LogRetentionDays`（3 天）的旧文件（`:348-353`）；停止时 `TryDeleteUnqualifiedLog`：按时长或大小不达标即删，**一条样本都没写的文件不删**（`:413-435`）；
- 运行中每 20 行 flush（`:458`），超过 `LogRollThresholdKb` 滚动（`:467-471`），阈值 = `LogKeepByDuration ? 2048 : Max(2048, LogKeepMinKb)`（`:58-59`）；
- `WriteLogHeader()`（`:377-380`）**是死代码**，实际写表头的是 `WriteLogHeaderLocked()`（`:382`）。

### 2.6 事件结算语义（重要行为约束）

`NoiseEventDetector.Process`（`NoiseDetection/NoiseEventDetector.cs:112-188`）：进入可计等级（Normal/Noisy）时开段并累计两类秒数（`:135-149`）；`total >= SustainSeconds` 时按占比给出**实时** `_segmentLevel`（`:152-157`）；**只有回落到可计等级以下、且连续 `FallWindowSeconds` 后才结算并触发一次 `EventFired`**（`:159-177`）。

推论（真实行为，非 bug 但容易被当成 bug 报告）：
- 一节课全程持续吵闹、中间没有 5 s 安静，整段**永远不会结算**，计数为 0；
- 下课/切课由 `NoiseCounter.StartNewClass()` 清零（`ViewModels/FullScreenClockViewModel.cs:1984`），未结算的段直接丢弃；
- 「吵闹次数」实际是「吵够 1 s 并以 ≥5 s 安静收尾的段数」。

---

## 3. 摄像头占用检测（Media Foundation 传感器活动监视）

`Services/CameraActivityService.cs`（281 行，`sealed class` + `IDisposable`，`:21`）。原理：Win10 1809+ 的 `MFCreateSensorActivityMonitor`，向系统注册「设备→进程」活动回调，系统告诉我们「哪个进程正在用哪台摄像头」。

### 3.1 初始化

- 常量 `MfVersion = 0x00020070`、`MfStartupLite = 1`（`:24,:27`）；
- `EnsureMediaFoundation()`（`:199-207`）：`MFStartup(0x00020070, MFSTARTUP_LITE)`，失败抛 `InvalidOperationException`；
- `StartMonitoring()`（`:53-86`）：`EnsureMediaFoundation` → `MFCreateSensorActivityMonitor(_callback, out monitor)`（`:63`）→ `_monitor.Start()`、`_started=true`、`IsSupported=true`（`:74-77`）；失败写 `LastError="创建摄像头监视器失败（0x...）"`（`:66`），并把 `_callback` 置 null。整个方法可重复调用，失败后下次 `Show()` 会重试。
- `StopMonitoring()`（`:89-98`）：只 `_monitor.Stop()` + 清空 `_occupancy`，**不释放 COM**，便于下次快速重启。

### 3.2 设备枚举（设置页用）

`EnumerateDevices()`（`:103-150`）：`MFCreateAttributes` + `SetGUID(MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE, MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE_VIDCAP_GUID)`（`:110-114`），然后因为 CsWin32 生成的 `MFEnumDeviceSources` 签名不便使用，改用**原始 P/Invoke `MFEnumDeviceSourcesRaw`**（`:118`，声明 `:249-252`），`Marshal.ReadIntPtr` 取数组（`:125`），`Marshal.GetObjectForIUnknown` 转 `IMFActivate`（`:128`），读 `FRIENDLY_NAME` / `SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK`（`:129-130`）；`finally` 里 `Marshal.Release(ptr)` + `CoTaskMemFreeRaw(array)`（`:134-142`）。

设备身份与显示：`Models/CameraDevice.cs`——`CameraDevice(Name, SymbolicLink)`（`:13`）、`CameraActivity(Name, SymbolicLink, Process)`（`:22`）；`CameraDeviceNaming.ShortId` 从符号链接第 3 段第 2 个 `&` 字段 + 哈希（`:38-47`），`DisplayNames` 仅在同名设备时追加短标识（`:64-93`）。设置项 `MonitoredCamera(Name, SymbolicLink)`，序列化用 `"name|||symbolicLink"`（`Models/MonitoredCamera.cs:16`），`Matches` 优先按路径、无路径退回名字（`:39-45`）。

### 3.3 活动回调

`OnActivitiesReport`（`Services/CameraActivityService.cs:153-196`），运行在 MF 后台线程：

- `IMFSensorActivitiesReport.GetCount` → 每台 `GetActivityReport` → `GetFriendlyName`/`GetSymbolicLink`；
- 每台设备 `GetProcessCount`，逐进程 `GetProcessActivity`，**优先挑 `streaming==true` 的进程名，没有就取第一个**（`:168-187`）；
- 整体替换 `_occupancy`（`:195`），读者拿到的是不可变快照（字段 `volatile IReadOnlyList<CameraActivity>`，`:38`）；
- 异常只 `Debug.WriteLine`（`:190-193`）——回调里抛异常会跨 COM 边界，宁可吞。
- `ProcessName` 解析：`Process.GetProcessById((int)pid).ProcessName`（`:209-213`），未见异常保护/Dispose。

消费方：`CameraAlertBuilder.Build(monitoredDevices, occupancy, titleFontSize)`（`Models/CameraAlertBuilder.cs:29-60`）→ `AlertInfo`，`AlertTitle="摄像头使用中"`、`Level="橙色"`、`Icon="Webcam"`、`Detail="{进程} 正在使用 {设备}"`（`:10,:13,:16,:52`），按设备名排序、用 `seen` 去重（`:45-46`）。VM 在 `UpdateAlertItems` 里调用并只与天气预警一起合并（`ViewModels/FullScreenClockViewModel.cs:1345-1352`），逐字段比较避免每秒重建（`:1354-1358`）。

---

## 4. 与 ClassIsland 主程序的集成点

### 4.1 通过 DI 拿到的服务

| 服务 | 拿法 | 用到的成员 | 用途 |
|---|---|---|---|
| `ILessonsService` | `GetRequiredService`（`ViewModels/FullScreenClockViewModel.cs:1696`、`1915`）、`GetService`（`Services/AutoTriggerService.cs:47`） | `CurrentSubject?.Name`（VM `:1697`）、`CurrentState`/`CurrentSubject`（`Services/AutoTriggerService.cs:99-100`）、事件 `OnClass` / `OnAfterSchool` / `CurrentTimeStateChanged`（`:62-64`）、VM 侧 `CurrentTimeStateChanged`（`ViewModels/FullScreenClockViewModel.cs:1915-1918`） | 课程名、当前状态、自动触发、状态切换重置计数 |
| `IComponentsService` | `GetService`（`ViewModels/FullScreenClockViewModel.cs:1258`） | `CurrentComponents`（`:1259`） | 读 CI 主界面倒计时/文本框组件 |
| `IExactTimeService` | `Lazy<>` + `GetService`（`:144-148`），取不到则 null | `GetCurrentLocalDateTime()`（`:168`） | **虚拟时间基准**（CI 的时间偏移），课程定位/进度/下课倒计时都用它 |
| `ITaskBarIconService` | `GetService`（`Services/TrayMenuService.cs:70`） | `MainTaskBarIcon?.Menu`（`:72`） | 往托盘右键菜单插「进入大屏时钟」 |
| `INotificationHostService` | `GetService`（`Services/EarthquakeReader.cs:136`） | 反射 `NotificationProviders`（`:140-142`） | 找地震预警插件实例 |
| `AppBase.Current.MainWindow` / `IClassicDesktopStyleApplicationLifetime` | 直接取静态（`Services/BackdropPreloadService.cs:177`、`ViewModels/FullScreenClockViewModel.cs:1630-1636`、`:1889-1909`） | 主窗口显隐、所在屏幕分辨率 | 大屏退出时还原 CI 主界面；预烘按主窗口所在屏 |

### 4.2 直接读 CI 数据目录（不经 API）

`CiDataDir = Path.GetFullPath(Path.Combine(Plugin.ConfigFolder!, "..", "..", ".."))`（`ViewModels/FullScreenClockViewModel.cs:1409`，`Services` 无独立计算），即 `Plugin.ConfigFolder`（`.../ClassIsland/Plugins/EveningSelfStudyClock`）向上三级。

- **课程表**：`CiDataDir/Settings.json` 的 `SelectedProfile`（默认 `"7.json"`）与 `TimeOffsetSeconds`（`ViewModels/FullScreenClockViewModel.cs:1758-1766`）→ `CiDataDir/Profiles/<profile>`（`:1768`），要求存在 `TimeLayouts`/`ClassPlans`/`Subjects`（`:1772-1774`）；按 `DateTime.Now.DayOfWeek` 选第一个 `WeekDay` 匹配、`IsEnabled`、非 `IsOverlay` 的 ClassPlan（`:1776-1792`）；按 TimeLayout 生成 `_todaySlots`，`TimeType == 0` 视为上课，`Classes` 顺序对齐 `SubjectId` → `Subjects.Name`（`:1797-1817`）。
- **天气**：`CiWeatherReader.ReadLastWeather(CiDataDir/Settings.json)`（`ViewModels/FullScreenClockViewModel.cs:1304`）。
- **降雨提醒**：同一文件再读两遍——`ReadRainHours`（`:1330`）、`ReadRainReminder`（`:1331`）。
- **组件**：不读文件，走 `IComponentsService.CurrentComponents`，再按类型名反射（`CiComponentsReader.CountdownSettingsTypeName = "CountDownComponentSettings"`、`TextSettingsTypeName = "TextComponentSettings"`，`Services/CiComponentsReader.cs:13,16`）。

**课程表解析的三个易错点**：
1. 星期用 `DateTime.Now.DayOfWeek`（`:1776`）而不是 `NowVirtual`——跨午夜 + 时间偏移时可能取错日（凌晨 00:0x 开屏场景）。
2. 课程定位/进度用 `NowVirtual`（`:1702,:1865`），而显示的「上课时间」是课表原始时刻；`FormatBellTime` 在 `TimeOffsetSeconds != 0` 时额外展示「实际打铃 = 槽时间 - 偏移」（`:1879-1887`）。三者语义要对齐阅读，否则会误判为时间错乱。
3. `LoadTodaySchedule` 失败只 `Debug.WriteLine`（`:1819-1822`），界面上表现为「课程信息空白」，无用户提示。

### 4.3 时间基准

`NowVirtual => _exactTimeService.Value?.GetCurrentLocalDateTime() ?? DateTime.Now`（`ViewModels/FullScreenClockViewModel.cs:164-171`）。用途：课程定位（`:1702`）、进度（`:1865`）、保护期（`:304-306`）、地震发震时刻比较。**时钟显示本身用真实 `DateTime.Now`**（`:1689-1691`）——即「屏幕上的秒数」与「课程进度/打铃」可能相差 `TimeOffsetSeconds`，这是有意设计。

### 4.4 计数与保护期

`OnNoiseEventRaised` 在音频线程触发 → Post 后 `_counter.OnEvent(e.Level, IsInProtection())`（`ViewModels/FullScreenClockViewModel.cs:294-302`）。`IsInProtection = SkipFirst3Min && (NowVirtual - _classStartTime) < SkipFirstMinutes*60`（`:304-306`）。保护期内计数不增加，界面文案「上课初期保护中（m:ss 后开始记录）」（`:2041-2048`）。`_classStartTime` 在 `OnClass` 时用 `LookupActualClassStart()` 按课表反查（`:1939-1952,1985`），查不到退回 `NowVirtual`。

`NoiseCounter`（`NoiseDetection/NoiseCounter.cs:18-53`）：`SetBreak(bool)`、`StartNewClass()` 清零、`OnEvent(level, isProtected)` 在 `_inBreak || isProtected` 时返回 `None`，否则 `NoisyCount++`/`NormalCount++`。

---

## 5. 地震预警

### 5.1 数据来源

「地震预警」插件（`denglihong.EarthquakeWarning`）是 CI 的 `NotificationProvider`，实时状态放在其设置对象 `EarthquakeNotificationSettings` 里。`Services/EarthquakeReader.cs:26-39` 用 `INotificationHostService` + 反射拿到该对象：

- 目标识别：`ProviderGuid == "B27C0AF3-C917-44DE-A61D-8010C3F3FB92"`（`:29`）或类型名 `EarthquakeNotificationProvider`（`:32,150-151`）；
- 设置对象：先 `Settings` 公开属性，退回 `SettingsInternal` 非公开（`:155`）；
- 计算器：`EarthquakeWarning.Calculators.HuaniaEarthQuakeCalculator`，从设置对象所在程序集 `GetType()`（`:35,88`）。

**全部反射、不引用对方程序集**（注释 `:18`），这是「没装/改名就安静降级」的代价与收益。

### 5.2 解析与兜底

`Read(now)`（`Services/EarthquakeReader.cs:62-114`）：
1. 取 `EarthquakeInfo` 的 `PlaceName/Magnitude/ShockTime/Depth/Latitude/Longitude` 与设置的 `Latitude/Longitude/Threshold`（`:69-82`）；
2. 本地坐标未配（≈0）→ 返回 `(null, eventKey)`（`:85`）——用户不填坐标永远不弹；
3. 距离：计算器 `GetDistance`，失败用 Haversine（`:89-90`，兜底 `:209-221`）；
4. 深度缺省 10 km（`:93`，与对方实现一致）；
5. 烈度：4 参数 `GetIntensity(mag, dist, depth, quakeLon)` → 2 参数 → 本地经验公式 `mag*1.363+2.941-ln(dist+7)*1.494`（`:98-100`，兜底 `:224-228`）；
6. 倒计时：`GetCountDownSeconds(depth, dist)`，失败按横波 3.5 km/s（`:102-103`，兜底 `:231-232`）；
7. 交给 `EarthquakeAlertBuilder.Build(place, mag, shockTime, intensity, threshold, countdown, now)`（`:105-106`），只有「够门槛且横波未到」才非 null；
8. 任何异常 → `default`（`:109-113`）。

`EventKeyOf`：优先 `E2Id`/`EventId`，否则 `"{PlaceName}|{ShockTime}"`（`:120-131`），用于区分「换了一场地震」和「这一拍没读到」。

### 5.3 展示

`ViewModels/FullScreenClockViewModel.cs:835-883`（`UpdateEarthquakeAlert`），由 `RefreshAll` 每秒调用（`:1712`）：
- 开关关 → `ClearEarthquakeAlert`（`:838-841`）；
- 有 Alert → 记录 `_earthquakeEventKey` 与 `_earthquakeEndTime = now + SecondsLeft`（`:848-849`）；若与上一帧 `==` 直接 return（`:850`，记录是 record，值相等）；
- 无 Alert 但正在显示 → EventKey 变了才清（`:858-862`），否则继续按 `_earthquakeEndTime` 本地倒数，`<1 s` 收（`:865-870`），秒数变化时 `WithSecondsLeft`（`:872-873`）；
- 颜色按烈度取 `SemanticColors.EarthquakeHex(Intensity)`（`:819`），字号随 `ClockFontSize` 夹取（`:812,815`）。

### 5.4 可靠性与成本

- 每秒一次 `FindProviderSettings()`：反射 `INotificationHostService.NotificationProviders`（internal）+ 遍历所有 provider + 每个 `Read` 约 10-15 次 `GetProperty`（`Services/EarthquakeReader.cs:134-159,69-82`）。没有缓存，provider 列表变化时自动跟上（这是不缓存的理由），代价是每秒几十次反射。
- `FindOverload` 要求**返回值精确为 `double`**（`Helpers/StaticOverloadFinder.cs:24-25`）；对方若改成 `float`/`decimal`，静默走本地兜底公式（可能与对方判定不一致，表现为「对方弹了、大屏不弹」）。
- `ShockTime` 解析用 `AssumeLocal`（`Models/EarthquakeAlertBuilder` 内），依赖对方写本地时间字符串。
- 历史事故：`GetIntensity` 增加重载后 `GetMethod(name)` 抛 `AmbiguousMatchException`，被外层 catch 吞成「没有地震」，导致大屏永不弹且自己的测试预警也不弹；修复方式是按参数个数选重载（`Services/EarthquakeReader.cs:161-168` 注释、`Helpers/StaticOverloadFinder.cs:5-11`）。**这是本类「吞异常」设计代价的实证。**

---

## 6. 托盘菜单与自动全屏触发

### 6.1 `AutoTriggerService`（`Services/AutoTriggerService.cs`）

- `BackgroundService`：`ExecuteAsync` 延迟 1 s（`:43`）后取 `ILessonsService`，取不到就 `return`（`:47-59`，即插件在无课程服务的环境里退化为纯手动）；
- 订阅 `OnClass`、`OnAfterSchool`、`CurrentTimeStateChanged`（`:62-64`），先立即 `DoCheck()`（`:67`），然后 `Task.Delay(Timeout.Infinite)` 挂起（`:71`），`finally` 退订（`:79-81`）；
- `DoCheck()`（`:95-116`）**只处理 `TimeState.OnClass`**：目标课程 → `Show()`；非目标且可见 → `Hide()`；`Breaking` 不动作（连堂保持显示）；`AfterSchool` → `Hide()`（`:90-93`）；
- `Show()`/`Hide()` 自身幂等（VM 内 `Dispatcher.Post`，`:1454-1540,1653-1668`），事件重复触发安全；
- 触发条件最终落到 `PluginSettings.IsTargetCourse(subjectName)`（`Models/PluginSettings.cs:678-679`）→ `CourseMatch.IsMatch`，**精确匹配**（忽略首尾空白与大小写），历史上前缀匹配导致 issue #1（`Models/CourseMatch.cs:3-25`）。

行为边界（阅读/测试时容易踩）：
- 若在目标课程中途改设置（增删目标课程），`DoCheck` 不会因设置变化重跑（只挂课程事件），要等下一次状态变更；
- 用户手动关闭大屏后，同一节目标课不会自动重开（要等下一节 `OnClass`）——注释把这条写成「天然幂等」（`Services/AutoTriggerService.cs:18`）；
- `DoCheck` 里 `CurrentTimeStateChanged` 与 `OnClass` 都触发检查，同一时刻可能连续两次 `Show()`，靠 VM 幂等兜住。

### 6.2 `TrayMenuService`（`Services/TrayMenuService.cs`）

不用 `ITaskBarIconService.MoreOptionsMenuItems`（CI 只在构建菜单时读一次，插件此时加入不生效），而是直接改 `MainTaskBarIcon.Menu`（真正的右键菜单），并挂 `NativeMenu.NeedsUpdate`（Avalonia 每次弹菜单前触发）幂等补注册（类注释 `:14-22`，实现 `:66-104`）：

- `StartAsync` 立即返回，后台 `Task.Run` 延迟 2 s 再 `Dispatcher.UIThread.Post(EnsureRegistered)`（`:41-58`）；
- `EnsureRegistered`：`menu.NeedsUpdate += (_,_) => EnsureRegistered()` 只挂一次（`:75-79`）；`ShowTrayMenuEntry` 关闭则移除项（`:82-87`）；否则创建 `NativeMenuItem{ Header="进入大屏时钟", Command=RelayCommand(Show) }` 并 `menu.Items.Add`（`:89-98`）；
- `StopAsync` 空实现（`:60`），`_menuItem` 与事件订阅在插件卸载时不清（宿主通常整个退出，影响有限）。
- 失效场景：若 CI 重建 **`MainTaskBarIcon` 实例本身**（而非复用同一 `Menu` 对象），`_hooked` 仍为 true，新菜单不会再被钩住，入口消失直到插件重启。需实测 CI 2.0 是否重建。

---

## 7. 背景预加载缓存

目的（`Services/BackdropPreloadService.cs:13-23`）：目标课程开始前 2 分钟把 4K 背景的「解码 + 模糊 + 两份像素快照」在后台做完，避开开屏瞬间的几百兆峰值（学校机器有 1.40 GiB 内存看门狗）。

- `Lead = 2 min`、`Poll = 20 s`（`:27,30`）；
- 循环（`:64-71`）：`TickAsync()`，异常 `Debug.WriteLine` 后继续；
- `TickAsync`（`:84-143`）：
  1. 缓存超过 10 分钟没人取 → `Clear()`（`:87`）；
  2. 大屏可见 → 直接返回（`:90`）；
  3. `Dispatcher.UIThread.InvokeAsync` 取 `(GetNextTargetCourseStart(), VirtualNow, MainScreenPixelSize())`（`:93-94`）——课表解析可能在 UI 线程触发 `LoadTodaySchedule`（`ViewModels/FullScreenClockViewModel.cs:1732-1749`），一天一次可接受；
  4. 只烘 `0 < (start - now) <= 2 min` 的窗口（`:98-99`）；
  5. `BackgroundEnabled` 且有图才烘（`:102-105`）；`wantBlur` 与 `BackdropLayer.WantBlurNow()` 对齐（`:110-111`）；
  6. `IsReadyFor` 命中就跳过（`:113`）；`TryBeginBuild()` 抢占（`:114`）；
  7. `Task.Run(BackdropImageSet.Build)`（`:119`）；空结果 → `Dispose + EndBuild + log`（`:121-128`）；成功 → `Publish`（`:130`）+ **强制一次全代 GC**（`:132-134`，`blocking:true, compacting:false`，在线程池线程上）；
  8. `catch (OperationCanceledException)` 才 `EndBuild`（`:139-142`）。
- 设置变化（背景开关/图片/填充/模糊/玻璃档）→ `Clear()` 作废，等下一轮再烘（`:41-51,55-57`）；
- 停机 `finally` 退订 + `Clear()`（`:77-81`）；
- 日志 `preload.log` 仅在 `EnableNoiseDebugLog` 打开时写，超过 512 KB 自删（`:149-165`）。

缓存本体 `Models/BackdropPreloadCache.cs`：静态、`Gate` 锁、单槽位（`:10-15`），`TryBeginBuild/EndBuild`（`:18-32`）、`Publish`（旧份在锁外 `Dispose`，`:35-46`）、`IsStale`（`:52-55`）、`IsReadyFor`（`:58-62`）、`TryTake`（取走即摘，`:65-76`）、`ReleaseIfMatches`（调用方自烘成功后释放多余份，`:81-93`）、`Clear`（`:96-105`）。

消费端 `Views/BackdropLayer.cs:1230-1294`：`TryTake` 命中就 `Adopt`（`:1249-1253`），否则现场 `Task.Run` 构建；自烘成功后 `ReleaseIfMatches` 把后台那份放掉（`:1289`）。`Adopt` 在 `_inRenderPass` 时只 `MarkGlassDirty` 不 `InvalidateVisual`，避免渲染中重入（`:1297-1312`）。

**设计要点**：缓存只有一份、谁取走谁负责释放、参数不匹配就留着（`TryTake` 不摘），这样「课前烘了但用户改了图」不会误用旧图，只是多占一份内存直到 `IsStale` 或 `Clear`。

---

## 8. 纯函数模型（服务层的可测核心）

| 模型 | 职责 | 关键行 |
|---|---|---|
| `ClassStartSchedule.NextTargetStart(slots, isTarget, now)` | 今天「还没开始」的最早一节目标课；`slot.Start <= now` 的跳过（正在上的目标课不算「下一节」） | `Models/ClassStartSchedule.cs:19-32` |
| `CourseMatch.IsMatch` | 精确匹配 + 忽略大小写/空白（issue #1 的修复） | `Models/CourseMatch.cs:14-26` |
| `ProgressCalculator.Calc(now,start,end)` | `clamp((now-start)/(end-start)*100, 0, 100)`，`total<=0` 返回 0 | `Models/ProgressCalculator.cs:15-20` |
| `CiComponentsReader.FormatRemaining` | `Ceiling((overTime-now).TotalDays)`；`>1 还有N天 / 1 还有1天 / 0 就是今天 / 负数 已过N天`；`OverTime` 无效才用 `DaysLeft` | `Services/CiComponentsReader.cs:49-69` |
| `NoiseLevelDisplay` | 五档槽名 / 进度条映射（各占 20%，不越档）/ Band 线性夹取 | `NoiseDetection/NoiseLevelDisplay.cs:16-65` |
| `ReminderData` / `AlertInfo` | 提醒面板数据契约；`AlertInfo.FontSize` 默认 15，供摄像头条目单独调 | `Models/ReminderData.cs:4-57` |

`FormatRemaining` 的边界：`overTime` 已过但不足 1 天时 `Ceiling` 得到 0 → 显示「就是今天」（而非「已过」）；「已过 N 天」只在超过 1 整天后出现（`:54-67`）。

---

## 9. 问题清单

标注：**P1** 可导致功能失效/资源异常；**P2** 明显缺陷或脆弱点；**P3** 代码卫生。

### P1

1. **看门狗重启与 `StopMonitoring` 竞态，可导致麦克风常开且自愈失效**
   `Services/DecibelMeterService.cs:191-208` 在检查通过后 `Post` 一个「停+启」lambda，lambda 内**不复查 `_shouldMonitor`**；`StopMonitoring()`（`:217-223`）若在此期间执行，排队动作仍会把设备重新打开并置 `_isMonitoring=true`。之后 `StartMonitoring()` 在 `:122` 因 `_isMonitoring` 早退、不再 `StartWatchdog()`，看门狗永久失效。
   建议：lambda 内 `if (!_shouldMonitor) return;`；或改用 `Task`/令牌化重启。

2. **「安静阈值」可调但不改变分级结果**
   `NoiseDetection/NoiseEventDetector.cs:50` 的 `QuietThreshold` 只被赋值（`Services/DecibelMeterService.cs:282`）从不读取；`Classify`（`NoiseEventDetector.cs:196-228`）只用 Good/Normal/Noisy。设置页却给了它独立滑块与文本框（`Settings/SettingsPage.axaml.cs:922-970`），唯一效果是改变 Good 滑块的左端点与 clamp 下界（`:922,1004`）；它还会被写进日志表头（`Services/DecibelMeterService.cs:387`）造成「已生效」的错觉。用户调「安静」阈值来改变显示/记录分界是无效的。

### P2

3. **采集固定用「第 0 台设备」而非系统默认设备**
   `Services/DecibelMeterService.cs:151-165` 以 `DeviceNumber = idx` 从 0 枚举，注释称「默认麦克风（设备 0）」（`:118,:143`）。NAudio/WinMM 语义中 `WAVE_MAPPER(-1)` 才是系统默认；设备 0 通常是枚举第一台。用户把 USB 耳麦设为默认、但板载声卡仍是 0 号时，会录到错误设备。（**需实测**：部分机器上 0 号恰为默认。）

4. **噪声事件只有在「安静 5 秒」后才结算**
   `NoiseDetection/NoiseEventDetector.cs:159-177`；持续吵闹整节课不结算，`StartNewClass()`（`ViewModels/FullScreenClockViewModel.cs:1984`）清空未结算段。这会让「吵闹次数」显著低于直觉值，属设计取舍但缺少说明文档。

5. **摄像头进程名查询产生短命进程句柄**
   `Services/CameraActivityService.cs:209-213` `Process.GetProcessById(...).ProcessName` 未 `using`/`Dispose`；句柄要等 GC 终结器才释放。MF 活动回调按设备活动频繁触发，长期运行会造成句柄抖动/短期堆积。

6. **摄像头 COM 生命周期不完整**
   `Services/CameraActivityService.cs:256-266` `Dispose` 只把 `_monitor`/`_callback` 置 null 再 `MFShutdown`，未 `Marshal.ReleaseComObject`/`Release`；`EnumerateDevices` 里 `Marshal.GetObjectForIUnknown`（`:128`）创建的 RCW 在 `finally` 中只 `Marshal.Release(ptr)` 了裸指针（`:134-142`），RCW 自己那份引用靠 GC 终结器回收。MF 要求 `MFShutdown` 前释放全部对象，反复开关/枚举可能累积引用或与关闭顺序冲突。

7. **天气读取每次刷新读 3 遍 Settings.json，且在 UI 线程**
   `ViewModels/FullScreenClockViewModel.cs:1304`、`:1330`、`:1331` 各自 `File.ReadAllText + JsonDocument.Parse`（`Services/CiWeatherReader.cs:66,139`），触发链是 1 s 定时器 Post 过来的 UI 线程（`:1674,1717-1721`）。CI 的 Settings.json 可达数百 KB，60 s 一次 ×3 次全量解析。

8. **单字段类型不符会让整份天气/预警消失**
   `Services/CiWeatherReader.cs:71-77,84-92` 用 `GetString()`，若 CI 某字段写成数字会抛 `InvalidOperationException`，被外层 `catch`（`:97-100`）吞成「无天气、无预警」。

9. **地震读取每秒一次完整反射链、无缓存**
   `ViewModels/FullScreenClockViewModel.cs:1712` → `Services/EarthquakeReader.cs:134-159`（`NotificationProviders` 反射遍历）+ `:69-82`（十余次 `GetProperty`）。可选优化：缓存 provider/settings 引用，按类型/实例变化失效。

10. **地震重载选择要求返回值精确为 `double`**
    `Helpers/StaticOverloadFinder.cs:24-25`；对方若改返回 `float`，静默走本地经验公式，与对方判定不一致（表现为「对方弹、大屏不弹」），且无日志。

11. **预加载异常未兜底会让 `_building` 永久为 true**
    `Services/BackdropPreloadService.cs:117-142` 只处理 `OperationCanceledException`；若 `Build` 抛出其它异常，离开 `TickAsync` 时 `EndBuild` 未调用，`BackdropPreloadCache._building` 卡住（`Models/BackdropPreloadCache.cs:18-26,29-32`），后续所有轮询在 `:114` 直接 return，预烘功能到重启为止全失效。（`Build` 内部是否可能抛非取消异常需看 `Models/BackdropImageSet.Build`。）

12. **`AutoTriggerService` 双实例注册**
    `Plugin.cs:69` + `:72`；hosted 实例才有事件订阅，单例实例是空壳（构造函数副作用只是再写一次 `Plugin.ServiceProvider`，`Services/AutoTriggerService.cs:37`）。易误导后续维护者。

13. **`CiComponentsReader.GetProp<T>` 直接强转**
    `Services/CiComponentsReader.cs:71-77` 中 `(T)v` 在 CI 改字段类型时抛 `InvalidCastException`；调用侧 `ViewModels/FullScreenClockViewModel.cs:1254-1274` 有 `catch{}` 兜底，但会丢掉同一次所有组件数据。

### P3

14. `Services/DecibelMeterService.cs:377-380` `WriteLogHeader()` 死代码（实际用 `WriteLogHeaderLocked`）。
15. `Services/DecibelMeterService.cs:320` 音频路径整体 `catch { }`，检测器/日志异常完全不可见。
16. `_isMonitoring`/`_lastDataReceived`/`_shouldMonitor` 无 `volatile`（`Services/DecibelMeterService.cs:29-42`），跨线程可见性靠实现细节；32 位下 `DateTime` 可能撕裂。
17. `Services/EarthquakeReader.cs:1-24` 两个连续 `<summary>`（XML 文档重复，第一个 `:10-20` 悬空）。
18. `Services/TrayMenuService.cs:60` `StopAsync` 空实现；`:77` `NeedsUpdate` 订阅不可解，`_hooked` 只在首次菜单实例上挂（见 §6.2 失效场景）。
19. `Services/BackdropPreloadService.cs:134` 在线程池线程做 blocking 全代 GC；虽在课前 2 分钟，仍会短暂卡住线程池。
20. `Services/CiWeatherReader.cs:226-242` 大小写不敏感查找是逐属性线性扫描，每个字段一次全枚举（文件解析成本之上再乘字段数）。

---

## 10. 代码之外的前提与未验证点

- **CI 版本耦合**：`apiVersion: 2.0.0.0`（`manifest.yml:10`）。反射依赖的 CI 内部：`INotificationHostService.NotificationProviders`（internal）、`CountDownComponentSettings`/`TextComponentSettings` 类型名、`Settings.json` 的 `SelectedProfile`/`TimeOffsetSeconds`/`LastWeatherInfo` 字段名。CI 升级改名 → 静默降级，无任何提示。
- **需要实测的结论**：① 设备 0 是否恒为默认麦克风（§9-P2-3）；② `MFShutdown` 顺序是否在真机上报错（§9-P2-6）；③ CI 2.0 是否重建 `MainTaskBarIcon`（§6.2 失效场景）；④ `BackdropImageSet.Build` 是否会抛非取消异常（§9-P2-11）。
- **未覆盖**：`Models/BackdropImageSet`（视觉/渲染层，属其他分析员范围）、`Views/` 渲染、`Helpers/BackdropImageStore` 细节。
