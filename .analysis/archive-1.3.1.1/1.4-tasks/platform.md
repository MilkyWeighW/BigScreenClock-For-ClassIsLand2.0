# 任务 platform：CI 主题色 / CI 偏移时间 / 异步 10ms 存储 / 清理与动画性能

必读：`1.4-实现契约.md`、`.analysis/00-总览.md` §5.1/§8、`.analysis/06-改造说明.md`、`.analysis/15-课程身份与壁纸缓存.md`。

## 目标（清单 2/7/11/13/6）
1. **异步 10ms 存储**：新增 `Services/SettingsStore.cs`：
   - `LoadAsync(path)` / `SaveAsync(path, settings)`：`await Task.Delay(10).ConfigureAwait(false)` 后在线程池序列化/写盘（`ConfigureFileHelper` 或 `JsonSerializer`），不阻塞 UI；
   - 写入合并：连续多次 `SaveAsync` 只保留最后一次（`SemaphoreSlim`/版本号），失败 `Debug.WriteLine` 不抛；
   - `Plugin.SaveSettings()` 保持同步签名但内部转异步；保留 400ms 防抖；启动时的首次加载仍允许同步（宿主初始化必须立即拿到 `Settings`，在注释里写清楚）。
   - 读路径：凡是插件自己重读配置/天气/课表的地方（如果在你 scope 内）改走异步；不在你 scope 的只回报。
2. **主题色跟随 CI**：新增 `Helpers/CiThemeReader.TryReadAccentColor()`（反射 `ClassIsland` 的主题服务/资源，失败 null），改 `Helpers/ClockThemeApplier.cs`：`FollowClassIslandAccent && CI 有值` 时把强调色写进 `PluginSettings.ThemeAccentColor` 并参与配色（单向，绝不回写 CI）；CI 不可用/无值 → 回退主题自带色。订阅 CI 主题变化（能拿到事件就订阅，拿不到就在设置页打开/大屏显示时读一次），换主题要刷新大屏与预览。
3. **CI 偏移时间**：新增 `Services/CiTimeService`：`DateTime Now` 优先 `IExactTimeService.GetCurrentLocalDateTime()`，异常/拿不到回退 `DateTime.Now`；每次调用有轻量缓存（同一帧不重复反射）。`FullScreenClockViewModel` 的**显示时间**改用 `CiTimeService.Now`（课程/打铃仍用 `NowVirtual`）；`ShowBellTime` 逻辑不变。保证 `TimeOffsetSeconds` 为 0 时行为与之前完全一致。
4. **清理**：`AutoTriggerService` 重复注册（`AddSingleton` + `AddHostedService`）改成只注册 hosted 实例并让 DI 能解析；`FullScreenClockWindow` 的 `_hideCursorCts` 泄漏——**不改窗口文件**，只把 `Cursor` 逻辑需要的改动回报给 Lead；摄像头 COM `ReleaseComObject`/`ProcessName.Dispose`；麦克风设备选择按 WinMM `-1`（若改动风险大就只修注释并回报）。
5. **动画性能**：ViewModel 里每秒 new `SolidColorBrush`/集合替换/重复读盘的热点缓存掉；`RefreshForPreview` 不做全量重建；通知只在值真变化时发（`SetProperty` 帮助方法）。别动 `Views/*`（Lead 负责）和 `Settings/*`。

## 交付件
代码 + 回报（文件清单、public API、主题色实际取到的字段/回退路径、时间源改动点、清理项、已知风险）。

## 禁止
改 `Models/*`、`Views/*`、`Settings/*`、csproj、build.ps1。
