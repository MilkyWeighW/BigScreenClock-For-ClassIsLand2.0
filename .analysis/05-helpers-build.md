# 05 · Helpers / 主题 / 背景图管线 / 构建发布 / git 历史

> 范围：`Helpers\*.cs`（LiquidGlassBaker、StackBlur、RoundedRectSdf、OffsetSampler、ClockBrushes、ClockThemeApplier、IconGlyph、StaticOverloadFinder）、`Models\` 中的主题与背景图一族、`LargeScreenClockRevived.csproj` / `manifest.yml` / `NativeMethods.txt` / `Properties\launchSettings.json` / `README.md`，以及 git 历史。
> 全部论断附 `相对路径:行号`。源码只读，本报告是唯一写入。
> 关键外部证据：本机 NuGet 缓存里只有 `classisland.pluginsdk` **1.7.106.2-dev-v2**，其 `build/net8.0/ClassIsland.PluginSdk.targets` 被用来解释 CreateCipx 的行为（工程引用的 2.1.0.1 未在本机缓存中，打包逻辑大概率一致，但结论按"缓存的 1.x targets"标注）。

---

## 0. 三个最重要的结论（先看这里）

1. **"玻璃"不是着色器，是一张张 CPU 烘出来的预乘 BGRA 小位图。** 每块玻璃单独一张 `WriteableBitmap`，按 `Rect/Radius/内容版本/缩放` 做缓存账，只重烘变了的那块（`Views\BackdropLayer.cs:69-75`、`Views\BackdropLayer.cs:1030-1081`、`Views\BackdropLayer.cs:938-983`）。这是把"整屏一张折射图"改成"按块"之后才解决的：整屏一张时，计数行每秒变字数就要重清重烘 1920×1080（8 MB/张），几十张就把 CI 的 1.40 GiB 内存看门狗顶爆（`Views\BackdropLayer.cs:69-74`）。
2. **玻璃里外能严丝合缝，靠的是结构而不是调参。** 清晰背景、磨砂底图、留白补边、CPU 采样器四者共用同一个 `BackgroundImageMap.ComputeDestRect`（`Models\BackgroundImageMap.cs:40-70`、`Views\BackdropLayer.cs:1002-1020`、`Views\BackdropLayer.cs:617-638`）。任何一处换成"图宽 ÷ 层宽"之类的近似，玻璃内外立刻错位。
3. **主题是"两槽位 + 哨兵"，不是算法生成。** 只有两套写死的 21 色调色板（`Models\ClockTheme.cs:115-141`、`Models\ClockTheme.cs:144-169`），用户改的 6 个颜色按变体存进 `LightColors` / `DarkColors` 两个槽（`Models\PluginSettings.cs:340-343`），靠 `AppliedPaletteVariant` 与 `VariantUnknown = -1` 哨兵保证切主题不丢色、老配置升级不变样（`Models\ClockTheme.cs:106-112`、`Helpers\ClockThemeApplier.cs:19-24`、`Helpers\ClockThemeApplier.cs:60-67`）。

---

## 1. 玻璃烘焙算法

### 1.1 为什么是 CPU 烘焙

`Helpers\LiquidGlassBaker.cs:91-94` 写明了理由：Avalonia 11 公开的 `DrawingContext` 没有 `PushEffect`，也没有自定义着色器入口，所以"真反射/折射"只能在 CPU 上算成位图再当普通图片贴上去。类注释的另外两条约束同样重要：只在背景换、组件挪、换主题时重算，**不是每帧**（`Helpers\LiquidGlassBaker.cs:92-94`）；烘焙器不碰 Avalonia 类型，因此可单测（`Helpers\LiquidGlassBaker.cs:96`）。

调用链固定为：`BackdropLayer.TryStartBake()` 在 `Task.Run` 里逐块调 `LiquidGlassBaker.RenderPanel(...)`（`Views\BackdropLayer.cs:907-930`），算完回到 UI 线程锁位图、`CopyInto`（`Views\BackdropLayer.cs:961-983`、`Views\BackdropLayer.cs:1314-1325`）。

### 1.2 StackBlur：三角核模糊（`Helpers\StackBlur.cs`、252 行）

- 实现是 Mario Klingemann 的 `stackblur.c` 移植（`Helpers\StackBlur.cs:5-6`），像素格式就是 Avalonia 的 Bgra8888（`:13`）。
- 精确参数：半径 `r`，`div = 2r+1`，`divsum = ((div+1)>>1)^2 = (r+1)^2`，查找表 `dv[i] = i / divsum`（`Helpers\StackBlur.cs:34` 起 `Blur` 内部）。两次箱式模糊叠加等价于权重为 `(r+1-|k|)` 的三角核，权重和为 `(r+1)^2`，与 `divsum` 一致。
- 入口 `Apply(pixels, width, height, radius)`：先钳半径（`Helpers\StackBlur.cs:34`），`radius < 1` 或任一维 `< 2` 直接返回（`:35`），越界按边缘钳制以杜绝黑边（`:82-83`）。
- **只改颜色、保 alpha**：`pix[yi] = (pix[yi] & 0xff000000u) | (red<<16) | (green<<8) | blue`（`Helpers\StackBlur.cs:208`）。
- **硬上限 `public const int MaxRadius = 88`**（`Helpers\StackBlur.cs:26`）。注释给了查找表尺寸账：`dv` 是 `256*(radius+1)^2` 个 short，半径 88 ≈ 4 MB、625 ≈ 200 MB、1667 ≈ 1.4 GB（`Helpers\StackBlur.cs:19-25`）——即"半径爆炸会直接吃掉整个进程预算"。
- 为什么不用 `Avalonia.Media.BlurEffect`：合成器效果，CI 在某些机器上可能走软件渲染后端；而且每块玻璃一张全屏位图比整图模糊一次更重（`Helpers\StackBlur.cs:9-11`）。

### 1.3 SDF 与梯度（`Helpers\RoundedRectSdf.cs`、91 行）

约定：内部为负、边界为 0、外部为正（`Helpers\RoundedRectSdf.cs:12-13`）。

- 圆角矩形距离场（`:22-37`）：
  `qx = |x| - (halfW - radius)`，`qy = |y| - (halfH - radius)`；
  `outside = len(max(qx,0), max(qy,0)) - radius`；
  `inside = min(max(qx,qy), 0)`；
  `sd = outside + inside`。`halfW/halfH` 先取 `max(0)` 防 NaN（`:25-26`）。
- 梯度 `Gradient(x,y,halfW,halfH,gradRadius)`（`:47-70`）：`qx>=0 || qy>=0` 走圆角区，返回 `(sign(x)*vx/len, sign(y)*vy/len)`，`len<1e-6` 返回 `(0,0)`；否则直边区 `horizontal = qx >= qy ? 1 : 0`，返回 `(sign(x)*horizontal, sign(y)*(1-horizontal))`。参数名是 `gradRadius` 而不是 `radius`，因为调用方故意传放大后的值（`Helpers\LiquidGlassBaker.cs:125`）来平滑直边/圆角交界，避免那里折出一道痕。
- `CircleMap(x) = 1 - sqrt(1 - x²)`（`:78-82`）：贴边陡、内沿趋平。注释一句话说透了设计意图——"线性深度像描边，圆弧映射才是透镜"（`:76`）。
- `SmoothStep(edge0, edge1, x)`（`:85-90`）用于 1 像素抗锯齿过渡。

### 1.4 RenderPanel：逐像素流水线（`Helpers\LiquidGlassBaker.cs:107-274`）

签名（`:107-110`）：`RenderPanel(uint[] layer, int layerWidth, int layerHeight, double panelX, double panelY, double panelWidth, double panelHeight, double radius, in LiquidGlassStyle style, IBackdropSampler backdrop)`。约定：`layer` 是 BGRA8888 **预乘**、按行连续；**只写玻璃覆盖的像素**，玻璃外一个字节都不动（`:101-105`），所以多块玻璃按顺序画上去自然叠合，重叠时后画的盖住先画的，与"整屏一张"的次序结果一致（`Views\BackdropLayer.cs:543-576`）。

| 步骤 | 公式 / 行为 | 行号 |
|---|---|---|
| 参数钳制 | `rr = clamp(radius, 0, min(halfW,halfH))` | `:117` |
| 折射带高 | `bandHeight = clamp(RefractionHeight, 0, min(halfW,halfH)*0.9)`（带不能比玻璃还宽，否则整块都在弯、看不出"中间是平的"） | `:120` |
| 位移量 | `amount = max(0, RefractionAmount)` | `:121` |
| 色散系数 | `dispK = clamp(Dispersion, 0, 0.4)` | `:122` |
| 梯度半径 | `gradRadius = min(rr*1.5, min(halfW,halfH))` | `:125` |
| 光照方向 | `l = (cos(LightAngle), sin(LightAngle))`，生产值 128° → 屏幕坐标（y 向下）下约 `(-0.616, +0.788)`，受光面偏**左下** | `:127-128`、`Views\BackdropLayer.cs:1098` |
| 材料色目标值 | `tint = TintIsLight ? 1.0 : 0.0`（浅色玻璃往白里混、深色往黑里混） | `:130` |
| 高光核 | `strokeHalf = HighlightWidth*0.5`、`sigma = max(strokeHalf*0.5, 0.35)`、`w0=1`、`w1=exp(-0.5)`、`sideWeight=w0+2*w1` | `:133-138` |
| 扫描范围 | 玻璃外接矩形 ∩ 层，`x/y` 用 `floor/ceil` 且每像素取 `+0.5` 像素中心 | `:141-145`、`:149`、`:155` |
| 距离场 | `sd = RoundedRect(cx, cy, halfW, halfH, rr)`；`sd > 0.5` 直接 `continue`（圆角外不写） | `:158-159` |
| 覆盖率 | `coverage = 1 - SmoothStep(-0.5, 0.5, sd)` | `:162` |
| 法线 | `(gradX,gradY)` 归一化；`gradLen <= 1e-6` 时 `n=(0,0)` | `:165-173` |
| 深度/折射带 | `depth = -sd`；`inBand = bandHeight>0.5 && depth<bandHeight`；`band = 1 - depth/bandHeight`（贴边 1、内沿 0） | `:175-181` |
| 位移 | `push = CircleMap(band) * amount` | `:182` |
| 采样坐标 | 层内坐标 `sx = localX + panelX`、`sy = localY + panelY`；`t = s + n*push` | `:188-191` |
| 色散 | `k = push*dispK`；R 取 `s + n*push*(1+dispK)`、G 取 `t`、B 取 `s + n*push*(1-dispK)`；alpha 用 G 那份 | `:199-206` |
| 无/有位移 | `push>0.01` 取 `Sample(t)`，否则取 `Sample(s)`（贴边一两像素外就是原样透过去） | `:207-216` |
| 磨砂 | `frost = style.Frost`；斜面加档 `frost += (1-frost)*band*RimFrost`，再 `clamp(0,1)`；按 `t` 取 `SampleBlurred` 做线性混合 | `:194-196`、`:218-226` |
| 材料色 | `k = tintAlpha * (1 - 0.33*clamp(localY/panelHeight,0,1))`，即"上端厚、往下渐薄"，逐通道 `c += (tint - c)*k` | `:229-235` |
| 斜面受光 | `lit = max(0, n·l)`；`k = 1 + BevelLight*band*lit`；`c *= k` | `:238-243` |
| 镜边高光 | 3 抽头：`i=-1..1`，`hard = |sd - i*sigma| < strokeHalf`，`mask = Σ w_i*hard / sideWeight * 0.5`（"只留边界内侧的一半"）；`lit = |n·l|^max(HighlightFalloff,0.1)`；`s = mask*lit*HighlightStrength` 加到 rgb | `:249-263` |
| 输出 | `alpha = coverage * clamp(a,0,1)`；`Pack(r*alpha, g*alpha, b*alpha, alpha)` | `:268-272`、`:276-283` |

`Pack` 是小端 BGRA：`bb | gb<<8 | rb<<16 | ab<<24`（`Helpers\LiquidGlassBaker.cs:276-283`），与 `WriteableBitmap(PixelFormat.Bgra8888, AlphaFormat.Premul)` 对齐（`Views\BackdropLayer.cs:967-969`、`Models\BackdropImageSet.cs:158`）。

**生产参数表**（`Views\BackdropLayer.cs:1085-1107`，全部长度乘 `scale` 换到物理像素）：

| 字段 | 值 | 行号 |
|---|---|---|
| `RefractionHeight` | `22 * scale` | `:1092` |
| `RefractionAmount` | `48 * k * scale`，`k = clamp(refraction,0,100)/100`（默认 50 → 24 px） | `:1088`、`:1093` |
| `Dispersion` | `0.04` | `:1094` |
| `HighlightWidth` | `2.0 * scale` | `:1095` |
| `HighlightFalloff` | `5` | `:1096` |
| `HighlightStrength` | `0.5` | `:1097` |
| `LightAngle` | `128°` | `:1098` |
| `BevelLight` | `0.35` | `:1099` |
| `TintAlpha` | `clamp(opacity,0,100)/100 * 0.8`（默认 35 → 0.28） | `:1100` |
| `TintIsLight` | `_chrome.IsLight` | `:1101` |
| `Frost` | `clamp(frost,0,100)/100`（默认 45 → 0.45） | `:1104` |
| `RimFrost` | `0.35` | `:1105` |

注意折射带是相对"玻璃半边"钳制的：小面板（如计数行，高约 24 DIP）`bandHeight` 会被压到 `0.9*halfH`（`Helpers\LiquidGlassBaker.cs:120`），所以同一套参数在大钟和计数行上的视觉效果并不等比——这是有意为之（避免小面板整块都在弯）。

### 1.5 OffsetSampler：分块烘焙与整层坐标的桥（`Helpers\OffsetSampler.cs`、34 行）

`sealed class OffsetSampler : IBackdropSampler`，ctor `(inner, dx, dy)`，`Sample` / `SampleBlurred` 都转发 `(x+dx, y+dy)`（`:31-33`）。
两条硬约束写在注释里：
- 偏移必须是**整数设备像素**（`:8-12`）。调用方用"外接框向下/向左取整、向上/向右取整"包住整块（`Views\BackdropLayer.cs:877-880`），小数部分通过 `PanelX/PanelY = Rect.X*scale - px` 传进烘焙器（`Views\BackdropLayer.cs:893-899`），这样玻璃形状仍画在正确亚像素位置。
- **钳制绝不能在偏移层做**（`:14-16`），否则贴边采样会按块边界拉长边缘像素，块与块之间出现假接缝；钳制放在内层 `LayerBackdrop.Fetch`（`Views\BackdropLayer.cs:1143-1145`）。
等价性由 `LiquidGlassTests` 钉住（`Helpers\OffsetSampler.cs:12`，该测试工程不在仓库里）。

### 1.6 成本与护栏

- 每像素最多 3 次清晰采样（色散）+ 1 次模糊采样 = 4 次双线性取点（`Helpers\LiquidGlassBaker.cs:199-226`、`Views\BackdropLayer.cs:1174-1207`），全在后台线程。
- 层像素数 `> 40_000_000`（多屏拼出的巨桌面）直接放弃折射，避免"一直在等新图"而整块不画（`Views\BackdropLayer.cs:860-865`）。
- 烘焙位图/缓冲按块复用：`panel.Canvas` 尺寸对得上就不再分配（`Views\BackdropLayer.cs:900`、`:914`、`:963-970`）；算的过程中矩形/版本/缩放变了则整批作废并稍后重来（`:938-952`、`:985-988`）。

---

## 2. 主题系统

### 2.1 两套枚举，别混

- `ClockThemeMode`：`Follow=0, Light=1, Dark=2, Auto=3`（`Models\ClockTheme.cs:4-22`）。`Auto` 是后加的，**刻意排在最后**以免打乱老配置里 0/1/2 的含义（`Models\ClockTheme.cs:19-21`）。
- `BackgroundThemeMode`（仅"背景开关打开"时生效）：`Auto=0, Dark=1, Light=2`（`Models\BackgroundThemeMode.cs:11-21`）。两个枚举整数不同（`Auto` 一个 3 一个 0、`Dark` 一个 2 一个 1），因此必须显式映射 `ToClockThemeMode`（`Models\BackgroundThemeMode.cs:24-32`），直接强转必错。`Models\BackgroundThemeMode.cs:4-9` 解释了为什么不复用一套：有背景图时"黑暗"这个词会和"亮壁纸上垫深色玻璃调出一屏白字"的观感对不上，所以一律按**看到的样子**命名（暗底白字 / 亮底黑字）。
- `GlassMode`：`Off=0, Translucent=1, Frosted=2, Liquid=3`，存成 int，枚举只给代码取名（`Models\GlassMode.cs:7-23`）。

### 2.2 调色板：没有"生成"，只有两张写死的表

`ClockThemePalette` 是 `required init` 记录式类，共 21 个字段：15 个界面语义色（`IsLight`、TextPrimary/Body/Secondary/Muted/Faint、Countdown、Divider、ButtonForeground/Background/Hover、PopupBackground/Border/Text、VolumeTrack、PanelBackground，`Models\ClockTheme.cs:31-76`）+ 6 个"设置项颜色"默认值（`BackgroundColor/FontColor/CourseInfoColor/NoiseTitleColor/ProgressColor/RainColor`，`:78-96`）。

- Dark（`Models\ClockTheme.cs:115-141`）：白字系（`#FFFFFF`/`#DDFFFFFF`/`#AAFFFFFF`/`#66FFFFFF`/`#40FFFFFF`）、倒计时 `#FFFFDD`、按钮 `#59000000`/悬停 `#80000000`、弹窗 `#14141F`、音量轨 `#B31C3047`、面板 `#1A1A2E`，用户色默认 `#000000`/`#FFFFFF`/`#88CCFF`/`#BBFFFFFF`/`#4FC3F7`/`#7FD4FF`。
- Light（`:144-169`）：黑字系、按钮 `#59FFFFFF`/`#80FFFFFF`、弹窗 `#FFFFFF`、音量轨 `#B3D5DEE8`、面板 `#E8EDF3`，用户色默认 `#FFFFFF`/`#000000`/`#1565C0`/`#99000000`/`#0288D1`/`#0277BD`。
- 变体常量：`VariantUnknown = -1`、`VariantLight = 0`、`VariantDark = 1`（`:106-112`）。`PaletteOf` 遇到未知变体按 Dark（`:190-191`），与旧版外观一致。

