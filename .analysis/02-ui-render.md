# 02 · UI / 渲染层分析（Views/）

分析对象：`Views/` 全部 10 个文件（BackdropLayer.cs 61 KB 为全项目第二/三大文件），并交叉引用 `Helpers/LiquidGlassBaker.cs`、`Helpers/OffsetSampler.cs`、`Models/BackdropImageSet.cs`、`Models/BackgroundImageMap.cs`、`Models/BackdropPreloadCache.cs`、`Models/GlassMode.cs`、`ViewModels/FullScreenClockViewModel.cs` 的被调用部分。所有行号以 read 工具显示的行号为准。

结论先行：这一层是整个插件技术含量最高的部分。它用「一张预烘的清晰底图 + 一张预烘的模糊副本 + 每块玻璃一张按块烘的折射小位图」三级缓存，在 Avalonia 11 **没有自定义着色器**的约束下，用纯 CPU 逐像素实现了折射、色散、镜边高光，并把「玻璃里外必须严丝合缝」做成结构性保证（同一份 `BackgroundImageMap`）而不是调参凑出来的。作者在注释里留下的失败史（整屏 8 MB 位图把进程顶到 CI 1.40 GiB 内存看门狗、渲染循环内 `InvalidateVisual` 被判插件严重错误并自动禁用）说明这些设计是被线上事故反复打磨出来的。

---

## 1. 视觉树与布局结构

### 1.1 顶层：Window → ClockSurface

```text
FullScreenClockWindow (Window, WindowState=FullScreen, SystemDecorations=None, CanResize=False,
                       ShowInTaskbar=True, Background={Binding BackgroundColor})
└─ ClockSurface (UserControl)                      Views/FullScreenClockWindow.axaml:14
   └─ Grid (RowDefinitions="*,Auto")               Views/ClockSurface.axaml:20-25
      ├─ BackdropLayer  Grid.Row=0 RowSpan=2       Views/ClockSurface.axaml:30
      ├─ Grid  Grid.Row=0  Margin=40,30,40,18      Views/ClockSurface.axaml:33-34
      │  ├─ Row0  顶部行 (Auto)                    :42
      │  ├─ Row1  BodyArea（时钟 + 音量条叠放）    :105
      │  ├─ Row2  计数/课程信息/按钮 (Auto)        :189
      │  ├─ ReminderPanel  Grid.Row=1 RowSpan=2    :311
      │  └─ RulesPopup (Popup 浮层)                :365
      ├─ ProgressBar  Grid.Row=1 底部进度带 (Auto) Views/ClockSurface.axaml:380-391
      └─ EarthquakeAlertLayer Grid.Row=0 RowSpan=2 View/ClockSurface.axaml:401-432
```

关键结构性决定：

- **ClockSurface 是独立 UserControl 而不是窗口内联内容**（`Views/ClockSurface.axaml:17-19`）。设置页把**同一份**布局塞进 `Viewbox` 等比缩小当预览（`Settings/SettingsPage.axaml:699-715`：`<views:ClockSurface IsPreview="True" IsHitTestVisible="False" BackdropCornerRadius="12" DataContext="{Binding PreviewVm}"/>`），因此字号、颜色、位置不可能两处对不上；`IsPreview` 只负责「长得一样」，不写回设置（`Views/ClockSurface.axaml.cs:21-22`、`:458`）。
- **背景层是整整一层 Control，不是给每个组件套 Border**（`Views/BackdropLayer.cs:31-44`）。玻璃完全不参与布局，组件的 Margin / 实测尺寸一个字节都没动，所以 `ApplyBodyPositions()` 那套靠实测高度算位置的逻辑、以及往设置里写回百分比的限位逻辑都不受影响（`Views/BackdropLayer.cs:41-43`）。
- 背景层 `Grid.RowSpan=2` 跨到底部进度带之下，但底部进度带是**语义色带，不登记玻璃**（`Views/ClockSurface.axaml.cs:175`）。

### 1.2 顶部行（Row0）：提醒合并 + 预警弹幕 + 日期

`Grid ColumnDefinitions="Auto,*,Auto"`（`Views/ClockSurface.axaml:42`）：

- 左列一个 `StackPanel Orientation=Horizontal Spacing=36`（`:45`），内含四块，各自 `IsVisible` 由 VM 的合并结果控制：`WeatherBlock`（`:47`）、`RainBlock`（`:58`）、`CountdownBlock`（`:69`）、`ReminderTextBlock`（`:80`）。合并规则写在注释里（`:36-41`）：天气固定主位并与弹幕同行；与天气合并的优先级 降雨 > 倒计时 > 文本；总宽 ≤ 屏幕 1/3 才并入（阈值 `MergeThreshold = Bounds.Width/3`，`Views/ClockSurface.axaml.cs:354`）；无天气则倒计时顶位，再无则文本框；天气预警始终单开面板。
- `Spacing=36` 这个值不是审美：`GlassTarget.InsetX` 默认 12（`Views/BackdropLayer.cs:22`），玻璃左右各外扩 12，相邻两块玻璃之间的净距 = 36 − 2×12 = 12；注释明确要求两处常量必须一致，否则两块玻璃贴成一条（`Views/ClockSurface.axaml:43-44`）。
- 中列 `AlertDetailScroll`（`ScrollViewer`，`HorizontalScrollBarVisibility=Hidden`，`:85-92`）绑定 `ExpandedAlertDetail`，`SizeChanged="DetailScroll_SizeChanged"` 驱动弹幕（见 §3.4）。
- 右列 `DateBlock`（`:93-98`）：日期 22px `Chrome.TextMuted`，副标题 11px；日期文本上挂 `PointerPressed="DateText_PointerPressed"`，是隐藏入口（连点 7 次弹「关于」，见 §3.5）。

### 1.3 主体区（Row1）：时钟 + 音量条同一个格子里叠放

`Grid Grid.Row=1 x:Name="BodyArea"`（`Views/ClockSurface.axaml:105`），两个子元素都 `HorizontalAlignment=Center VerticalAlignment=Top`，**用 Margin.Top 上下摆**，而不是用行/列：

- `ClockText`（`:107-113`）：`FontSize={Binding ClockFontSize}`、`Foreground={Binding FontColor}`、`FontFamily={Binding ClockFontFamily}`、`FontFeatures={Binding ClockFontFeatures}`。`ClockFontFamily` 来自 CI 主界面字体（`ViewModels/FullScreenClockViewModel.cs:466`，`Models/CiMainFont.cs:9`）；`ClockFontFeatures` 是 `tnum` 表格数字（`ViewModels/FullScreenClockViewModel.cs:352-354`），秒数跳动时整行不左右抖——这是大屏时钟最容易被忽略但最影响观感的一处细节。
- `VolumePanel`（`:116-182`）：`Width="{Binding #ClockText.Bounds.Width, FallbackValue=640}"`，即音量条宽度**跟随时间字体宽度**（`:115-116`）。内部三段：
  1. 标题行 `Grid ColumnDefinitions="Auto,*,Auto"`（`:123-146`）：左「教室音量」+ `Megaphone` 图标；中 `RecordingText`（`IsVisible={Binding RecordingInMeterTitle}`）；右 `NoiseLevelText` 档位文字。注释说明「正在记录」放在这一行而不是另起一行，是为了出现/消失不改这块玻璃的尺寸、一次都不用重烘（`:119-122`）。
  2. 轨道 + 刻度 `Grid ColumnDefinitions="1*,3,1*,3,1*,3,1*,3,1*" Height=16`（`:148`）：`ProgressBar` 跨 9 列、`Height=5`，内联 `ControlTemplate` 只留 `PART_Track` / `PART_Indicator` 两个 `Border`（`:153-161`）——砍掉 Avalonia 默认模板里的圆角与动画，保证是硬边分段仪表盘；四个 `Rectangle Width=3 Height=13 RadiusX=1.5` 用 `Chrome.Divider` 立在轨道上（`:162-165`）。
  3. 五档文字 `ItemsControl ItemsSource={Binding NoiseLevelSlots}` + `UniformGrid Columns=5`（`:168-181`），每项 `Text` / `TextBrush` / `TextOpacity` / `TextFontSize` 由 VM 统一按 `AnimatedProgress` 反推（`ViewModels/FullScreenClockViewModel.cs:482-483` 的 `CurrentSlot => LevelSlotCalculator.SlotFromProgress(AnimatedProgress)`），保证条和字同源。
- 位置换算：设置里存的是「该块中心落在主体区高度的百分之几」，`TopFor(percent, areaHeight, height) = percent/100*areaHeight - height/2`（`Views/ClockSurface.axaml.cs:493-494`），逆运算 `PercentOf`（`:497-498`）。限位算法见 §3.3。

### 1.4 底行（Row2）：计数 / 课程信息 / 按钮同行

`Grid ColumnDefinitions="*,Auto,*"`（`Views/ClockSurface.axaml:189`）。左右都是星列，两边平分剩余，中间 `Auto` 才落在屏幕正中（注释 `:185-188` 明确说明「否则会被更宽的左边计数推歪」）。

