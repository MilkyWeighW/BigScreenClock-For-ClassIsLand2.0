# 任务 components：组件渲染 / 文字材质 / 多贴纸

必读：`1.4-实现契约.md`、`.analysis/08-组件化与页签改版.md`、`.analysis/09-组件开关与玻璃总开关.md`、`.analysis/18-组件透明度与自定义图片.md`，以及 `Views/ClockSurface.axaml(.cs)`、`Views/ComponentHost.cs`。

## 目标（清单 8/9/12/6）
1. **组件长宽比 + 字体**：`ComponentHost` 应用 `StretchX/StretchY`（与 `Scale` 相乘的 `MatrixTransform`，注意现有左上/中心原点语义别破坏默认外观）与 `FontFamily/FontSize/FontWeight`。字体要真的作用到组件里的文字（遍历 `TextBlock` 设值并在 0/空 时恢复默认；用 `TextElement` 附加属性也可以，但必须验证继承生效）。默认值下与 1.3.1.1 逐像素一致。
2. **文字材质 overlay**：新增 `Views/TextMaterialLayer.cs`，由 `ComponentHost` 按 `TextMaterial` 挂载。要求：
   - **只覆盖文字外框**（对每个 `TextBlock` 取 `Bounds`/`FormattedText` 外框，圆角薄片贴合文字，不铺满组件）；
   - Frosted：半透明底 + 细描边 + 轻微模糊（用 `Avalonia.Media` 的 `BlurEffect` 要控制半径/不每帧重算）；
   - LiquidGlass：在磨砂基础上加边缘高光/色散感（渐变描边 + 2~3 层薄片，别写成全屏逐像素）；
   - 标注清楚「原生路径」：优先用合成层（`RenderOptions`/`BitmapInterpolationMode` + 透明层叠），DWM 不可用时退回 D2D/Skia 静态磨砂；代码里写清楚选择逻辑。
   - 性能：材质层随组件移动用 RenderTransform，不在 `Render` 里 new 画刷；无材质时零开销（不创建任何控件）。
3. **多贴纸**：`ClockSurface` 里自定义图片从单一 `ImageHost` 改成按 `Plugin.Settings.ImageInstances` 生成多个 `ComponentHost`（id=`instance.ComponentId`），每个实例一张图、独立布局；`ImageInstances` 增删要动态同步（`CollectionChanged`）。无图实例隐藏且不占位；换图/删除实例要 `Dispose` 旧位图（同图不重复解码）。
4. **预览支持**：新增 DP `SurfaceDesignWidth`/`SurfaceDesignHeight`（默认 1920×1080），供设置页预览算 `LayoutTransform`，不要在 `IsPreview` 下改变最终输出行为。
5. **设置页组件页**：`SettingsPage.Components.cs` 负责组件条的实例列表（静态组件 + 贴纸实例），选中项联动；新增两个自包含 UserControl：
   - `Settings/ComponentStylePanel.axaml(.cs)`：长宽比、字体、文字材质（绑定当前选中组件的 `ComponentLayout`）；
   - `Settings/StickerPanel.axaml(.cs)`：贴纸列表（添加/删除/选图/填充方式），调用 `ComponentImageStore` 存副本。
   两个面板都无参构造、从 `Plugin.Settings` 取数据、暴露 `SelectComponent(string id)` 之类的最小 API 给 `SettingsPage.Components` 调用。
6. **动画性能**：`ClockSurface` 的每帧工作减少（静态文本/画刷缓存、别每次都重建 `ColumnDefinitions`、`InvalidateVisual` 限定范围）；开屏/退场继续走现有时间线即可。

## 交付件
代码 + 上述两个 UserControl + 回报（文件清单、public API、需要 Lead/ui 接线的点、贴纸内存策略、已知风险）。

## 禁止
改 `Models/*`、`SettingsPage.axaml`、`BackgroundLayer*`、`FullScreenClockWindow*`、csproj；新增 NuGet。