`ClockBrushes` 把调色板一次性转成 15 个 `IBrush`（`Helpers\ClockBrushes.cs:16-50`，`From` 在 `:52`，`Solid` 在 `:54`），设计意图是"整批替换、绑 `Chrome.Xxx` 只通知一次"，避免逐个换刷子导致 N 次布局/重绘（`Helpers\ClockBrushes.cs:6-10`）。`IsLight`（`:14`）是下游一切"浅底要压暗的颜色"的开关。

### 2.3 变体解析真值表

`ClockTheme.ResolveVariant(mode, ciIsDark, backgroundIsDark)`（`Models\ClockTheme.cs:178-187`）：

| 模式 | 背景图明暗 | 生效变体 |
|---|---|---|
| Light | 任意 | `VariantLight`（浅色玻璃 + 黑字） |
| Dark | 任意 | `VariantDark`（深色玻璃 + 白字） |
| Auto(3) | `true`（暗图） | `VariantLight`（浅色玻璃 + 黑字） |
| Auto(3) | `false`（亮图） | `VariantDark`（深色玻璃 + 白字，先把亮壁压暗） |
| Auto(3) | `null`（没图/读不出） | 退化为跟随 CI：`ciIsDark ? Dark : Light` |
| Follow(0) / 其它 | 忽略 | `ciIsDark ? Dark : Light` |