- 计数区 `Border x:Name="CounterAreaBorder"`（`:192-196`）`ClipToBounds=True`、`TextElement.FontSize={Binding CountFontSize}`，`PointerEntered/Exited` 控制记录规则 Popup。
- 内层 `DockPanel x:Name="CounterStrip" HorizontalAlignment="Left"`（`:202`）：`RecordingText` 先写且 `DockPanel.Dock="Right"`（`:205-209`），`Viewbox CounterContent` 最后写（填剩余空间）。注释解释了为什么必须是 DockPanel + 这个顺序（`:197-204`）：横向 `StackPanel` 量孩子时给无限宽，`Viewbox` 永远量不出「放不下」，长句会被右边裁掉；`DockPanel` 先把右侧「正在记录」的宽度扣掉，剩下的有限宽交给 `Viewbox`，才触发「只缩不放大」。
- 课程信息 `Viewbox CourseInfoBox`（`:250-266`）：`Stretch=Uniform StretchDirection=DownOnly`，`MaxWidth={Binding CourseInfoMaxWidth}`，超长等比缩字而不是裁掉。
- 按钮区 `StackPanel ButtonBar`（`:268-304`）：`MinimizeButton` / `ExitButton`，`Click` 分别调 VM 的 `Minimize()` / `ExitFullScreen()`（`Views/ClockSurface.axaml.cs:754-762`），`:pointerover` 换 `Chrome.ButtonHover`（`:281-285`、`:298-302`）。可读性不靠实底片而靠 code-behind 加的 `DropShadowEffect` 同色柔光（见 §3.2）。
- 三个预览占位控件（`PreviewRecordingPlaceholder` `:137-142`、`PreviewCounterPlaceholder` `:214-220`、`PreviewCoursePlaceholder` `:261-264`）默认 `IsVisible=False`，只在 `IsPreview && 真实内容为空` 时显示（`Views/ClockSurface.axaml.cs:251-260`），用来让「字号」类设置在没上课时也调得动；且刻意不往 VM 塞假数据，避免污染真实大屏（`:248-250`）。

### 1.5 提醒面板（Row1 起跨两行）与记录规则 Popup

- `ReminderPanel`（`Views/ClockSurface.axaml:311-315`）：`HorizontalAlignment=Left VerticalAlignment=Top`、`MaxWidth={Binding ReminderPanelMaxWidth}`（= 屏宽 2/3，`Views/ClockSurface.axaml.cs:353`）、`MaxHeight=400`、`Margin="0,16,0,0"`。这个 16 的左边距是给玻璃留的：上面第一行与这块各垫玻璃、各外探 6px，原本严丝合缝贴着就会叠出 12px 错位边（注释 `:307-310`）。内部 `ScrollViewer` + `StackPanel Spacing=8`（`:316-317`），依次是未并入第一行的降雨（`:319-326`）/倒计时（`:328-335`）/文本（`:337-338`）/预警 `ItemsControl`（`:340-358`）。
- `RulesPopup`（`:365-376`）：`Placement=Top`、`VerticalOffset=-8`、`IsOpen={Binding ShowCountRules}`、`IsLightDismissEnabled=False`、`MaxWidth=720`。用 `Popup` 是为了不占布局——规则出现/消失不会让计数区上下位移、避免鼠标反复脱离引起的排版抖动（注释 `:363-364`）。锚点由 code-behind 设：`RulesPopup.PlacementTarget = CounterAreaBorder`（`Views/ClockSurface.axaml.cs:53`）。

### 1.6 地震提醒覆盖层

`EarthquakeAlertLayer`（`Views/ClockSurface.axaml:401-432`）：跨两行、`IsHitTestVisible=False`、`IsVisible={Binding ShowEarthquakeAlert}`。三层：

1. `QuakeVeil`（`:404`）兜底：没有背景图、也就没有模糊图时用近乎不透明底色（暗主题 `#E8 08 08 0C` / 亮主题 `#E8 FFFFFF`，`Views/ClockSurface.axaml.cs:334-336`）把别的都盖掉。
2. `QuakeSheet`（`views:FrostedCardBackdrop Radius="0"`，`:406`）整屏磨砂：有模糊图时它盖掉 QuakeVeil，整块屏幕变成一张磨砂玻璃；压色按主题（`#3D FFFFFF` / `#59 000000`，`Views/ClockSurface.axaml.cs:330-332`）。**关键：磨砂用背景层早烘好的那张模糊图，不对整屏做实时模糊**——4K 上后者每次重绘几十毫秒，而这张图是现成的，只多画一遍（注释 `:397-398`）。
3. `EarthquakeAlertBlock`（`:407-431`）：`CornerRadius=14`、`BorderThickness=2`、语义色 `EarthquakeAlertBackground/Border`，内部 `FrostedCardBackdrop EqFrost Radius=12 Tint={Binding EarthquakeAlertBackground}`（`:414-415`）再用同一张模糊图裁一块，卡片里外磨砂连续；文字两层（横波倒计时 + 震中/震级）包在 `Viewbox StretchDirection=DownOnly MaxWidth={Binding EarthquakeAlertMaxWidth}` 里（`:416-429`），字号随时钟字号缩放、再长等比缩，保证整屏塞得下。

---

## 2. 玻璃 / 真折射效果的实现机制

### 2.1 四种档位与分流点

`GlassMode`（`Models/GlassMode.cs:7-22`）：`Off=0` / `Translucent=1` / `Frosted=2` / `Liquid=3`。设置里存 int，`PluginSettings.GlassMode` 钳到 0~3（`Models/PluginSettings.cs:256-259`）。

分流的完整拓扑：

| 关注点 | Off | Translucent | Frosted | Liquid |
|---|---|---|---|---|
| 要不要烘模糊底图 `WantBlurNow()` | 仅当 `stretch==Fit` | 同左 | 恒要 | 恒要 |
| `RenderCore` 画不画玻璃 | 直接 return（`Views/BackdropLayer.cs:531`） | 渐变底 + 1px 描边 + 阴影（`:575`） | 再叠「模糊背景裁块」（`:565-566`） | 先逐块落折射位图（`:546-555`），后只补描边+阴影（`:572-573`） |
| 要不要 CPU 烘 `LiquidGlassBaker` | 不 | 不 | 不 | 要（`TryStartBake` 早退条件 `Views/BackdropLayer.cs:838`） |
| 每块玻璃的 `NeedsBake` | false | false | false | 按矩形/圆角/倍率/`_contentVersion` 判（`:712-717`） |

`RenderCore` 的三段式（`Views/BackdropLayer.cs:503-577`）顺序是硬约束：底色 → 清晰图（`Fit` 档另加一层「铺满 + 模糊」的虚化填充，`:511-519`）→ 明暗遮罩 veil（`:526-529`）→ 玻璃。**明暗遮罩必须在玻璃之前整层压一遍，且玻璃里还要再压一遍**（`DrawBlurredBackdrop` 里 `:636`），否则玻璃里外差一档亮度——注释点明这是曾经的 bug（`:610-616`）。

### 2.2 底图与模糊副本：`BackdropImageSet`

`Models/BackdropImageSet.cs` 是「一张背景图烘出来的一整包」：`Sharp` 位图 + `Blur` 位图 + 两份 `uint[]` 像素快照（`SharpPixels` / `BlurPixels`）。要点：

- **先读文件头再解码**：`ImageHeader.ReadSize(path)` 拿原图尺寸，`BackgroundImageMap.DecodeSize` 算目标解码宽，`Bitmap.DecodeToWidth(stream, decode.Width, HighQuality)`，避免为一张 100000×10 的截图按整屏宽解出天量像素。
- **顺手倒成连续数组**：位图每次读都要 Lock，而折射是 CPU 逐像素采样，所以在烘图时拷成 `uint[]`，之后重算玻璃完全不碰位图（`Views/BackdropLayer.cs:64-66`）。
- **模糊是纯 CPU 的 `StackBlur`**，不是 `RenderTargetBitmap` 全屏糊：后者后端相关、后台线程渲可能抛异常，一抛整块背景图就没了（`Models/BackdropImageSet.cs:147-151`）。
- **模糊半径要按「图像像素/视口像素」换算**：`BlurRadiusInImagePixels = Clamp(radius * ratio, 0, StackBlur.MaxRadius)`，`ratio = imageSize.Width / dest.Width`（按实际落位算，不能用「图宽÷视口宽」）。旧写法只用宽度比，极端长条图会算出几十上百倍半径，`StackBlur` 查找表按半径平方涨，一张图吃上 GB，直接顶爆 CI 的内存看门狗 1.40 GiB（`Models/BackgroundImageMap.cs:106` 及其注释、`Models/BackdropImageSet.cs:133`）。
- 解码尺寸有 `PixelBudgetFactor = 1.25` 预算与「绝不放大」原则（`Models/BackgroundImageMap.cs:31`、`:125`）。

### 2.3 同一套映射：玻璃里外严丝合缝的结构性保证

