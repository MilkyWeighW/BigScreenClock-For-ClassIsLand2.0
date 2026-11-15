# 任务 bg：背景管线 / WebView2 / 原生实时模糊 / 壁纸内存

必读：`1.4-实现契约.md`、`.analysis/11-背景系统改造.md`、`.analysis/19-开屏时间线与全屏守卫.md`，以及你要改的文件本身。

## 目标（对应用户清单 3/4/5/6）
1. **壁纸背景内存**：`WallpaperService` / `WallpaperCacheService` / `ScreenCaptureService` 里所有 `Bitmap`/`RenderTargetBitmap`/`WriteableBitmap`/中间纹理都要有明确归属：换图、退出、窗口隐藏时释放；缓存加**容量上限 + LRU + WeakReference**，不能在长时运行里只增不减；解码按 1.25× 像素预算、绝不放大；模糊中间图用完即弃，`WallpaperBlurRadius` 上限内按目标尺寸缩放，别对 4K 原图直接 StackBlur。目标：反复切壁纸/开关大屏 100 次，托管内存回落、无 GDI/位图句柄泄漏。
2. **背景与组件分开渲染**：`BackgroundLayer` 只管背景（自己一条绘制路径、不做每帧整层重绘）；组件层由 `ClockSurface` 负责（不是你的文件）。**WebPage 模式**用独立的下层窗口显示网页，需要 `NeedsTransparentWindow=true`。
3. **WebView2 背景**：新增 `WebBackgroundService` + `WebBackgroundWindow` + `WebViewHost`（真 HWND 承载 `CoreWebView2`）+ `WebView2Availability`。
   - 不可用（运行时没装/初始化失败）时：`IsAvailable=false`、`UnavailableReason` 有中文原因，服务静默不显示，绝不抛异常影响大屏。
   - 可编辑地址（`BackgroundWebUrl`），导航失败有回退（保持空白/纯色即可，不弹框）。
   - 窗口必须是 **非 topmost 的下层全屏无边框窗口**，跟着大屏窗口的位置/尺寸/可见性走；大屏隐藏/退出时一起隐藏。
   - 注意：`WebView2` 的 `CoreWebView2Controller`/环境初始化是异步的，所有 `async void` 都要 try/catch；COM 对象在 `Detach` 时释放。
4. **原生实时模糊**：`NativeBlur.TryEnable(hwnd, true, tint)` 用 `SetWindowCompositionAttribute`（`ACCENT_ENABLE_ACRYLICBLURBEHIND` / `BLURBEHIND`），失败返回 false 并让 `BackgroundLayer` 退回纯色，不崩。`BackgroundLayer.NeedsTransparentWindow` 在 NativeBlur/WebPage 时为 true。
5. **动画性能**：开屏时间线/背景淡入改成「时间线在 UI 线程只改 opacity/transform」，不要每帧重建画刷、不要每帧 `InvalidateVisual` 整层；`Apply()` 里可缓存的东西缓存起来（`SolidColorBrush`、`Pen`、`FormattedText`），`Stopwatch`/`DispatcherTimer` 在隐藏时停掉。
6. 设置页「背景」逻辑（`SettingsPage.Background.cs`）：新的 `BackgroundKind` 四/五档（壁纸/纯色/自定义图片/实时模糊/网页）切换、导入图片仍走原逻辑、网页地址保存；不可用时置灰由 `BackgroundExtrasPanel` 体现，你也要在切换钩子里拦截。

## 交付件
- 上面 write scope 内的代码；
- `Settings/BackgroundExtrasPanel.axaml(.cs)`：无参 UserControl，自己取 `Plugin.Settings`，含「网页地址 / 打开说明 / WebView2 状态」；不可用时所有输入禁用并显示 `UnavailableReason`。
- 回报：新增/改动文件、public API、需要 Lead 在窗口里接的线（透明、`NativeBlur`、`WebBackgroundService.Attach`）、内存管理策略、已知风险。

## 禁止
改 `Models/*`、`FullScreenClockWindow*`、`ClockSurface*`、`SettingsPage.axaml`、csproj；新增 NuGet。