`ciIsDark` 的取值是 `Application.Current?.ActualThemeVariant != ThemeVariant.Light`（`ViewModels\FullScreenClockViewModel.cs:397`）——即"CI 当前实际生效的变体"，而不是 CI 设置里的字符串。

**命名陷阱**：`VariantLight` 表示"浅色玻璃 + 黑字"，即用于**暗背景**；`VariantDark` 表示"深色玻璃 + 白字"，用于**亮背景**。二者与"背景图亮/暗"是反的；读代码时若把 `VariantLight` 当成"亮背景"会得到完全相反的结论。

### 2.4 Apply / Capture / Restore：为什么切主题不丢用户自定义色

`ClockThemeApplier.Apply(settings, ciIsDark)`（`Helpers\ClockThemeApplier.cs:16-25`）：

1. `variant = ResolveVariant(EffectiveMode(settings), ciIsDark, BackgroundIsDark(settings))`（`:18`）；
2. 若 `settings.AppliedPaletteVariant == variant` 直接返回 `false`（`:19`）；
3. `Capture(settings, 旧 variant)`：把当前生效的 6 个颜色写回**旧变体自己的槽**（`LightColors` 或 `DarkColors`，`:70-83`）；
4. `Restore(settings, 新 variant)`：把新槽里的 6 个颜色写成当前生效颜色（`:86-95`）；
5. 写 `AppliedPaletteVariant = variant`，返回 `true`。

"某个主题第一次被用到时，槽里就是该主题的默认色"——因为两个槽在设置里就是用 `ThemeColorSet.FromPalette(ClockTheme.Light/Dark)` 初始化的（`Models\PluginSettings.cs:340-343`、`Models\ThemeColorSet.cs:20-28`），6 个字段的默认值也直接取 Dark 表（`Models\ThemeColorSet.cs:12-17`）。于是"用户调过色 → 切走 → 切回"能还原他自己那套，这是整个主题系统最核心的一处状态设计。

`EffectiveMode`：背景开关开着就用 `BackgroundThemeMode`，否则用外观里的 `ThemeMode`（`Helpers\ClockThemeApplier.cs:31-34`）——背景主题档生效时 `ThemeMode` 整项失效（`Models\PluginSettings.cs:216-217`）。