这是整个效果的「唯一致命伤」防线（`Models/BackgroundImageMap.cs:20-23` 原话）。所有落位/采样都必须走同一对纯函数：

- `ComputeDestRect(PixelSize imageSize, Size viewportSize, int stretch)`：`FillCrop`（默认，UniformToFill，取 `Math.Max`）、`Fit`（Uniform，`Math.Min`）、`Stretch`（各取各的）。
- `MapToImage(Rect viewportRect, ...)`：先 `ComputeDestRect`，再按 `kx = imageSize.Width/dest.Width` 把视口矩形映回图像像素矩形；`Fit` 档留白处会映到图外坐标，采样时按边缘钳制。

清晰图的绘制（`Views/BackdropLayer.cs:521-523`）、磨砂玻璃里透出的那块（`DrawBlurredBackdrop` 里 `:624`）、内容层卡片自己裁的磨砂（`DrawFrostedCard` 里 `:266`）、折射采样（`BuildBackdrop` 里 `:1007-1020`，`SharpDest`/`BlurDest`/`FillDest` 全部由 `ComputeDestRect` 现算）——五处同一个函数、同一份 `_stretch`，所以里外必然对齐，不靠调参（`Views/BackdropLayer.cs:33-39`）。

### 2.4 采样器：`LayerBackdrop` / `OffsetSampler`

`IBackdropSampler`（定义在 `Helpers/LiquidGlassBaker.cs:11-22`）只有两个方法：`Sample(x,y)`（清晰图）与 `SampleBlurred(x,y)`（模糊图），返回直线空间 0..1 非预乘的 `ColorSample`。

`BackdropLayer.LayerBackdrop`（`Views/BackdropLayer.cs:1113-1171`）把「清晰图 / 留白处的模糊填充 / 纯主题底色 / 明暗遮罩」四件事收进一个采样器：

1. `Fetch` 先把坐标钳进层范围（贴边折射会把采样坐标推出层外，钳回来等效于拉长边缘像素，不会带黑边，`:1143-1145`）；
2. 按 `dest` 矩形判是否落在图内，落在图外退化到 `Fill`（`Fit` 档的虚化补边，`Fill` 与 `Blur` **共用同一份像素数组**，少拷一次 8 MB，`:1015-1017`）；
3. 再退化到 `ThemeBackground` 纯色；
4. 最后按 `VeilAlpha` 线性插值压暗/提亮（`:1159-1167`）。**明暗遮罩因此是烘进折射图里的**，所以 `Apply` 的 `restyle` 判定必须包含 `_dim`（`:192-199`，注释说明以前漏了这一项导致拖明暗滑块时折射档里外差一档亮度）。

`SampleBitmap`（`:1174-1207`）是手写双线性：`fx = (x-dest.X)*(size.Width/dest.Width) - 0.5`，四个相邻像素按 `(1-tx)(1-ty)…` 加权；`Red/Green/Blue` 读取时**必须反预乘**（`Unpremultiply(channel, alpha)`，`:1210-1215`），否则半透明像素会整体偏暗。

`OffsetSampler`（`Helpers/OffsetSampler.cs:18-34`）只是给内层采样器整体加一个偏移：玻璃改成「一块一张小位图」后，烘一块时画布是这块自己的外接框，而背景图按**层**坐标铺，中间差的正是 `(Px, Py)`。偏移必须是**整数设备像素**，小数部分交给 `panelX = 矩形X×倍率 − Px` 由烘焙那边处理；两处都算对，折射图案才与整屏一张烘出来的逐像素一致（作者说这块由 `LiquidGlassTests` 的等价性用例钉着）。坐标钳制一概不在这里做，否则会变成「按块的边界拉长边缘像素」，玻璃边上出现假接缝。

### 2.5 烘焙：`TryStartBake` → `BakeJob` → `LiquidGlassBaker.RenderPanel`

`TryStartBake`（`Views/BackdropLayer.cs:831-991`）的守卫链，顺序本身是 bug 史：

```
_glassBusy / !_glassDirty → return          (:833)
120ms 防抖定时器还在跑 → return             (:835)
_adjusting（正按着位置/字号滑块）→ return   (:837)
_glassMode != Liquid → return               (:838)
Bounds < 2 → return                          (:841)
SyncPanels()                                 (:847)  ← 补算 NeedsBake
pending = _panels where NeedsBake            (:849-851)
pending 空 → _glassDirty = false             (:853)
层像素 > 40,000,000 → 放弃折射               (:858-865)
BuildBackdrop + BuildStyle（UI 线程取值）    (:868-870)
每块算整数设备像素外接框（px 下取整、右/下上取整，"包住"）
                                             (:877-880)
Task.Run 逐块 RenderPanel                    (:907-930)
Dispatcher.UIThread.Post 提交 + 校验 stale   (:932-989)
```

`:847` 那次「多余」的 `SyncPanels()` 是明确的历史修复：`NeedsBake` 靠 `_contentVersion` 判定，而 `_contentVersion` 会因为一堆**不动布局**的东西变（玻璃档位、透明度、折射强度、雾面、背景明暗），不在这里补一次就会「一块都不该烘」→ 标脏被清掉 → 玻璃一直不重画（注释 `:843-846` 记录了「切回液态档时直接不出图」这个现象）。

`BakeJob`（`:1062-1082`）是「参数在 UI 线程取好、后台只读」的不可变快照：`Panel`、`Rect`、`Radius`、`Version`、`Scale`、`Px/Py/Width/Height`、`PanelX/PanelY/PanelWidth/PanelHeight`（带小数）、`RadiusDevice`、复用的 `Canvas`。

- `Px/Py` 取整方向必须是「包住」（`px=floor(rect.X*scale)`、`pw=ceil(rect.Right*scale)-px`，`:877-880`），缓冲才装得下烘焙里的那个小数偏移。
- `PanelX = panel.Rect.X*scale - px` 是玻璃左上角在缓冲里**带小数**的位置；传 0 会让玻璃形状画在缓冲原点，与采样坐标差一个亚像素，折射图案就和矢量描边错开（`:893-896`）。
- 缓冲复用：`Canvas = panel.Canvas is { } c && c.Length == pw*ph ? c : null`（`:900`），后台烘前 `Array.Clear(canvas,0,...)`（`:913-915`）——不擦的话上一次那几块玻璃的像素会留在原地。
- 单块异常只吞掉自己（`:926-929`），不拖垮整屏；提交时若该块算崩，`panel.NeedsBake = false` 防止每轮布局白算（`:954-959`）。
- **stale 校验**（`:944-952`）：算的这段时间里块又动了 / 设置又变了（`!SameRect` / `Radius` / `Version` / `Scale` 任一对不上）→ 这一趟结果作废、`stale=true`，提交末尾 `_glassDirty = true` 并再 `TryStartBake`。比的是「烘的那份」对「现在这份」，所以拖一圈又拖回原位时不会白烘（`SyncPanels` 里 `:709-717` 同理，比对 `BakedRect` 而不是上次同步值）。
- 提交阶段在 **UI 线程**：位图也在 UI 线程创建/上锁（`new WriteableBitmap(... Bgra8888, Premul)`、`bitmap.Lock()` + `CopyInto`，`:961-973`），尺寸没变就一直复用同一张位图（`:963-965`）。`CopyInto` 用 `unsafe` 逐行 `Buffer.MemoryCopy`（`:1314-1325`），按 `frame.RowBytes` 处理行 padding。

`LiquidGlassBaker.RenderPanel`（`Helpers/LiquidGlassBaker.cs:107-283`）逐像素算法（参数由 `BuildStyle` 给出，`:1085-1107`）：

```
RefractionHeight = 22*scale      RefractionAmount = 48*(refraction/100)*scale
Dispersion = 0.04                HighlightWidth = 2.0*scale     HighlightFalloff = 5
HighlightStrength = 0.5          LightAngle = 128°              BevelLight = 0.35
TintAlpha = (glassOpacity/100)*0.8                             Frost = frost/100
RimFrost = 0.35
```

每像素：

1. `sd = RoundedRectSdf.RoundedRect(...)`；`sd > 0.5` 直接跳过（只写玻璃覆盖的像素，矩形外不动，调用方先清透明）；`coverage = 1 - SmoothStep(-0.5, 0.5, sd)` 做一像素抗锯齿。
2. 梯度 `RoundedRectSdf.Gradient(...)` 取归一化法线；`depth = -sd`；`bandHeight = clamp(RefractionHeight, 0, min(halfW,halfH)*0.9)`（折射带不能比玻璃宽）；`band = 1 - depth/bandHeight`；`push = CircleMap(band) * amount`；采样坐标 `(sx + nx*push, sy + ny*push)` → 贴边画面被推弯。
3. **色散**：`push > 0.01 && dispK > 0.001` 时 R 用 `(1+dispK)`、G 用原、B 用 `(1-dispK)` 各采样一次（三次双线性）。
4. **磨砂**：`frost += (1-frost)*band*RimFrost`，再与 `SampleBlurred` 线性插值——磨砂浓度在边缘更强（`RimFrost`）。
5. **材料色**：`k = tintAlpha * (1 - 0.33*clamp(localY/panelHeight, 0, 1))`，上厚下薄，与另两档的竖向渐变视觉一致（`Views/BackdropLayer.cs:753-764`）。
6. **斜面受光**：`k *= 1 + BevelLight*band*max(0, n·L)`。
7. **镜边高光**：对 `i ∈ {-1,0,1}` 取 `sampleSd = sd - i*sigma`（`sigma = max(strokeHalf*0.5, 0.35)`，即 3 抽头高斯近似），落在 `|sampleSd| < strokeHalf` 记 1，`mask = mask/sideWeight*0.5`（只留边界内侧一半，避免描边两侧对称发光），`lit = |n·L|^HighlightFalloff`，再加 `mask*lit*HighlightStrength`。
8. `alpha = coverage * clamp(a,0,1)`，打包成预乘 BGRA：`bb | (gb<<8) | (rb<<16) | (ab<<24)`。

