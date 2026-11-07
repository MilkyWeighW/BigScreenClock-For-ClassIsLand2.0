# 大屏时钟（BigScreenClock）

ClassIsland 2.1 插件：在教室大屏上全屏显示时间、教室分贝、课程信息与提醒，并按课程统计吵闹次数。

![大屏时钟预览](icon.png)

## 功能

* **时钟与日期**：全屏显示时间（等宽数字、秒不抖动）、日期与星期。
* **教室音量**：麦克风实时分贝显示，安静 / 良好 / 一般 / 吵闹 / 嘈杂五档分级；支持按课程统计吵闹次数（可设起算底线、回落窗口、上课初期保护、吵闹计数规则）。
* **课程信息**：当前课程、下节课与打铃时间、课程进度条；课后自动隐藏。
* **提醒与预警**：降雨、倒计时、文本提醒三档合并显示；CI 天气预警、摄像头占用提醒、地震预警。
* **组件化布局**：13 个可独立开关的组件（时钟 / 日期 / 天气 / 降雨 / 倒计时 / 文本 / 预警详情 / 提醒面板 / 音量条 / 吵闹计数 / 课程信息 / 进度条 / 地震警告）。每个组件可单独调整大小、水平/垂直位置与先后顺序，关掉的组件不占位；设置页顶部组件条可多行显示、点击即出该组件全部设置。
* **三种背景**：提取当前壁纸 / 纯色 / 自定义图片，默认提取当前壁纸。壁纸支持景深放大（1.0~1.6）与模糊（半径 0~64），已解码模糊的壁纸会缓存，重进大屏不再卡顿。
* **开屏过渡**：每次显示都重演——先停留压暗的桌面截图，再交叉淡入背景（壁纸还会缓缓推近），同时 UI 组件淡入；退出时窗口淡出。动画速率可调（0.5×~2.0×）。
* **背景明暗**：-100~100 的遮罩正好压暗亮壁纸，保证文字可读；地震进入时整屏压暗。
* **防御与恢复**：防休眠（25 分钟自动续期）、最小化后暂停（可设时长）、麦克风看门狗自动重连、内存与烘焙预算保护、崩溃后不拖垮 ClassIsland。
* **一键重置**：设置页「运行」页顶部可一键恢复默认设置（二次确认）。

## 环境要求

* Windows 10 1809（build 17763）或更新版本
* ClassIsland **2.1** 或更新版本（`manifest.yml` 的 `apiVersion: 2.1.0.0`）
* 构建：.NET SDK 8.0+（当前用 10.0.301 验证）、PowerShell 7

## 依赖

`EveningSelfStudyClock.csproj` 直接声明的 NuGet 包：

| 包 | 版本 | 用途 |
| --- | --- | --- |
| ClassIsland.PluginSdk | 2.1.0.1 | 插件 SDK（编译期引用，运行时由 ClassIsland 提供） |
| NAudio | 2.2.1 | 麦克风采样（`NAudio.Wave`） |
| Microsoft.Windows.CsWin32 | 0.3.106 | 生成 Windows API P/Invoke（摄像头检测、截屏、壁纸、置顶） |
| Avalonia.Controls.ColorPicker | 11.3.17 | HSV 取色器（版本需与宿主 Avalonia 一致） |

Avalonia、`Microsoft.Extensions.*` 等由 PluginSdk 传递引入，不单独声明；运行时插件包只需 ClassIsland 自带组件，不需要用户另装 NuGet 包。

## 构建与打包

```powershell
pwsh -File ./build.ps1
```

默认构建对外发布版（等价 `dotnet build -c Release -t:Rebuild -p:DevBuild=0`）。

| 参数 | 说明 |
| --- | --- |
| （默认） | 对外发布版，四位版本号（当前 1.2.0.0） |
| `-Configuration <Debug\|Release>` | 构建配置，默认 `Release` |
| `-Dev` | 内部开发版，输出五位版本号（如 1.2.0.0.1），写入 `devbuild.txt` |
| `-Release` | 显式对外发布版（与 `-Dev` 互斥） |
| `-NoRestore` | 跳过 `dotnet restore`，构建加 `--no-restore` |
| `-Clean` | 构建前 `dotnet clean` |
| `-Help` | 显示帮助 |

产物：

* `bin/<Configuration>/net8.0-windows/EveningSelfStudyClock.dll` —— 插件程序集
* `bin/<Configuration>/net8.0-windows/manifest.yml` —— 已盖号的运行时清单
* `cipx/EveningSelfStudyClock.cipx` —— 可直接安装 / 发布的插件包（`checksums.md` 记录包内文件 MD5）

说明：构建通过命名互斥量 `Global\bsclock-build` 串行，多个窗口同时构建会自动排队；发布链路是「本机构建 → 手动上传 cipx」，仓库不含 CI。构建时需要写 `obj/`、`bin/`、`cipx/`、`devbuild.txt`，请勿在只读目录构建。

## 版本号与 apiVersion

1. `manifest.yml` 的 `version` 是四位对外版本号（当前 1.2.0.0）。
2. 普通开发构建会把输出目录的 `manifest.yml` 与 `cipx` 盖成五位版本号 `1.2.0.0.N`（N 存在 `devbuild.txt`，已 gitignore）。五位号 ClassIsland 能加载，但更新检查用 `Version.TryParse` 会跳过，因此不会当正式更新分发。
3. 只有 `-p:DevBuild=0`（`build.ps1` 默认）才产出四位对外版本。

`apiVersion` 表示插件要求的最低宿主 API 版本，应与编译所用 SDK 对应：当前按 **PluginSdk 2.1.0.1** 编译，`manifest.yml` 为 **apiVersion: 2.1.0.0**，即要求 ClassIsland 2.1+；2.0.x 会被拒绝加载。升级 SDK 时请同步修改 `apiVersion`。

## 离线构建

依赖 4 个直接 NuGet 包及传递依赖，首次构建需联网还原。断网时：

1. 在能联网的机器上 `dotnet restore`，把 `%USERPROFILE%\.nuget\packages` 拷到本机同位置，然后 `pwsh -File ./build.ps1 -NoRestore`。
2. 在仓库根放 `NuGet.config` 指向内网源或本地文件夹源，再正常运行 `build.ps1`。
3. 或临时指定源：`dotnet restore --source D:\nuget-packages`。

构建失败时脚本会打印离线提示；日志里的 `NU1101` / `NU1301` 一般是包源问题而非源码编译错误。

## 致所有用户的一封信

> 从这个版本（1.2.0.0）开始，大屏时钟插件不再添加新功能。这个项目最初只是为了给晚自习教室做一块安静的大屏时钟，后来一点点长出了音量统计、提醒、组件化这些功能。感谢每一位使用、反馈、帮忙排查问题的老师和同学。
>
> 愿你的教室安静一点，时间过得从容一点。
>
> —— sokumei，2026/10/31

## 作者与许可

* 作者：sokumei
* 项目主页：https://github.com/Human-air/BigScreenClock-For-ClassIsLand2.0
* 许可证：GNU General Public License v3.0（详见 [LICENSE](LICENSE)）
* 免责声明：本插件为第三方 ClassIsland 插件，与 ClassIsland 官方无关。插件按「现状」提供，不附带任何担保；因使用本插件造成的课程信息错误、数据丢失或其他损失由使用者自行承担。