`BackgroundIsDark`：`BackgroundImageStore.ResolvePath` → `ReadLuminance` → `BackgroundTone.IsDark(luminance, settings.BackgroundDim)`；读不到返回 `null` 表示"没有背景图/读不出来"（`Helpers\ClockThemeApplier.cs:40-46`）。**遮罩参与判定**（`Models\BackgroundTone.cs:39-45`）：盖黑往 0 拉 `L*(1-a)`、盖白往 1 拉 `L*(1-a)+a`，alpha = `|dim|/100`，阈值 `DarkThreshold = 0.5`（`:12`）。亮图被压暗到看不清时，白字才立得住——这是 `IsDark` 要吃 `dim` 的原因。

应用链路（谁在什么时候调）：
- 插件初始化：若 `Settings.json` 已存在才跑 `MigrateLegacySettings`（`Plugin.cs:50-51`）；
- 大屏 VM：`ApplyThemePalette()` → `Apply` → `RefreshChrome()` → `RefreshSemanticColors()`（`ViewModels\FullScreenClockViewModel.cs:456-461`、`:400-435`），`Chrome` 由 `ResolvePalette` 重建（`:402`）；
- 设置页：预览刷新时 `Apply`（`Settings\SettingsPage.axaml.cs:59`、`:236`、`:307`）；
- `settings.PropertyChanged` → 400 ms 防抖保存（`Plugin.cs:22-23`、`:53-62`），所以 Apply 里连写 6 个属性只会落一次盘。

### 2.5 老版本迁移

`MigrateLegacySettings`（`Helpers\ClockThemeApplier.cs:60-67`）：仅当 `AppliedPaletteVariant == VariantUnknown` 时执行——把当前那套（旧版只有一套）颜色 `Capture` 进 Dark 槽、`ThemeMode = Dark`、`AppliedPaletteVariant = VariantDark`。设计意图（`:52-58`）：旧版默认就是黑底白字，若不锁 Dark，跟随 CI 会在浅色 CI 上把外观整个换成明亮那套，用户会以为设置丢了。触发条件是"配置文件已存在"（`Plugin.cs:50`），新装用户没有文件，故保持默认"跟随"。

### 2.6 语义色（数据色，不随主题翻转）

`Models\SemanticColors.cs` 把"数据语义色"独立出来：这些颜色表达数据（黄=一般、红=吵闹、按官方等级着色），语义不随主题变，只是浅底上原色太亮读不清，故按底色深浅换一版明度（`:3-7`）。
- `CountNoisyHex(light)`：深色 `#FF5555` / 浅色 `#C62828`（`:19`）；
- `EarthquakeHex(intensity, light)`：`≤2` 蓝、`≤4` 黄、`≤6` 橙、`>6` 红，浅底换同色相压暗版（`:25-35`）；
- `AlertHex(level, light)`：按中文等级字符串 `"蓝色"/"黄色"/"橙色"/"红色"` 映射，未知返回 `null` 由调用方用正文色兜底（`:38-49`）。

`IsLight` 从 `Chrome` 流到这些函数（`ViewModels\FullScreenClockViewModel.cs:819`、`:1381`），`CountNormalHex(bool light)` 却是唯一忽略参数的（见 §6 问题 3）。

### 2.7 字体跟随

`CiMainFont.ReadFontString(dataDir)` 从 CI 的 `Settings.json` 读 `MainWindowFont`，取逗号前第一段文本字体（第二段是 FluentSystemIcons 图标字体，大屏不需要），再补 `Microsoft YaHei UI, SimHei` 中文回退（`Models\CiMainFont.cs:12`、`:40-51`）；`FontFamily.Parse` 按逗号拆分且不 trim，所以先自己 trim（`:44-48`）。VM 侧解析失败回落默认字体（`ViewModels\FullScreenClockViewModel.cs:463-470`）。动机写在 `Models\CiMainFont.cs:5-8`：原来用 Consolas，数字 0 中间带斜杠。

---

## 3. 背景图管线

```
选择文件（设置页）
  └─ BackgroundImageStore.Replace      复制副本进配置目录、写设置、删上一张
       └─ ImageHeader.ReadSize         只读文件头拿原图尺寸（不解码）
            └─ BackgroundImageMap.DecodeSize   算解码尺寸（绝不放大 + 像素预算）
                 └─ BackdropImageSet.Build     解码 + 像素快照 + 整张 StackBlur + 模糊快照
                      ├─ 绘制：BackdropLayer.RenderCore（清晰图 / Fit 补边 / 明暗遮罩）
                      └─ 采样：LayerBackdrop（清晰/模糊/补边/主题底色/遮罩）→ LiquidGlassBaker
预热：BackdropPreloadService（课前 2 分钟）→ BackdropPreloadCache（单槽）→ BackdropLayer.TryTake
明暗：BackgroundImageStore.ReadLuminance(48px 缩略图) → BackgroundTone → ClockThemeApplier
```

### 3.1 导入与存储（`Models\BackgroundImageStore.cs`）

- 副本文件名 `background-<yyyyMMddHHmmssfff><ext>`（`:43-44`）。**为什么带时间戳**：固定名字时"jpg 换 jpg"设置字符串不变，`PropertyChanged` 不发，大屏和预览都以为没换过（`:32-35`）。
- 写入顺序：先写 `target.tmp` → 复制成功才改名（同目录 `File.Move` 原子）→ 设置指向新名 → **最后**才删旧图（`:48-66`）。以前"先删旧再拷新"，源文件读不了/磁盘满时用户新旧都没有、背景直接退回纯色（`:49-50`）。异常时清掉 tmp 再抛（`:57-61`）。
- 清理只删自己放的副本：`Directory.GetFiles(folder, "background*")` 后再用 `background.` / `background-` 前缀二次过滤（`:159-166`），不误伤配置目录里同前缀的其它文件；删不掉（占用）就留给下次（`:168-169`）。

### 3.2 读文件头（`Models\ImageHeader.cs`、205 行）

纯字节解析、不解码、不依赖渲染器（`:7`）。动机：`Bitmap.DecodeToWidth(流, 宽)` 给多宽就解多宽，1600×1600 的壁纸给 2560 会解成 2560×2560，白占一倍半内存并多插一遍值（`:9-13`）。

| 格式 | 解析位置 | 行号 |
|---|---|---|
| PNG | 签名 + 第一个块必须是 `IHDR`，宽高 4 字节大端 | `:19`、`:50`、`:59-64` |
| JPEG | 逐段跳 marker 找 SOF0~SOF15（排除 C4/C8/CC），**不能只读开头几十字节**（手机照片前面可能挂着几十 KB EXIF，SOF 远在后面） | `:44-45`、`:108-136` |
| GIF | 逻辑屏幕宽高各 2 字节小端 | `:51`、`:67-68` |
| BMP | DIB 头大小 12（OS/2，2 字节宽高）或 ≥40（4 字节，高度取绝对值以兼容自上而下） | `:52`、`:71-80` |
| WebP | 有 `VP8X` 信画布（24 位 +1）；`VP8L` 无损按 14 位；`VP8 `（有损）跳过 3 字节帧标签 + 起始码 `9D 01 2A` 再取 14 位宽高 | `:22`、`:53`、`:86-102` |

认不出的格式返回 `null`，调用方按"老办法（按窗口宽解）"处理（`:14`）——这条回退路径正是 §6 问题 2 的风险源。

### 3.3 解码尺寸（`Models\BackgroundImageMap.cs:125-153`）