为什么是 CPU：**Avalonia 11 没有暴露自定义着色器**（`DrawingContext` 只有 `PushClip`/`PushOpacity`，没有 `PushEffect`），而玻璃只在离散时刻重算、不是每帧（`Helpers/LiquidGlassBaker.cs:91-94`）；且这个算法有分支、三次采样、SDF，写成 shader 也未必更快。

### 2.6 磨砂档的两条绘制路径

- **玻璃层里（Frosted）**：`DrawBlurredBackdrop`（`Views/BackdropLayer.cs:617-638`）——`PushClip(圆角)` → 填主题底色 → `MapToImage` 算出这块该从模糊图的哪一区域裁 → `visible = src ∩ 图像范围`（`Fit` 档玻璃盖到留白处时那片就该是纯底色）→ `DrawImage(blur, visible, dest)` 按比例映射 → 再压一遍 veil。三层顺序与玻璃外一致（注释 `:610-616`）。
- **内容层卡片上（地震卡片）**：`FrostedCardBackdrop` 自己画（`Views/FrostedCardBackdrop.cs:83-94`），因为玻璃层画在所有内容**下面**，居中卡片若登记成 `GlassTargets`，磨砂会跑到时钟下面、时钟的字反而浮在磨砂上（`:10-13`）。它通过 `Layer.DrawFrostedCard(...)`（`Views/BackdropLayer.cs:252-281`）从同一张模糊图按同一套映射裁一块，坐标系换成卡片自己的局部坐标，所以卡片里外磨砂连续。响应链：`Layer` 变 → `HookLayer` 订阅 `BackdropChanged`（`:70-79`）→ 背景换图/改明暗/换留白底色 → `InvalidateVisual()`（`:81`）。

### 2.7 失效 / 重绘策略（脏区）

三层标脏，粒度从粗到细：

1. **整张底图失效**（`_needsRebuild`）：只有「换图 / 模糊半径变 / 从烘变不烘」才触发（`Views/BackdropLayer.cs:185-190`），由 `ArrangeOverride`（`:345-356`）、`Render`（`:474`）、`OnLayoutUpdated`（`:442`）三处调用 `TryStartRebuild`。判定带容差：`SamePixelBudget` 用 `Near(a,b) = |a-b| <= max(8, a/50)`（`:389-395`）——以前是精确相等，差一个像素就整张 4K 重烘（几百兆临时内存），课前预烘按屏幕估的尺寸取整差一两像素就会白干（注释 `:379-388`）。
2. **光学参数失效**（`_contentVersion++`）：`SetPalette` 只在「亮/暗翻转」或留白底色变了才 `++`（`:128-147`，否则设置页每刷新一次配色就整屏重烘 12 块）；`Apply` 的 `restyle` 命中时 `++`（`:194-215`）；`Adopt` 换上新底图时 `++`（`:1305`）。
3. **单块失效**（`GlassPanel.NeedsBake`）：`SyncPanels()`（`:684-734`）每轮布局跑一次，只做记账、一个像素都不算。它按 `GlassTargets` 顺序重建 `_panels`：新增块建出来、挪过的块更新 `Rect`、消失的块连位图一起 `Dispose`（`:722-728`）。`NeedsBake` 六项判定（`:712-717`）：位图为空 / `Rect` 变 / `Radius` 变 / `BakedVersion != _contentVersion` / `BakedScale` 变 / 圆角变；比较对象是「烘的时候那份」`BakedRect` 等，所以拖回原位自动免烘。

触发入口：

- `ClockSurface` 在 12 个玻璃目标上挂 `PropertyChanged`，只认 `BoundsProperty` / `IsVisibleProperty` 两种（`Views/ClockSurface.axaml.cs:218-222`）——Avalonia 是保留式渲染，兄弟控件动了不会叫醒背景层（注释 `Views/BackdropLayer.cs:439`）。
- 设置变化走 `OnPluginSettingsChanged` 白名单（`Views/ClockSurface.axaml.cs:288-301`）。
- 主题/背景色变化走 `OnClockViewModelPropertyChanged`（`:224-232`）。
- `InvalidateGlass()` → `MarkGlassDirty()` + `InvalidateVisual()`（`Views/BackdropLayer.cs:284-290`）。这里**必须**走 120ms 防抖（`:804-817`）：以前直接标脏，「标脏 → 下一帧 Render 里立刻重烘」，拖滑块等于每帧烘一张整屏图。
- `BackdropLayer` 自己在 `OnAttachedToVisualTree` 挂 `LayoutUpdated`（`:292-299`），在 `OnLayoutUpdated` 里 `SyncPanels` + `MarkGlassDirty` + `TryStartBake`（`:440-456`）。

防抖与抑制的两档（`_adjusting` / `_suppressed`）：设置页正按着「位置 / 字号」滑块期间（`PluginSettings.AdjustingLayout`）——只按住不拖：玻璃原样留着、一个字节不重算；一拖动：`_suppressed = true`，玻璃整层收走、改画 2px 范围框（`DrawGlassOutlines`，`:600-608`），因为旧玻璃已是按旧位置烘的、留着就是错位拖影；松手：`_suppressed = false`，只重烘矩形真变过的那几块（`:111-177`）。同时 `RenderOptions.SetBitmapInterpolationMode` 在拖动中降到 `LowQuality`、松手换回 `HighQuality`（`:159-168`）——设置页预览是 `Viewbox` 里装着整屏那么大的一面，高质量缩放会让 Skia 每帧给整屏位图重算 mipmap。

### 2.8 渲染管线时序（一次「玻璃要更新」的完整路径）

```text
UI 线程                                   后台线程
────────────────────────────────────────────────────────────────
某控件 Bounds/IsVisible 变
  → OnGlassTargetPropertyChanged
  → Backdrop.InvalidateGlass()
      _glassDirty = true; 启动 120ms DispatcherTimer
                                    …120ms…
  → OnGlassTimerTick → TryStartBake
      SyncPanels()（更新 NeedsBake / 建块 / 释放退场块）
      BuildBackdrop + BuildStyle（快照）
      每块算 px/py/pw/ph、BakeJob
      _glassDirty = false; _glassBusy = true
      Task.Run ───────────────────────────► 逐块 Array.Clear
                                            LiquidGlassBaker.RenderPanel
                                            （OffsetSampler → LayerBackdrop
                                             → SampleBitmap 双线性反预乘）
                                          ◄── Dispatcher.UIThread.Post
      _glassBusy = false
      逐块 stale 校验 → WriteableBitmap
      Lock + CopyInto（unsafe 行拷贝）
      写回 BakedRect/BakedVersion/NeedsBake=false
      InvalidateVisual()
      if (_glassDirty) TryStartBake()
  → Render(context)
      SyncPixelBudget；_needsRebuild 则 TryStartRebuild
      _panels 为空则 SyncPanels
      _glassDirty 则 TryStartBake（重入保护：_inRenderPass）
      RenderCore：底 → 清晰图 → veil → 逐块 DrawPanel / 磨砂 / 描边
```

两条必须遵守的「不在渲染循环里标脏」约束：

- `NotifyBackdropChanged` 里若 `_inRenderPass` 则 `Dispatcher.UIThread.Post` 延后一帧（`Views/BackdropLayer.cs:234-243`）——课前预烘命中缓存那条路径会在 `Render` 里 `Adopt`，此时通知订阅者标脏会撞上 `InvalidOperationException: Visual was invalidated during the render pass`，CI 判插件严重错误并禁用插件（`:238-240`）。
- `Adopt` 里 `if (!_inRenderPass) InvalidateVisual()`（`:1308-1310`），注释记录了 1.1.1.9 在学校上课点名那一刻报错、重启后因 `CorruptPluginsDisabledLastSession` 被自动禁用的事故（`:493-500`）。

落位精度：`DrawPanel`（`:584-594`）用 `PushTransform(Matrix.CreateTranslation(panel.Px/scale, panel.Py/scale))` 平移整数设备像素、再按 1:1 画；换算回设备像素正好是整数 `px`，不会落在半像素上被 Skia 重新采样（那样整块会发虚）。

---

