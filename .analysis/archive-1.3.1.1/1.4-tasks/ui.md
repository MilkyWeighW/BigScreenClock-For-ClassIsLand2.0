# 任务 ui：设置页 / 预览按 40% 渲染

必读：`1.4-实现契约.md`、`.analysis/07-设置页改版.md`、`.analysis/10-设置页V2与包体瘦身.md`、`.analysis/19-开屏时间线与全屏守卫.md`，以及 `Settings/SettingsPage.axaml`、`.axaml.cs`。

## 目标（清单 1/10/13 + 验收点）
1. **预览 40%**：右栏预览改成「按最终输出分辨率的 40% 渲染」：
   - `PreviewWidth/PreviewHeight` = 屏幕逻辑尺寸 × `PluginSettings.PreviewRenderScale`（0.4）；
   - 新增 `PreviewSourceWidth/PreviewSourceHeight`（屏幕逻辑尺寸本身）；
   - XAML：`Panel Width={Binding PreviewWidth} Height={Binding PreviewHeight}` 里放 `ClockSurface Width={Binding PreviewSourceWidth} Height={Binding PreviewSourceHeight}` + `LayoutTransform` = `ScaleTransform(0.4,0.4)`（用 `LayoutTransformControl` 或 `LayoutTransform` 属性），外面保留 `Viewbox`；`IsPreview=True`、`IsHitTestVisible=False` 不变。
   - 确认预览与最终输出**解耦**：预览的缩放/降采样不得写回设置、不得改大屏窗口尺寸；`RefreshForPreview` 逻辑保持。
   - 预览只在页面可见时刷新（现有 200ms 防抖保留），Unloaded 后停。
2. **默认隐藏三项**：`date/weather/countdownMerge` 已在模型层默认关；在组件页加一句说明（「这三项与 ClassIsland 主界面重合，默认隐藏，可在此重新打开」），并保证「添加组件」能把它们加回来（现有逻辑应该已支持，验证并补文案）。
3. **接新面板**：在合适位置插入 `<settings:BackgroundExtrasPanel/>`（背景页签）、`<settings:ComponentStylePanel/>` 与 `<settings:StickerPanel/>`（组件页签，选中相应组件时显示）。它们由 bg/components 两个任务提供，签名见契约 §3：无参构造、自己取 `Plugin.Settings`。你只负责摆放与命名空间，不实现内部逻辑。若编译时它们还不存在，先按契约写，最后等 Lead 集成。
4. **主题色 UI**：外观页新增「主题色跟随 ClassIsland」开关（绑 `FollowClassIslandAccent`）与当前强调色预览（`ThemeAccentColor`），并显示回退说明；不实现读取逻辑（platform 负责）。
5. **简化与清理**：删掉设置页里已经无效的控件/事件（例如与「位置滑块」有关的残留、重复说明文字）；保证 `SettingsPage.axaml` 的 `x:Name` 与 `.axaml.cs` 一致；UI 文案统一中文、精简。

## 交付件
改动文件 + 回报（新增绑定名、放在哪个页签、编译注意点、需要 Lead 检查的 XAML 片段）。

## 禁止
改 `Models/*`、`Views/*`、`Settings/SettingsPage.Background.cs`、`SettingsPage.Components.cs`、`SettingsPage.Appearance.cs`、csproj。`SettingsPage.Appearance.cs` 归 platform？—— 不，它归你，但如果和 platform 的 VM 属性冲突，以属性名为准并回报。