两条规矩写在 `:114-121`：
1. **绝不放大**：`t = min(1, needed)`（`:140`）。`needed` 按填充方式选：Fit 取 `min(sx,sy)`、Stretch 取 `sx`、FillCrop 取 `max(sx,sy)`（`:133-138`）。原图比屏幕小就按原样解，放大交给绘制那一步。
2. **像素预算**：解码像素 `> box.Width*box.Height*1.25` 时按 `sqrt(budget/(w*h))` 再压一档（`:31`、`:144-150`）。极端长条图（超宽全景/超长截图）"宁可稍微糊一点，也不要把看门狗顶爆"。

### 3.4 三种填充模式（`Models\BackgroundImageMap.cs:8-18`、`:40-70`）

| 模式 | 语义 | 缩放 | 结果 |
|---|---|---|---|
| `FillCrop = 0` | 铺满裁切（CSS cover / UniformToFill） | `max(sx,sy)` | 一维溢出被裁 |
| `Fit = 1` | 完整显示（contain / Uniform） | `min(sx,sy)` | 两侧或上下留白，留白由**同图放大模糊**填充（`Views\BackdropLayer.cs:511-519`） |
| `Stretch = 2` | 拉伸（fill） | `(sx,sy)` | 长宽比不保 |

`ComputeDestRect` 以视口中心对齐，允许结果超出视口，由绘制方裁剪（`:33-63`）。`MapToImage`（`:81-94`）把视口矩形反算到图上的像素矩形，"完整显示"会算出图外坐标，这是预期行为——绘制方本身有底色垫着，采样按边缘钳制（`:72-75`）。`ScaleTo`（`:158-166`）是可见但本管线里未用于模糊套路的等比换算辅助函数。

### 3.5 烘焙一套（`Models\BackdropImageSet.cs`、221 行）

`Build(path, pixels, stretch, blurRadius, wantBlur)`（`:46-84`）：
1. 文件存在才做，整段包在 `try/catch` 里，异常吞掉返回空套（`:57-83`）——"图读不了不该把整个大屏拖垮"（`:42-44`）；
2. `ImageHeader.ReadSize(path) ?? pixels`（`:63`）→ `DecodeSize`（`:64`）→ `Bitmap.DecodeToWidth(..., HighQuality)`（`:67`）；
3. `ReadPixels(Sharp)` 倒成连续 `uint[]`（`:69-70`、`:169-183`）：位图每行可能带对齐 padding，必须逐行 `Buffer.MemoryCopy`，不能把首地址当 `w*h` 连续内存（`:165-167`、`:185-198`）；
4. 需要模糊时 `BakeBlur`：新建 `WriteableBitmap` → `CopyPixels` 进锁住缓冲 → `BlurFramebuffer`（`StackBlur.Apply`）→ 再拷回（`:73`、`:153-163`、`:201-220`）。**为什么模糊整张图而不是烘一张"全屏模糊画面"**：后者要走 `RenderTargetBitmap`，与渲染后端绑定、后台线程渲可能出岔子，一抛异常整块背景就没了；模糊整张图是纯 CPU，不碰渲染器（`:144-152`）。"玻璃里模糊、玻璃外清晰"对得上，靠的是绘制时共用同一个 `ComputeDestRect`，与底图尺寸无关。
5. 半径换算 `BlurRadiusInImagePixels`：`radius * ImagePixelsPerViewportPixel(imageSize, layer, stretch)`，再 `clamp(0, StackBlur.MaxRadius)`（`:133-142`）。注释举了两个真实反例：100000×10 截图铺满整屏宽度比是几十；1×2160 竖图在"完整显示"档能到 46 倍——半径两百多时查找表上百兆，"一张图足以把看门狗顶爆"（`:121-131`）。

`Matches`（`:94-109`）：路径、填充方式、要不要模糊必须严格相同；半径容差 0.4；尺寸容差 `max(4, 10%)`。理由（`:86-93`）：预烘按屏幕物理分辨率估，真开窗可能差一两个像素，画的时候按位图自己尺寸落位、采样按快照自己尺寸算，整套自洽，只是分辨率略不同；差太多（换屏/大改窗口）必须重来，否则整张明显发虚。

### 3.6 明暗与主题色提取

`ReadLuminance`（`Models\BackgroundImageStore.cs:90-118`）：只把图解到 48 px 宽的缩略图（`:79`、`:102`）再算平均亮度，按 `(路径, 最后修改时间)` 缓存一份结果（`:94-96`、`:111-116`）——拖"背景明暗"滑块时不会反复解图。像素读取把预乘 BGRA 还原成非预乘再拼 `0xAARRGGBB`（`:121-151`，还原在 `:140-145`）。
`BackgroundTone.AverageLuminance`：跳过全透明像素，按人眼敏感度加权 `0.2126R + 0.7152G + 0.0722B`（`Models\BackgroundTone.cs:26`），全透明时返回阈值本身（`:30`）。

"主题色提取"在这个项目里**不是** k-means/调色板量化，而是**一个标量亮度** → 二选一（亮变体/暗变体）。这是有意的简化：只有两套调色板，不需要主色。

### 3.7 绘制与采样

`RenderCore`（`Views\BackdropLayer.cs:503-577`）顺序：兜底底色 → （Fit）放大模糊补边 → 清晰图 → 明暗遮罩 → 玻璃。`DrawBlurredBackdrop`（`:617-638`）在玻璃范围内**重画"底色 → 模糊图 → 遮罩"三层**，因为模糊图会盖掉整层遮罩，不重画玻璃里外就差一档亮度（`:610-615`）。`LayerBackdrop.Fetch`（`:1141-1170`）把四件事收成一个采样器：清晰图 → （落不到图上）留白补边 → （再落不到）主题底色，最后叠遮罩；坐标先钳到层内（`:1143-1145`），等效于拉长边缘像素、不带黑边。

### 3.8 预加载缓存

`BackdropPreloadCache` 是**单槽**静态缓存（`Models\BackdropPreloadCache.cs:10-15`）：
- `TryBeginBuild` 抢"正在烘"名额（`:18-26`，避免烘第二份）；`Publish` 换入并释放旧份（`:35-46`）；`TryTake` **取走即摘除，所有权转移**（`:65-76`）；`IsReadyFor` 只闻不问（`:58-62`）；`ReleaseIfMatches` 用于"自己烘好了，后台那份同样参数的就多余"（`:81-93`）；`Clear` 在设置变化时调用（`:96-105`）；`IsStale(10min)` 防止课取消后攥着一两百兆占一整天（`:52-55`、`Services\BackdropPreloadService.cs:87`）。

`BackdropPreloadService`（`Services\BackdropPreloadService.cs`）：`Lead = 2min`、`Poll = 20s`（`:27`、`:30`）。动机写在 `:13-22`：4K 解码 + 整张模糊 + 两份快照一瞬间吃几百兆，正好撞上"19:00 大屏点亮"那一刻的 CI 内存看门狗（1.40 GiB）强杀；提前做完，GC 有两分钟回收，开屏只剩建窗口。
- 只在"下一节目标课在 2 分钟内开"且"大屏没开着"时做（`:90`、`:98-99`），课表与屏幕信息在 UI 线程问（`:93-94`）；
- `wantBlur` 与 `WantBlurNow()` 必须一致：磨砂/折射两档要模糊，"完整显示"的补边也要（`:110-111` 与 `Views\BackdropLayer.cs:1226-1228`）；
- 烘完立刻 `GC.Collect(GC.MaxGeneration, Forced, blocking: true, compacting: false)`（`:134`）；
- 尺寸按"CI 主窗口所在那块屏"的 `Screen.Bounds` 估（`:173-194`），差一两个像素由 `Matches` 容差兜住；
- 设置变更清缓存只认 `BackgroundEnabled/BackgroundImageFile/BackgroundImageStretch/GlassBlurRadius/GlassMode`（`:41-51`）——`BackgroundDim`、`BackgroundThemeMode`、`ThemeMode` 不在其中是对的，它们没烘进图里。