## 3. ClockSurface 代码后置的职责与事件流

`Views/ClockSurface.axaml.cs`（775 行）分六块职责。

### 3.1 尺寸相关布局与位置钳制

- 构造里挂 `SizeChanged → UpdateSizeDependentLayout()`、`BodyArea/ClockText/VolumePanel.SizeChanged → ApplyBodyPositions()`、`Loaded → ApplyBodyPositions()/UpdatePreviewPlaceholders()`（`:54`、`:68-72`）。
- `UpdateSizeDependentLayout()`（`:349-359`）按实测宽算四个 VM 属性：提醒面板最大宽 = `W*2/3`、合并阈值 = `W/3`、课程信息最大宽 = `max(160, (W-80)/2)`、地震块最大宽 = `max(240, W-80-72)`；`Bounds.Width <= 0` 时直接 return（还没量出尺寸别把课程信息压成 0 宽，`:352`）。
- `ApplyBodyPositions()`（`:425-475`）是「时钟 / 音量条不许互相压」的约束求解：
  - `ClockVolumeGap = 24`（`:404`）；
  - 音量条：`volumeMinTop = clockTop + clockHeight + 24`，`volumeMaxTop = max(0, areaHeight - volumeHeight)`，取设置值后 `Min(Max(...))` 钳住（`:444-448`）——下不出主体区，这条优先守住（再往下就压到按钮行）；
  - 时钟：`clockRoomTop = volumeVisible ? volumeTop - 24 - clockHeight : areaHeight - clockHeight`，`clockMaxTop = max(0, clockRoomTop)`（`:451-453`）；音量条往上顶，时钟可落范围就收紧、被一并顶上去，不需要另记「这次拖的是哪一个」（注释 `:417-419`）；
  - `SetTop` 只在 `|Margin.Top - top| > 0.5` 时才写（`:501-505`），因为写边距还会再跑一轮 `SizeChanged`，不比较会来回抖；
  - 预览到此为止（`:458`），真实大屏才 `vm.SetPositionLimits(...)`（`:460-467`）并把钳完的实际位置写回设置（`WriteBack`，阈值 0.05 个百分点，`:478-490`）；
  - `_applyingLayout` 挡住「写回设置 → PropertyChanged → 又进来」的重入（`:407`、`:428`、`:432`、`:473`）。
- 主窗口尺寸变化时 `FullScreenClockWindow` 在**不可见/关闭**时调 `ResetPositionLimits()`（`Views/FullScreenClockWindow.axaml.cs:23-29`），否则上次地震留下的下限会一直卡着设置页滑块。

### 3.2 外观：按钮柔光、背景下发

- `ApplyButtonHalo()`（`:269-286`）：给 `MinimizeButton`/`ExitButton` 的**内容控件**各建一个 `DropShadowEffect`（`BlurRadius=4, Opacity=0.9`，深色主题黑晕、亮色主题白晕），两个控件不能共用同一 Effect 实例（`:273`）。这样按钮底片可以只留一点同色系淡彩而不是一块实心黑/白砖（`:265-268`）。
- `ApplyBackdrop()`（`:304-319`）是设置 → 背景层的唯一出口：`BackgroundEnabled` 关着时连图带玻璃一并收掉（`:310`、`:316`）；先把调色板推给 `SetPalette(vm.Chrome, 解析后的 BackgroundColor)`，再把 `GlassMode/Opacity/BlurRadius/Refraction/Frost/AdjustingLayout` 推给 `Apply`。
- `ApplyQuakeFrost()`（`:326-337`）给整屏磨砂压色、给兜底 veil 上色（见 §1.6）。
- 主题/背景色变化由 `OnClockViewModelPropertyChanged` 接（`:224-232`），换 `Chrome` 或 `BackgroundColor` 就重跑上面两个。

### 3.3 玻璃登记与信号挂接

`SetupBackdrop()`（`:164-216`）只跑一次（`_backdropBound`）：

- `EqFrost.Layer = Backdrop; QuakeSheet.Layer = Backdrop;`（`:176-177`）——地震卡片从同一张模糊图裁。
- `Backdrop.GlassTargets` 共 **12 块**（`:178-192`）：`WeatherBlock`、`RainBlock`、`CountdownBlock`、`ReminderTextBlock`、`AlertDetailScroll`、`DateBlock`（均默认 `InsetX=12/InsetY=6/Radius=12`），`ClockText`、`VolumePanel`（`InsetX=16, InsetY=8, Radius=16`），`CounterStrip`、`CourseInfoBox`、`ButtonBar`、`ReminderPanel`（默认值）。
- 刻意**不登记**的两块及原因（`:171-175`）：地震提醒块（在整屏正中，底下正好压着时钟，登记进来磨砂会跑到时钟下面、时钟的字浮在磨砂上面）、底部全宽进度带（语义色带，垫了反而糊）。
- 挂线统一走 `HookSignals()`（`:97-111`）：进视觉树挂 `Plugin.Settings.PropertyChanged` 与 VM 的 `PropertyChanged` + `NoisyDisplaySegments.CollectionChanged`，**离开视觉树全部摘掉**（`:113-125`）。注释说明原因（`:77-80`）：`Plugin.Settings` 与 VM 都是长命单例，不摘线会让它们反过来攥住整棵视觉树，设置页每开一次预览就漏一整套控件树 + 背景层位图，内存一路涨、界面一路钝。

### 3.4 预警弹幕（自维护的 16ms 动画）

这是 code-behind 里最复杂的一段，全部围绕一个 `ScrollViewer` + `TranslateTransform`：

- 悬停某条预警（`AlertPointerEntered`，`:518-530`）：设 `vm.ExpandedAlertDetail = item.Detail`，`StopMarquee` 后 `StartMarquee(once:false)`（悬停态一直循环滚）。
- 鼠标点击（`AlertPointerPressed`，`:534-563`）：若已在循环滚，则「接着当前进度滚完这一遍就收」（`_marqueeStopAfterThisPass()`），不重头播——用户 2026-09-19 反馈点头重播很出戏（注释 `:551-552`）；触屏/触控笔按下不算点击（`:543-547`），因为触屏按下也会触发 `PointerEntered`，抬手的 `PointerExited` 才按点击收尾（`:540-542`、`:567-590`）。
- `StartMarquee`（`:609-625`）：预览那份直接 return（`:612`）；`sv.Tag` 作「已启动」标记防重复；先挂一个 `TranslateTransform { X = 1e6 }` 把文字推到右侧外，避免首个 Tick 之前闪一下完整开头（`:618-622`）。
- `StartMarqueeWhenReady`（`:628-699`）：`Dispatcher.UIThread.Post` 等布局就绪，最多重试 5 次（`:649-653`）；**用 `TextBlock.DesiredSize.Width` 而不是 `sv.Extent.Width`** 算文字宽——`ScrollViewer.Extent` 有「不小于视口宽」的下限，短文字会被抬到视口宽，按它算行程会在文字离场后多空跑 `(视口宽-文字宽)/speed` 秒，摄像头那条短详情正好暴露这个空档（注释 `:634-637`）；`speed = 100 px/s`，`total = (textWidth + viewport)/speed`（`:657-658`）；`stopAfter` 默认 `+∞`（循环），点击时算成「当前这一遍结束 + 至少再滚一遍（不足 2 秒补 2 秒）」以免点完没反应（`:663-673`）；`DispatcherTimer` 16ms（`:675`）每帧算 `translate.X = viewport - (elapsed % total) * speed`（`:694`），到 `stopAfter` 就停表、清变换、清 `ExpandedAlertDetail`、放开重复点击限制（`:681-690`）。
- `StopMarquee`（`:593-600`）：停表、`RenderTransform = null`、`Tag = null`、清两个状态字段。

### 3.5 隐藏入口与退出

- 日期连点：`DateText_PointerPressed`（`:705-717`）——间隔 > 3 秒视为中断，`_dateClickCount >= 7` 时 `ShowAboutPopup()`（`:712-716`）。
- `ShowAboutPopup()`（`:721-752`）：有烘好的模糊底图（磨砂/液态档）时给**整面墙**套 `BlurEffect { Radius = clamp(GlassBlurRadius, 4, 48) }`，关于窗口浮在上面，关掉立刻撤掉 Effect 不留常驻开销（`:729-736`、`:746-750`）；窗口底色跟**背景图明暗**走而不是界面主题（`ClockThemeApplier.BackgroundIsDark(settings)`，`:741-743`，注释 `:738-740`：暗色界面压在一张很亮的壁上时，深色方片会突兀得像贴上去的）；`_aboutWindow.Show(owner)` 以全屏窗口为 Owner（`:751`）。
- `ExitButton_Click` / `MinimizeButton_Click` 只转发给 VM（`:754-762`）。

### 3.6 地震弹出动画

