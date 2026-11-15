# 大屏时钟（LargeScreenClockRevived）

在教室大屏上全屏显示时间、教室分贝、课程信息与提醒，并按课程统计吵闹次数。
解耦化 + 轻量化

## 功能

* **时钟与日期**：全屏时间（等宽数字、秒不抖动）、日期与星期；时间优先跟随 ClassIsland 的时间偏移，拿不到时回退系统时间。
* **教室音量**：实时分贝与静 / 良好 / 一般 / 吵闹 / 嘈杂五档；可按课程统计吵闹次数（起算底线、回落窗口、上课初期保护、计数规则）。
* **课程信息**：当前课、下节课与打铃时间、课程进度条；课后自动隐藏。
* **提醒与预警**：降雨、倒计时、文本提醒；CI 天气预警、摄像头占用提醒、地震预警。
* **组件化布局**：13 个可开关组件，各自调大小、长宽比、位置、顺序与不透明度，关掉不占位；每个组件还能单独设字体（族 / 字号 / 字重）与文字材质——材质直接作用在**字形填充**上（磨砂 / 液态玻璃），不是给文字垫一块外框。
* **默认隐藏重合信息**：天气、日期、倒计时、降雨、文本提醒与左上角提醒面板与 ClassIsland 主界面/课表栏重合，新装与升级后都默认隐藏，可在组件页逐项打开。
* **多贴纸**：图片统一由「贴纸」实例承载，一个实例一张图（班徽、logo、值日表……），各自大小 / 位置 / 不透明度，互不影响；旧的单张「自定义图片」已并入贴纸，不再有两个入口。
* **五种背景**：提取当前壁纸 / 纯色 / 自定义图片 / 原生实时模糊（DWM）/ 网页（Edge WebView2）。网页可以直接填网址，也可以**选择一个本地 .html 文件**（file:// 打开，css/js/图片和它放同一目录即可）。壁纸支持景深放大与模糊，解码结果带缓存，长时运行会主动回收位图；WebView2 运行时不在了会自动禁用网页背景并提示。
* **主题色跟随**：单向读取 ClassIsland 的强调色（未手动改过进度条色时接管进度条），CI 不可用时回退本插件主题色。
* **设置页实时预览**：右栏预览按最终输出分辨率的 40% 渲染，与大屏共用同一份布局与 ViewModel，改设置即时可见。
* **开屏过渡**：每次显示都重演——压暗的桌面截图 → 交叉淡入背景（壁纸缓缓推近）→ 组件淡入；退出淡出，速率可调。
* **防御与恢复**：防休眠、最小化暂停、麦克风看门狗、内存保护；一键恢复默认设置。
* **始终置顶**：大屏窗口保持全屏与置顶，ClassIsland 顶部课表栏压在其上。

## 环境要求

* Windows 10 1809（17763）或更新版本
* ClassIsland 2.1+（`manifest.yml` 的 `apiVersion: 2.1.0.0`）
* 构建：.NET SDK 8.0+、PowerShell 7

## 依赖

`LargeScreenClockRevived.csproj` 直接声明的 NuGet 包：

| 包 | 版本 | 用途 |
| --- | --- | --- |
| ClassIsland.PluginSdk | 2.1.0.1 | 插件 SDK（运行时由 ClassIsland 提供） |
| NAudio.WinMM | 2.2.1 | 麦克风采样（WASAPI/ASIO/MIDI 后端不需要，故只引 WinMM 与传递的 Core） |
| Microsoft.Windows.CsWin32 | 0.3.106 | 生成 Windows API P/Invoke（摄像头、截屏、壁纸、置顶） |
| Avalonia.Controls.ColorPicker | 11.3.17 | HSV 取色器 |
| Microsoft.Web.WebView2 | 1.0.4314.35 | 网页背景（托管封装 + 原生 Loader；运行时由系统的 Edge WebView2 提供） |

Avalonia、`Microsoft.Extensions.*` 由 PluginSdk 传递引入，插件包运行时不需要用户另装。
网页背景需要系统已安装 Edge WebView2 运行时（Win10/11 一般自带）；没有时插件其余功能不受影响，网页背景选项会置灰并给出原因。

## 构建与打包

```powershell
pwsh -File ./build.ps1
```

默认打对外发布版（四位版本号）；`-Dev` 出五位开发版，`-NoRestore` 跳过还原，`-Clean` 先清理，`-Help` 查看全部参数。构建用命名互斥量 `Global\bsclock-build` 串行，发布链路是「本机构建 → 手动上传 cipx」，仓库不含 CI。

产物：

* `bin/<Configuration>/net8.0-windows/LargeScreenClockRevived.dll` —— 插件程序集
* `bin/<Configuration>/net8.0-windows/manifest.yml` —— 已盖号的运行时清单
* `cipx/LargeScreenClockRevived.cipx` —— 可直接安装 / 发布的插件包

`manifest.yml` 的 `version` 为对外版本（当前 `1.4.0.0`）；`apiVersion` 跟随编译所用 SDK（当前 PluginSdk 2.1.0.1 → `2.1.0.0`），升级 SDK 时同步修改。开发版五位号（`1.4.0.0.N`）宿主可加载，但更新检查不认，不会当正式更新分发。

## 说明

1.4.0.0 是对既有能力的补齐与稳定化（背景 / 组件 / 主题色 / 时间来源 / 设置页预览 / 内存治理），不改变下面这封信的结论：这个插件仍然以「稳定可用」为先，不追新功能。

## 致所有用户的一封信

> 从这个版本开始，大屏时钟插件不再添加新功能。这个项目最初只是给晚自习教室做一块安静的大屏时钟，后来一点点长出了音量统计、提醒、组件化这些能力。感谢每一位使用、反馈、帮忙排查问题的老师和同学。
>
> 愿你的教室安静一点，时间过得从容一点。
>
> —— sokumei，2026/10/31

## 作者与许可

* 作者：sokumei、MilkyWeighW
* 项目主页：https://github.com/MilkyWeighW/LargeScreenClockRevived
* 许可证：GNU General Public License v3.0（详见 [LICENSE](LICENSE)）
* 免责声明：本插件为第三方 ClassIsland 插件，与 ClassIsland 官方无关；按「现状」提供，不附带任何担保。