### 3.9 支持的格式与尺寸限制（汇总）

- **可选格式**：`*.jpg *.jpeg *.png *.bmp *.webp *.gif`（`Settings\SettingsPage.axaml.cs:374`）；文件头解析覆盖 PNG/JPEG/GIF/BMP/WebP（`Models\ImageHeader.cs:50-53`）；实际能否解码取决于 Skia，解码失败静默退回纯色底。
- **尺寸限制**：没有文件大小/像素数硬限制；隐式上限是解码像素 ≤ `屏幕像素 × 1.25`（`Models\BackgroundImageMap.cs:31`、`:144-150`）；模糊半径 ≤ 88 图内像素（`Helpers\StackBlur.cs:26`、`Models\BackdropImageSet.cs:141`）；折射层像素 ≤ 4000 万（`Views\BackdropLayer.cs:860`）；烘焙尺寸下限 `max(64,·) × max(36,·)`（`Views\BackdropLayer.cs:403-408`）。

---

## 4. 构建与发布

### 4.1 工程属性（`LargeScreenClockRevived.csproj`）

| 项 | 值 | 行号 |
|---|---|---|
| TargetFramework | `net8.0-windows` | `:4` |
| CreateCipx | `true`（每份构建都产 cipx） | `:7` |
| RootNamespace | `LargeScreenClockRevived`（= `manifest.yml:4` 的 `entranceAssembly` 程序集名） | `:8` |
| CopyLocalLockFileAssemblies | `true`（依赖全拷到输出，才可能打进 cipx） | `:9` |
| AllowUnsafeBlocks | `true`（CsWin32 生成的 P/Invoke 要指针） | `:10-11` |
| BuiltInComInteropSupport | `true`（摄像头占用检测的 COM 回调接口） | `:12` |
| ClassIslandPluginSdkVersion | `2.1.0.1` | `:16` |
| ClassIsland.PluginSdk | `ExcludeAssets=runtime;native`（宿主已加载，不重复打包） | `:20-22` |
| NAudio | `2.2.1`（分贝检测） | `:23` |
| Microsoft.Windows.CsWin32 | `0.3.106`，`PrivateAssets=all`（只在编译期生成代码，不进 cipx） | `:25` |
| Avalonia.Controls.ColorPicker | `11.3.17`，`ExcludeAssets=runtime;native` | `:26-29` |

源生成：`NativeMethods.txt` 列出 MediaFoundation 的传感器活动监控 API（`MFStartup`/`MFShutdown`/`MFCreateAttributes`/`MF_DEVSOURCE_ATTRIBUTE_*`/`MFCreateSensorActivityMonitor`/`IMFSensorActivityMonitor`/`IMFSensorActivitiesReport*`/`MFSensorDeviceMode`/`CoTaskMemFree`，`NativeMethods.txt:3-18`），CsWin32 按清单生成 P/Invoke 与 COM 接口声明（`NativeMethods.txt:1-2`）。这只用于 `Services\CameraActivityService.cs`。

### 4.2 manifest vs SDK 版本

`manifest.yml`：`id: com.largescreenclockrevived`、`name: 大屏时钟`、`entranceAssembly: "LargeScreenClockRevived.dll"`、`repoOwner/repoName`、`assetsRoot: main`、`version: 1.2.0.0`、**`apiVersion: 2.0.0.0`**、`author: sokumei`、`supportedOSPlatforms: [Windows]`（`manifest.yml:1-13`）。

工程按 **SDK 2.1.0.1** 编译（`LargeScreenClockRevived.csproj:16`），清单却声明 **apiVersion 2.0.0.0**（`manifest.yml:10`）。见 §6 问题 1。

Avalonia 对齐：`Avalonia.Controls.ColorPicker 11.3.17` 与 ClassIsland 主程序同版本，`ExcludeAssets=runtime;native` 避免与宿主重复加载（`LargeScreenClockRevived.csproj:26-29`）。调试走 `Properties\launchSettings.json`：以 CI 可执行文件启动并 `-epp $(TargetDir)` 挂插件目录（`:3-8`），依赖 CI 侧 SDK 提供的两个属性。

### 4.3 CreateCipx 打包（依据缓存 SDK 1.7.106.2-dev-v2 的 targets）

`build/net8.0/ClassIsland.PluginSdk.targets` 内容：
- `<Target Name="CreateCipx" AfterTargets="Build" Condition="'$(CreateCipx)'=='true'">`；
- `CipxPackageOutputDirectory = $(MSBuildProjectDirectory)/cipx`，输出 `$(MSBuildProjectName).cipx`；
- 若 `$(OutputPath)/manifest.yml` 不存在 → 报错 `CISDK0001`；
- **`RemoveDir` 清空 `cipx` 目录 → `ZipDirectory SourceDirectory="$(OutputPath)"`**（即把整个输出目录压成 cipx）→ 调 `pwsh -ep bypass -File generate-md5.ps1` 生成 MD5 摘要（`PowershellBinaryName` 在 targets 顶部硬编码为 `pwsh`）。

由此得到两个工程层结论：
1. 打包**没有文件白名单**，`bin/Debug/net8.0-windows` 里有什么就进什么（PDB、旧文件、任何本地杂物）；
2. 项目自己的 `StampManifestVersion` 必须在 zip 之前跑，所以用 `BeforeTargets="CreateCipx"`（`LargeScreenClockRevived.csproj:91`）；实测顺序成立：`CreateCipx` 是 `AfterTargets="Build"`，而包 targets 在工程 targets 之后导入，`BeforeTargets` 钩子先执行。

### 4.4 DevBuild 五位内部版号机制（`LargeScreenClockRevived.csproj:32-108`）

设计（注释 `:32-44`）：`manifest.yml` 永远只写四位对外版号；本机每次 build 在**打包产物**（`bin` 里那份 manifest 与 cipx）上追加一位递增序号 → `1.1.1.8.1`、`1.1.1.8.2`……源码和 GitHub 上传夹里看不到它。动机：2026-10-12 之前本机改了十几版全叫 `1.1.1.7`，希沃上装错包判断不出是包的问题还是系统的问题。五位号 ClassIsland 能正常加载、日志照原样显示，但**更新检查走 `Version.TryParse`，五位号解析失败会被跳过**——内部开发版天然流不出去。换四位号（如 1.2.0.0）时 `devbuild.txt` 里存的上一个四位号对不上，自动从 `.1` 重新数（`:74-78`）。出对外包：`dotnet build -c Release -t:Rebuild -p:DevBuild=0`（`:43`）。

实现：
- `UsingTask StampDevBuild`（RoslynCodeTaskFactory，`:49-89`）读 manifest 的 `version:` 行；非 Release 时读 `devbuild.txt`（两行：上一个四位号、上一序号），`count = last + 1`，写回文件并把 manifest 改成 `base.count`；Release（`DevBuild=0`）不改号、不写计数器（`:66-81`）。
- `StampManifestVersion`（`:91-108`）：先判 Release（`:93-96`，因为"属性只在 Condition 里求值"），**每次都从源清单重拷一份再盖号**（`:98-101`，输出目录里那份可能是上次盖过号的），然后 `StampDevBuild`，最后 `Message` 打印 `插件版本：$(_StampedVersion)`。