`EarthquakeAlertLayer.PropertyChanged` 盯 `IsVisibleProperty`（`:386-401`）：变为可见时先把 `EarthquakeAlertBlock.RenderTransform` 归零（`QuakeScale(0.04)`，纯横向缩放），再 `QuakeRevealAnimation.RunAsync`；`catch` 里兜底设 `QuakeScale(1)`，别把提醒吞了（`:392-400`）。动画 380ms `CubicEaseOut`（`:366-376`），`RenderTransformOrigin = RelativePoint.Center`（`:65`）。选择「左右绽开」而不是 CI 原生那种「往下摊开」是为了跟大屏这面墙的构图一致（注释 `:362-364`）。

---

## 4. 窗口行为

### 4.1 FullScreenClockWindow

XAML（`Views/FullScreenClockWindow.axaml`）：`WindowState="FullScreen"`、`SystemDecorations="None"`、`ShowInTaskbar="True"`、`CanResize="False"`、`Background="{Binding BackgroundColor}"`（`:6-10`）。全部内容只有一个 `ClockSurface`（`:14`），窗口只管窗口自己的事（`:12-13`）。

- **Esc / F11 退出**：`KeyDown` 里 `Key.Escape or Key.F11` → `vm.ExitFullScreen()` 并 `e.Handled = true`（`Views/FullScreenClockWindow.axaml.cs:32-39`）。`ExitFullScreen() => Hide()`（`ViewModels/FullScreenClockViewModel.cs:2127`），不是关窗——窗口实例复用。
- **躲鼠标**：`PointerMoved` 里恢复 `Cursor.Default`、取消上一个 CTS、新建 CTS，`Task.Delay(10000, token).ContinueWith(...)` 在非取消时 `Dispatcher.UIThread.Post(() => Cursor = null)`（`:41-52`）。即停止移动 10 秒后光标消失，一动就回来。
- **最小化暂停**：按钮 → `vm.Minimize()`（`Views/ClockSurface.axaml.cs:759-762`）→ `WindowState = Minimized` + 把 CI 主界面拉出来（`ShowCiMainWindow`，`ViewModels/FullScreenClockViewModel.cs:1546-1555`、`:1630-1636`）+ `ScheduleMinimizePause()`（`:1563-1585`）。暂停逻辑：设置开 `PauseWhenMinimized`（默认 true，`Models/PluginSettings.cs:599-601`）、`MinimizePauseMinutes` 默认 5（钳 1~30，`:606-609`）；到点且窗口仍是 Minimized 才 `PauseForMinimize()`——停更新定时器、停防休眠、停分贝/摄像头监控、退订课程事件、停进度动画（`:1597-1608`），**不关窗口、不动任务栏入口**。点回（`Activated` → `EnsureFullScreen()`，`:1642-1651`）或再从设置页/托盘打开时 `ResumeFromMinimizePause()`（`:1611-1624`）：重载课表、重订事件、重启各监控、`RefreshAll()`。
- **防休眠**：`SetThreadExecutionState(ES_CONTINUOUS|ES_DISPLAY_REQUIRED|ES_SYSTEM_REQUIRED)`，每 25 分钟续一次（`ViewModels/FullScreenClockViewModel.cs:2094-2124`）。
- `Closed` 时统一善后：`IsWindowVisible=false`、取消最小化暂停、`StopAntiScreensaver`、`StopUpdateTimer`、停两路监控、退订课程事件、恢复主界面显示、`_window=null`（`:1476-1488`）。所以「直接关窗」和「Hide」两条退出路径都能清干净。
- `Activated` 里 `EnsureFullScreen()` 兜底把 OS 恢复成 `Normal` 的窗口拉回全屏（`:1642-1651`）；`ActualThemeVariantChanged` 让跟随 CI 主题的大屏即时换色（`:1493`）。

### 4.2 LetterWindow（致所有用户的一封信）

用途：开发者的一封公开信 + 版本回顾 + 「1.2.0.0 起不再加新功能」的声明（`Views/LetterWindow.axaml:51-77`，署名 sokumei 2026/10/31）。打开大屏时钟设置页时弹一次、每次启动 CI 只弹一次（`Views/LetterWindow.axaml.cs:9-11`）。

- 触发：`Settings/SettingsPage.axaml.cs:81-89` —— `if (!LetterWindow.ShouldShow()) return;` 然后**先** `MarkShown()` 再在 try/catch 里 `new LetterWindow().Show(owner)`。
- 门控：`ShouldShow() => !_shownThisSession && Plugin.Settings is { LetterDismissed: false }`（`:36-37`），`_shownThisSession` 是 `static`（`:18`），`MarkShown()` 先标免得排版期间又进来一次（`:39-40`）。
- 关闭：右上 × / 底部「我知道了」/ Esc 都走 `CommitClose()`（`:27-30`、`:77`）；勾了「不再显示」就 `settings.LetterDismissed = true` 再 `Close()`（`:80-86`）。
- 视觉：680×620、无边框、透明背景、`PanelBorder` 圆角 16（`Views/LetterWindow.axaml:7-13`、`:28`）；配色在 code-behind 里跟 CI 实际主题（`ApplyTheme`，`Views/LetterWindow.axaml.cs:42-75`），并订阅 `ActualThemeVariantChanged` 即时翻。

### 4.3 AboutPopupWindow（关于）

用途：制作者、版本号（正式/测试/开发版徽标）、项目仓库与演示视频链接（`Views/AboutPopupWindow.axaml:31-57`）。460×262、`SystemDecorations=None`、`Topmost=True`、`ShowInTaskbar=False`、`TransparencyLevelHint=Transparent`、`Background=Transparent`、`CenterOwner`（`:7-15`），圆角与底色都画在 `PanelBorder` 上（`:31`）。

- 版本号**运行时从插件目录 `manifest.yml` 读**（`Views/AboutPopupWindow.axaml.cs:22-56`），不再写死；版本类型徽标规则：五位 = 开发版（`#ff9800`）、四位末位非 0 = 测试版（`#ff5555`）、末位为 0 = 正式版（`#4caf50`）（`:118-127`）。
- 链接：`Process.Start(new ProcessStartInfo(url){ UseShellExecute = true })`，`catch {}` 静默（`:129-136`）；URL 常量 `:17-20`。
- 关闭：60 秒自动关（`DispatcherTimer`，`:77-80`、`:87`）；点击窗口外（`Deactivated`）关，但加 0.5 秒宽限期避免刚显示就误关（`:82-86`）。
- 配色跟大屏主题 + 身后是否磨砂：`frosted` 时底色带透明（亮 `#CC FFFFFF` / 暗 `#C4 14141F`），否则纯实色保证字看得清（`:90-100`）；`frosted` 的值来自 `Backdrop.HasBlurredBackdrop`（`Views/ClockSurface.axaml.cs:731`，该属性见 `Views/BackdropLayer.cs:232`）。

---

## 5. 性能考量

### 5.1 位图缓存与内存

- **三级缓存**：① 底图整包 `BackdropImageSet`（清晰 + 模糊 + 两份像素快照）；② 每块玻璃一张 `WriteableBitmap`（只有这块那么大）+ 一块复用 `uint[] Canvas`（`Views/BackdropLayer.cs:1030-1059`）；③ 课前预烘缓存 `BackdropPreloadCache`（单槽、取走即摘、谁拿谁释放，`Models/BackdropPreloadCache.cs:10-106`）。
- **「按块」是主要的内存优化**：注释里写明原实现是整屏一张折射图，任何块动一点都要整屏重清重算，「正在记录」那种每秒变字数的块一动就是几十张整屏大小的垃圾（1920×1080 = 8 MB），这正是把进程顶到 CI 内存看门狗上限（1.40 GiB）、大屏闪退的元凶（`Views/BackdropLayer.cs:69-74`）。改成按块后，计数行变字只重烘它自己那块（几十 KB）。
- **像素预算**：`WantedPixelSize = size * DeviceScale`，`DeviceScale = RenderScaling × 这一层在屏幕上的缩放`（`Views/BackdropLayer.cs:403-437`）。设置页预览是 `Viewbox` 缩着放的，不把这一层缩放算进来，就是为一张几百像素宽的缩略图去解 2560 宽底图 + 烘 2560 宽折射图，白占两百来兆常驻、每换一次图再造上百兆垃圾（注释 `:410-416`）。
- **离开视觉树即释放**：`ReleaseBitmaps()` 停定时器、`_image.Dispose()`、逐块 `Dispose`、清 `_built*` 键（`Views/BackdropLayer.cs:315-343`）；后台正烘着就设 `_releasePending`，等它收工再放（`:319-324`、`:935`、`:1264-1270`）。不放的话 `DispatcherTimer` 会一直攥着这一层（连带几十 MB），设置页每开一次预览就漏一份（注释 `:308-313`）。
- **背景变化不重复烘**：`TryStartRebuild` 的回调里只按「烘图真正吃的那几项」（path / radius / stretch / wantBlur / pixels）判 stale（`:1275-1277`）——换一次壁纸设置页会连着写好几项设置，不做这个判断同一张图会被烘两遍，白造上百兆垃圾（注释 `:1271-1274`）；自己烘成功后还 `ReleaseIfMatches` 把缓存里同样的那份放掉（`:1288-1289`）。

### 5.2 重绘触发面