风险（详见 §6 问题 4/6）：
- `CreateCipx` 无 `Inputs/Outputs`，所以每次 build（包括什么都没改的空 build）都会跑 `StampManifestVersion` → 序号必涨、cipx 必重打；`-t:Rebuild` 并非版本正确性的必要条件（`:98-101` 已强制重拷），但注释要求它，说明作者是靠 Rebuild 保证干净输出。
- `devbuild.txt` 写在工程根目录（`:46`），而仓库**没有 `.gitignore`**（见 §5）→ 每次构建都给工作区留一个未跟踪文件，`git add -A` 会把它和 bin/obj 一起提交（历史上确实提交过）。
- 构建需要写工程目录：只读检出 / 并行构建 / IDE 与 CLI 同时构建时，`File.WriteAllLines(CounterFile, ...)`（`:80`）与 `$(OutputPath)/manifest.yml` 的写写竞争会失败或错号。

### 4.5 发布面

`README.md` 是插件说明书兼发布说明（`README.md:1-25`，含 B 站演示视频 BV 号 `:5`），`README.md`/`icon.png`/`manifest.yml` 都 `CopyToOutputDirectory=PreserveNewest` 进输出（`LargeScreenClockRevived.csproj:110-120`）——因此 cipx 里自带 README 与图标，符合 CI 插件市场（`assetsRoot: main`，`manifest.yml:8`）的资产约定。仓库里没有 GitHub Actions/CI 配置，也没有测试工程（`LiquidGlassTests` 只以注释形式出现，`Helpers\OffsetSampler.cs:12`），发布链路是"本机构建 → 手动上传 cipx"。历史上 cipx/bin/obj 曾被提交又删除（`a26660b`、`13392d7`、`6b9cf8a`、`dbc762f`），说明有一段时间发布产物是直接进仓库的。

---

## 5. git 历史特征

`git log`：**34 个提交，0 次 merge，无分支（只有 main）、无 tag**。提交主题分布（`git log --pretty=format:'%s' | Group-Object`）：

| 主题 | 次数 |
|---|---|
| `Add files via upload` | 20 |
| `Update README.md` | 6 |
| `Delete cipx directory` | 2 |
| `Delete docs directory` | 2 |
| `更新 README.md` | 1 |
| `Delete bin directory` | 1 |
| `Delete obj directory` | 1 |
| `Initial commit` | 1 |

时间跨度 `2026-07-26`（Initial commit 只含 LICENSE + README 两行，`4a69ded`）→ `2026-10-31`（`2c8d48a`）。全部提交者 `sokumei`，日期集中在深夜/凌晨（如 `2026-10-31 01:53:35`、`2026-10-02 00:12:47`）。

"Add files via upload" 是 **GitHub 网页端拖拽上传**自动生成的默认提交信息。它能说明的流程特征：

1. **没有本地 git 工作流**：作者在本地写代码、编译验证，然后把改动过的源文件拖到网页上提交；提交信息、粒度、原子性都由网页上传界面决定，一个提交往往混着"新功能 + 顺手修 bug + README"（例如 `2c8d48a` 一次改了 21 个文件、+1634/−373，同时新增 `FrostedCardBackdrop`/`LetterWindow`、改 `BackdropLayer`、改 `manifest.yml` 版本号）。
2. **没有分支/PR/评审**：单人项目，直接提交到 main；没有 merge 提交，也没有 issue/PR 关联。
3. **仓库曾被当作网盘**：`bin/obj/cipx` 与 `docs/` 一度被提交（分别由 `13392d7`、`6b9cf8a`、`a26660b`/`dbc762f`、`a54be78`/`b5c4f42` 删除），`docs/CHANGELOG.md`（170 行）与 `docs/设计文档.md`（422 行）也曾存在后被删除（`a54be78` 的 stat）。仓库至今**没有 `.gitignore`**，所以每次本地构建都会让 `git status` 变脏。
4. **版本发布节奏可从 manifest 反推**（`git log -p -- manifest.yml`）：`1.0.0.0`(7-26) → `1.0.0.1` → `1.0.0.2` → `1.0.1.0` → `1.0.1.1` → `1.0.1.2` → `1.0.1.3` → `1.1.0.0`(9-25) → `1.1.1.2`(9-30，**跳过了 1.1.1.0/1.1.1.1**) → `1.1.1.3`…`1.1.1.8`(10-17) → `1.2.0.0`(10-31)。四位号的第三位每次改动都很小（多在 0/1 间跳），第四位单调递增——和 `csproj:37-41` 里"1.1.1.7 用了十几版"的自述吻合。
5. **无 CI、无自动测试**：没有 `.github/`、没有测试工程；质量靠注释里记录的现场事故驱动（如 `Helpers\StaticOverloadFinder.cs:8-11` 的"地震提醒从来没弹出来过"、`Models\ImageHeader.cs:9-13` 的"2026-10-11 内存顶到看门狗"、`Views\BackdropLayer.cs:69-74` 的"闪退元凶"）。这些注释是这份代码最有价值的"工程史"。

---

## 6. 问题清单

按影响排序，每条给 `文件:行号`。

1. **[高] `apiVersion: 2.0.0.0` 与 SDK 2.1.0.1 不一致**——`manifest.yml:10` 声明宿主只需 API 2.0.0.0，工程却按 `ClassIslandPluginSdkVersion 2.1.0.1` 编译（`LargeScreenClockRevived.csproj:16`）。若 apiVersion 的语义是"插件要求的最低宿主 API"，那么在 2.0.x 宿主上安装时清单校验通过，但一旦用到 2.1 新增 API 就是 `MissingMethodException`/`TypeLoadException`。本机 NuGet 缓存里只有 `classisland.pluginsdk 1.7.106.2-dev-v2`（无 2.1.0.1），也说明本地开发环境与工程声明已经漂移。**建议**：把 apiVersion 提到与 SDK 一致，或显式记录"只用到 2.0 API"的理由。
2. **[高] 不认识的图片格式会绕回"按屏宽放大解码"的老路**——`Models\ImageHeader.cs:14` 认不出就返回 `null`，`Models\BackdropImageSet.cs:63` 于是 `native = pixels`（按层物理像素解码）；`Settings\SettingsPage.axaml.cs:374` 的过滤器只认扩展名，一个 AVIF/HEIC/TIFF/JXL 改名成 `.png` 就能选进来。结果正是 `Models\ImageHeader.cs:9-13` 要避免的"小图放大着解，内存翻倍"，也是 `Models\BackdropImageSet.cs:11-12` 记录的看门狗死因。更糟的是失败是静默的：`Models\BackdropImageSet.cs:78-81` 吞异常返回空套 → 纯色底，而设置页仍提示"已复制到插件配置目录"（`Settings\SettingsPage.axaml.cs:384`）。
3. **[中] "一般"计数色忽略明暗参数，浅底上不可读**——`Models\SemanticColors.cs:16`：`CountNormalHex(bool light) => "#FFDE02"`，参数完全没用；对比 `Models\SemanticColors.cs:19` 的吵闹色是分明暗两版的。白底（Light 调色板 `BackgroundColor #FFFFFF`，`Models\ClockTheme.cs:163`）上 `#FFDE02` 对比度约 1.4:1，注释自己写了"#FFDE02 当文字放白底上对比度不足，改前先看真机"（`Models\SemanticColors.cs:14`）。调用方甚至已经备了两支 brush（`ViewModels\FullScreenClockViewModel.cs:48-51`），但两支同色。
4. **[中] 构建期改写产物清单 + 无 Inputs/Outputs 的打包目标**——`LargeScreenClockRevived.csproj:91-108` 每次 build 都重拷并盖号；缓存 SDK 的 `CreateCipx` 是 `AfterTargets="Build"` 且无 `Inputs/Outputs`，所以空 build 也会涨序号、重打 cipx、重算 MD5。`devbuild.txt`（`:46`）落在工程根目录且仓库无 `.gitignore` → 构建污染工作区；只读检出或并行构建时 `File.WriteAllLines`（`:80`）会直接把构建打挂。
5. **[中] cipx 无文件白名单，整目录压缩**——缓存 SDK targets 的 `ZipDirectory SourceDirectory="$(OutputPath)"`：Debug 构建的 PDB、残留文件、甚至本地临时文件都会进包；`RemoveDir`+`MakeDir` 每次都清空 `cipx/`（同 targets）。发布质量完全依赖"构建前目录是干净的"这一口头约定。
6. **[中] 五位内部版号是"特性也是分发陷阱"**——`LargeScreenClockRevived.csproj:37-41` 明说五位号会被 ClassIsland 更新检查的 `Version.TryParse` 跳过。设计意图是让内部包流不出去，但默认 `CreateCipx=true`（`:7`）意味着**任何人 clone 后 `dotnet build` 得到的第一个 cipx 就是五位号开发包**；它一旦被分发，用户永远收不到更新提示（因为每次新版本仍是五位号，旧包也解析不出来）。缺少把开发包与 `-p:DevBuild=0` 正式包区分开的文件名/目录标记。
7. **[中] 模糊半径换算只用宽度比，并且会被静默截断**——`Models\BackgroundImageMap.cs:106-111` 返回 `imageSize.Width / dest.Width`，文档却说"必须按实际落位算"；`Stretch` 模式下 `sx≠sy`，纵向误差没有体现。另 `Models\BackdropImageSet.cs:141` 把结果硬钳到 `StackBlur.MaxRadius = 88`，极端长条图的用户滑块值（0~64，`Settings\SettingsPage.axaml:513`）与实际模糊半径脱钩，用户在界面上看不出这件事。
8. **[低] 半径取整为 0 时仍返回"模糊位图"，磨砂档会露出清晰图**——`Models\BackdropImageSet.cs:203` 在 `radius < 1` 时直接返回，但 `BakeBlur` 已经把这张未模糊的副本赋给 `set.Blur`（`:158-162`）；`Views\BackdropLayer.cs:232` 的 `HasBlurredBackdrop` 只看 `Blur is not null` → 玻璃画的是"清晰图的裁切"，`Views\ClockSurface.axaml.cs:731-735` 也会据此给关于窗口加 `BlurEffect`。触发条件：小图 + 小半径（`ImagePixelsPerViewportPixel` 小于 1 时）。
9. **[低] `BackdropLayer` 的字段默认值与设置的默认值不一致**——`Views\BackdropLayer.cs:53` `_refraction = 55`，而 `Models\PluginSettings.cs:53` `_glassRefraction = 50`。首次 `Apply` 之前（或预览控件刚建、还没套设置的那几帧）玻璃参数偏一档。同类小漂移：`Views\BackdropLayer.cs:51-54` 的 `_glassOpacity 35 / _blurRadius 24 / _frost 45` 与设置默认一致，只有 refraction 不同，更像手改后忘了同步。
10. **[低] 两处"半径上限"各自演化**——`Helpers\StackBlur.cs:26` 的 `MaxRadius = 88` 与设置 UI 上限 64（`Settings\SettingsPage.axaml:513`）/ 设置钳制 0~64（`Models\PluginSettings.cs:274-277`）之间没有任何引用关系；将来若把滑块上限提到 100，`MaxRadius` 会毫无提示地开始截断。
11. **[低] 预警等级用中文枚举字符串匹配**——`Models\SemanticColors.cs:38-49` 用 `"蓝色"/"黄色"/"橙色"/"红色"` 做 switch，上游（`Services\CiWeatherReader.cs`）文案一改（如加"白色预警"或改成"蓝"），整片颜色会静默掉回 `null` → 正文色。
12. **[低] 亮度读取假设像素格式**——`Models\BackgroundImageStore.cs:121-151` 直接按 BGRA 拆字节，没有校验 `frame.Format`。当前仅 Windows/`Bgra8888`，风险低，但这类"隐式格式假设"正是 `Views\BackdropLayer.cs:1209-1215` 里专门写 `Unpremultiply` 想避免的同类问题。
13. **[低] 迁移哨兵可能吃掉自定义色**——`Helpers\ClockThemeApplier.cs:62` 只以 `AppliedPaletteVariant == VariantUnknown` 判断"老配置"，并用 `Capture(VariantDark)`（`:64`）覆盖 Dark 槽。若出现"主题功能已存在但字段缺失"的配置文件（手工编辑、降级后升级、或从别人的机器拷 Settings.json），用户当前那套颜色会被强行定为 Dark 槽，之后再切主题就可能丢失原来 Dark 槽里那套。
14. **[低] "完整显示"档的虚化补边与 `wantBlur` 有两处重复定义**——`Views\BackdropLayer.cs:182-183`、`:1226-1228` 与 `Services\BackdropPreloadService.cs:110-111` 三处各写一遍"磨砂/折射或 Fit 才要模糊"。目前三处一致，但这是典型的"改一处忘两处"结构（`BackdropPreloadService.cs:109` 的注释也承认是在"保持一致"）。
15. **[低] 设置页选中图片后不做可解码性校验**——`Settings\SettingsPage.axaml.cs:383-384`：先 `Replace` 复制、再无条件提示成功；不支持/损坏的图要等大屏点亮才发现是纯色底。建议先 `ImageHeader.ReadSize` + 一次缩略图解码校验再复制。

### 不算问题、但值得记录的边界处理（避免后续误判为 bug）

- `Views\BackdropLayer.cs:860-865`：层像素 > 4000 万直接放弃折射，并把 `_glassDirty` 置 false，避免"永远在等新图"；此时玻璃不画是预期行为。
- `Views\BackdropLayer.cs:1230-1233`：尺寸 < 2 时推迟烘焙（等 `ArrangeOverride`），不是漏烘；`Views\BackdropLayer.cs:350-353` 特意用 `finalSize` 而不是 `Bounds`，因为排布中 `Bounds` 还是上一轮的。
- `Views\BackdropLayer.cs:238-243`、`:1308-1310`：在 Render 里命中预烘缓存时改用 `Dispatcher.UIThread.Post` 通知，否则 "Visual was invalidated during the render pass" 会被 CI 判成插件严重错误并禁用插件。
- `Helpers\OffsetSampler.cs:14-16`：钳制一律在内层采样器做，不在偏移层做——这是"玻璃块之间不能出现假接缝"的结构性要求，不是风格问题。
- `Models\BackgroundImageStore.cs:48-66`：先临时文件再改名、最后删旧图；这是"换图时源文件读不了会连旧图都没了"的事故修复。
- `Services\BackdropPreloadService.cs:41-51`：设置变更只清缓存不立即重烘，等下一轮 20 秒轮询再判断，避免拖滑块时反复烘几百兆。