- 12 个目标每个只挂 `Bounds` / `IsVisible` 两种属性（`Views/ClockSurface.axaml.cs:218-222`），任何布局抖动都会经 120ms 防抖合并（`Views/BackdropLayer.cs:804-817`）；`TryStartBake` 里再挡一次「定时器还在跑」（`:834-835`），否则每次布局/渲染都直接从这儿烘一遍、防抖等于白搭。
- 拖动滑块期间**一次都不烘**（`:836-837`、`:454`），只画覆盖框；松手才开新一轮，且只烘真变过的块。
- 明暗遮罩、玻璃底色/描边/阴影、渐变全是「画的时候现算」的刷子，不烘焙（`RebuildBrushes`，`:747-796`）；`Apply` 的 `restyle` 命中才让 `_contentVersion++`。换主题时只有「亮/暗翻转」与留白底色变了才整批作废（`:128-143`）。
- 震动卡片/磨砂层：`FrostedCardBackdrop` 只在 `BackdropChanged` 时 `InvalidateVisual`（`Views/FrostedCardBackdrop.cs:81`），不订阅布局。

### 5.3 CPU / GPU 分工

| 工作 | 位置 | 线程 |
|---|---|---|
| 背景图解码（按目标物理像素） | `BackdropImageSet.Build` | `Task.Run` 后台（`Views/BackdropLayer.cs:1257`） |
| 整张图 StackBlur 模糊 | `StackBlur.Apply` | 同上后台 |
| 每块玻璃折射/色散/高光 | `LiquidGlassBaker.RenderPanel` | `Task.Run` 后台，**串行逐块**（`:907-930`） |
| 位图创建 / Lock / 像素拷贝 | `WriteableBitmap` + `CopyInto` | UI 线程（`:961-973`） |
| 背景图/清晰图/磨砂块/描边绘制 | `DrawingContext.DrawImage/DrawRectangle` | 渲染线程（`Render`） |
| 弹幕滚动（16ms） | `TranslateTransform` 动画 | UI 线程（`Views/ClockSurface.axaml.cs:675-696`） |
| 音量条平滑（33ms） | `AnimatedProgress` 指数逼近 `delta*0.5` | UI 线程（`ViewModels/FullScreenClockViewModel.cs:514-541`） |
| 时间刷新（1s） | `_updateTimer` | `System.Timers` + `Dispatcher.Post`（`:1670-1684`） |

设计取舍明确：**CPU 只做「离散时刻的重算」，GPU 做「每帧的合成」**。玻璃重算不是每帧，所以纯 CPU 可接受；一旦重算就在后台线程，UI 线程只承担位图上载与拷贝。代价是首帧/参数变更后玻璃会「迟到」一次（120ms 防抖 + 烘焙耗时），期间 `NeedsBake` 的块**先不画**（`RenderCore` 里 `if (p.NeedsBake || p.Bitmap is not { } bitmap) continue;`，`Views/BackdropLayer.cs:552`、`:559`），避免画旧位置的错位拖影。

### 5.4 课前预烘

`Services/BackdropPreloadService.cs`：目标课程开始前 **2 分钟**（`Lead = TimeSpan.FromMinutes(2)`，`:27`）把解码/模糊/像素快照在后台做完，每 20 秒轮询一次课表（`Poll`，`:30`）；`TryBeginBuild` 单飞防重复烘（`Models/BackdropPreloadCache.cs:18-26`）；缓存超过 10 分钟没人取就 `Clear`（`BackdropPreloadService.cs:87`、`Models/BackdropPreloadCache.cs:52-55`）；烘完 `Publish` 并强制 GC 一次（`:130-132`）。`BackdropLayer.TryStartRebuild` 优先 `BackdropPreloadCache.TryTake`（`Views/BackdropLayer.cs:1246-1253`），命中就同步 `Adopt`，省掉开屏那一刻的整张解码 + 模糊（4K 一张图是几百兆瞬时开销，刚好撞上 CI 内存看门狗）。

### 5.5 其它性能细节

- `IsPreview` 的时钟：不起弹幕定时器（`Views/ClockSurface.axaml.cs:611-612`）、不写回设置（`:458`）。
- `RebuildBrushes` 每次 `Apply` 都重建刷子与 `BoxShadows.Parse`（`Views/BackdropLayer.cs:747-796`）——拖动明暗滑块时每帧一次；注释认为「一次分配无所谓」（`:790`），但这是每帧分配 `BoxShadows` + 若干 `LinearGradientBrush`，是可见的小量 GC 压力。
- 摄像头/分贝/课程事件都跟着大屏显示周期开关，最小化暂停时全部停掉（`ViewModels/FullScreenClockViewModel.cs:1597-1608`）。
- `DropShadowEffect` 只加在按钮内容上、且每次主题变化重建（`Views/ClockSurface.axaml.cs:269-286`），不是每帧对象。
- 地震弹出时给整面墙套 `BlurEffect`（`Views/ClockSurface.axaml.cs:735`），只在关于窗口存续期间挂着，关掉即撤（`:746-750`）。

---

## 6. 设计亮点

1. **「同一套映射」把最脆的效果做成了结构性正确**：清晰图、磨砂裁块、卡片磨砂、折射采样四处共用 `BackgroundImageMap.ComputeDestRect/MapToImage`（`Models/BackgroundImageMap.cs:20-23`、`:40`、`:81`；调用点 `Views/BackdropLayer.cs:521`、`:624`、`:266`、`:1007-1020`）。玻璃里外对不齐是磨砂玻璃唯一的致命破绽，作者用「同一 Render、同一份映射」而不是调参解决（`Views/BackdropLayer.cs:33-39`）。
2. **玻璃层完全不参与布局**（`Views/BackdropLayer.cs:41-43`）：组件的 Margin/实测尺寸一个字节没动，定位与设置写回零影响；这是一个「加视觉特效但不碰既有布局契约」的干净切面。
3. **把整屏 8 MB 位图改成按块小位图**（`Views/BackdropLayer.cs:69-74`、`:1029-1059`）：直接把 CI 内存看门狗闪退问题从根上解决，且块级 stale 校验 + `BakedRect` 比较让「拖一圈拖回原位」自动免烘（`:709-717`）。
4. **在没有着色器的平台上用 CPU 做出折射/色散/镜边高光**（`Helpers/LiquidGlassBaker.cs:91-94`、`:107-283`），且刻意只在离散时刻重算；烘焙参数全走不可变 `BakeJob` 快照，UI 线程只取值不参与计算（`Views/BackdropLayer.cs:1061-1082`）。
5. **像素级对齐意识**：`Px/Py` 整数设备像素 + `PanelX/PanelY` 小数偏移（`:893-896`）、`DrawPanel` 整数平移 1:1 绘制（`:584-594`）、`SamePixelBudget` 容差（`:389-395`）、亚像素不进 Skia 重采样——这些是「不糊」的关键。
6. **渲染循环内不许标脏的显式防御**（`Views/BackdropLayer.cs:234-243`、`:493-501`、`:1308-1310`）：用 `_inRenderPass` 把「课前预烘命中缓存 → Adopt → 通知订阅者」这条路径的 `InvalidateVisual` 延后一帧，避免 CI 判插件严重错误 + 自动禁用。
7. **两档拖动抑制**（`_adjusting` / `_suppressed`，`Views/BackdropLayer.cs:111-177`）：只按住不拖 = 一个字节不算；一拖动 = 收走旧玻璃只画范围框；松手 = 只烘真变过的块。同时把插值模式降到 `LowQuality` 省掉 Viewbox 预览的 mipmap 重算（`:159-168`）。
8. **明暗遮罩在玻璃里重画一遍**（`Views/BackdropLayer.cs:610-616`、`:636`）：保证玻璃里外亮度一致——一个很容易漏、且漏了立刻能看出来的细节。
9. **UI 细节的工程化**：`tnum` 表格数字（`ViewModels/FullScreenClockViewModel.cs:352-354`）、音量条宽度跟随时间字体宽度（`Views/ClockSurface.axaml:116`）、`DockPanel` 顺序解决 Viewbox 无限宽量不出「放不下」（`:197-204`）、`ProgressBar` 内联模板砍掉默认圆角/动画（`:153-161`）、课程信息 `StretchDirection=DownOnly`（`:252`）、规则用 `Popup` 不占布局（`:363-369`）。
10. **预览即真身**：设置页预览放的是同一个 `ClockSurface`（`Settings/SettingsPage.axaml:699-715`），只能靠 `IsPreview` 关掉副作用（不写回设置、不起弹幕），且用三个占位控件补假内容让字号类设置可调（`Views/ClockSurface.axaml.cs:243-260`）。
11. **资源生命周期纪律**：`OnAttached/OnDetached` 挂线/摘线（`Views/ClockSurface.axaml.cs:85-125`）、`ReleaseBitmaps` + `_releasePending`（`Views/BackdropLayer.cs:315-343`）、预烘缓存单槽取走即摘（`Models/BackdropPreloadCache.cs:65-76`）——三处都是针对 CI 长生命周期的泄漏防护。
12. **可读性兜底**：磨砂浓度滑块（`Frost`，`Helpers/LiquidGlassBaker.cs` 的 `LiquidGlassStyle.Frost`、`Views/BackdropLayer.cs:1102-1105` 注释「苹果那版栽的也是这个坑」）——意识到「花壁纸原样透过玻璃会淹掉文字」，把它做成用户可调项。

---

## 7. 可疑问题 / 风险点

> 以下按「可能性 × 影响」排序，均附证据。标 **[确认]** 的是从代码可直接推出的行为，标 **[推测]** 的是需要运行时验证的。

1. **[确认] 躲光标只用 `PointerMoved` 触发恢复，触屏设备可能永久无光标**。`OnPointerMoved` 是唯一把 `Cursor` 恢复成 `Default` 的地方（`Views/FullScreenClockWindow.axaml.cs:41-43`）；10 秒无移动后 `Cursor = null`（`:47-51`）。教室大屏是触屏（作者在弹幕代码里明确写了「教室大屏正是触屏」，`Views/ClockSurface.axaml.cs:542`），触屏设备上指针不动就没有 `PointerMoved`；若系统仍显示光标（或外接鼠标后不动），会一直保持隐藏。建议在 `PointerPressed`/`PointerEntered`/`Activated` 里也恢复一次。
2. **[确认] 每次鼠标移动都 `new CancellationTokenSource()` 且从不 `Dispose`**（`Views/FullScreenClockWindow.axaml.cs:44-46`）。`Task.Delay(10000, token)` 的定时器在取消时不会回收 CTS 本身；鼠标连续移动时每秒可产生几十个 CTS + 一个 10 秒 Delay 注册。单次很轻，但这是 UI 线程热路径上的无谓分配，可用「单个 timer + 时间戳」或 `CancellationTokenSource.CancelAfter` 复用一个实例替代。
3. **[确认] 液态玻璃烘焙是单线程串行，首帧成本集中**。`Task.Run` 里 `foreach (var job in jobs)` 逐块串行烘（`Views/BackdropLayer.cs:907-930`）；12 块 × 每块（例如 600×120 设备像素 ≈ 7.2 万像素）× 每像素最多 3 次双线性采样 + 3 次 SDF + `pow`，4K/125% 缩放下一次全量重烘是「几十~上百毫秒」量级。期间所有 `NeedsBake` 的块**不画**（`:552`、`:559`），表现为「换玻璃档/换图后先空一下再出现」。没有并行化（`Parallel.For` / 分块），也没有「先用上一次的位图凑合」的降级策略。
4. **[确认] `DrawGlassOutlines` 每帧新建 `Pen`**（`Views/BackdropLayer.cs:600-608`）：拖动位置/字号滑块时每帧一次分配，虽然只在拖动期间，但和「拖动中要极致省」的其它优化（降插值、停烘焙）不一致。
5. **[确认] `RenderCore` 里 Frosted 档每帧对每块玻璃做一次子矩形 `DrawImage`**（`Views/BackdropLayer.cs:633`，`DrawBlurredBackdrop`）：4K 下 12 块玻璃 = 每帧 12 次纹理采样绘制。若 GPU 较弱可能掉帧；可考虑把「模糊图 + 各块裁剪」合成到一张 RenderTargetBitmap 缓存，仅在背景/矩形变化时重建。
6. **[确认] 地震覆盖层每帧整屏重画两次模糊图**：`QuakeSheet`（整屏，`Views/ClockSurface.axaml:406`）与 `EqFrost`（卡片，`:414`）各自 `Render` 时 `TranslatePoint` + `DrawFrostedCard`（`Views/FrostedCardBackdrop.cs:83-94`），而 `DrawFrostedCard` 内部是整屏 `DrawImage(blur, visible, dest)` 的子矩形采样（`Views/BackdropLayer.cs:274`）。4K 模糊图每帧整屏重采样两次，弹出期间（最多数秒）代价可观。建议缓存一张 RenderTargetBitmap。
7. **[确认 + 推测] 贴边玻璃的外扩被静默裁掉，且裁边会让「玻璃与文字的相对余量」左右不对称**。`ComputeGlassRects` 里 `rect = rect.Intersect(new Rect(layerSize))`（`Views/BackdropLayer.cs:662`），而 `ReminderPanel` 是 `HorizontalAlignment=Left`、`Margin="0,16,0,0"`（`Views/ClockSurface.axaml:311-312`），所以它的玻璃左边界 `0 - InsetX(12) = -12` 会被裁成 0：左边贴屏、右边仍有 12 的余量，视觉上玻璃不是居中包住文字。功能上无害（投影本来就允许溢出，`:661` 注释也承认「只管主体」），但和「玻璃四周各外放一圈」的设计意图不一致；若希望对称，可对贴边块改用 `Math.Max(0, ...)` 之外的内缩策略或单独设 `InsetX`。另外若 `layerSize` 在布局收敛前抖一像素，`Intersect` 结果随之变、`changed` 会连续触发 `MarkGlassDirty`（有 120ms 防抖兜底，风险低）。
8. **[确认] `_glassShadows` / `_glassFill` / `_glassPen` 在每次 `Apply` 都重建**（`Views/BackdropLayer.cs:747-796`、`:210`）。设置页拖动「明暗」「透明度」滑块时每帧一次 `BoxShadows.Parse` + 3 个渐变刷子分配。注释说「一次分配无所谓」（`:790`），但与旁边 `RenderOptions` 降档、120ms 防抖的精细程度相比是漏网的一处。
9. **[确认] `FullScreenClockWindow` 的 `KeyDown` 只挡 Esc/F11，没有 `Alt+F4` 拦截**（`Views/FullScreenClockWindow.axaml.cs:32-39`）。若目标场景是「学生不能随手退出大屏」，Alt+F4/任务栏右键关闭仍可直接关窗（`Closed` 里会正常善后，不是崩溃，只是能被退出）。
10. **[确认] 弹幕 `sv.Tag` 一字段三用**（`TranslateTransform` 占位 / `DispatcherTimer` / null 状态，`Views/ClockSurface.axaml.cs:621-623`、`:697`、`:593-600`）。`StopMarquee` 里 `if (sv.Tag is DispatcherTimer timer) timer.Stop();` —— 若 Tag 停在 `TranslateTransform`（`StartMarqueeWhenReady` 的 Post 还没跑）时调 `StopMarquee`，计时器尚未创建、Post 里靠 `ReferenceEquals` 退出，逻辑是对的；但类型复用的写法很脆，未来加第四种语义就会出隐性 bug。建议改成独立的 `_marqueeState` 对象。
11. **[推测] `AlertPointerExited` 在 `_alertMarqueeOncePlaying` 为真时直接 return（`Views/ClockSurface.axaml.cs:567-569`），不清 `ExpandedAlertDetail`**。设计上依赖 16ms 计时器跑到 `stopAfter` 时清理（`:686-688`）。若期间窗口隐藏/最小化导致 `DispatcherTimer` 被外部 `StopMarquee` 停掉，而 `_alertMarqueeOncePlaying` 又被 `StopMarquee` 复位的时序不对，可能残留一条弹幕文本（顶部 `AlertDetailScroll` 仍可见）。需实测「悬停预警 → 最小化 → 恢复」路径。
12. **[推测] `BackdropPreloadCache` 单槽 + `ReleaseIfMatches` 的竞态**：`TryTake` 取走即摘（`Models/BackdropPreloadCache.cs:65-76`），`TryStartRebuild` 命中后 `Adopt` 直接返回、不再自己烘（`Views/BackdropLayer.cs:1249-1253`）；若同一时刻 `BackdropPreloadService` 又 `Publish` 了新版，旧的那份已在 UI 侧，无人释放它之外的——逻辑上 `Adopt` 会 `_image?.Dispose()` 旧的（`:1299`），风险低，但「预烘服务与窗口同时烘同一张图」的双份内存窗口确实存在（已有 `TryBeginBuild` 单飞，但只防服务内部）。
13. **[确认] 预览圆角只裁了背景层**：`Backdrop.CornerRadius = 12` 只让 `BackdropLayer.Render` `PushClip` 圆角（`Views/BackdropLayer.cs:478-485`），`ClockSurface` 里的文字/按钮**没有**被圆角裁剪；设置页靠外层 `Border ClipToBounds=True`（`Settings/SettingsPage.axaml:705`）只裁矩形、不裁圆角（作者在 `Views/ClockSurface.axaml.cs:39-42` 已经承认这一点）。内容本来不会溢出卡片，所以目前只是「背景四角会不会露出方角」的视觉问题，已用 `CornerRadius` 解决；但如果将来有控件溢出到圆角外就会露馅。

> 补充：`Views/BackdropLayer.cs` 里多处注释直接记录了历史事故（内存看门狗 1.40 GiB、`Visual was invalidated during the render pass` 导致插件被自动禁用、1.1.1.9 上课点名时崩溃）。这些注释本身就是最有价值的一手资料，建议在后续重构时保留原样，不要「清理」掉。
