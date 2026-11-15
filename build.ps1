#Requires -Version 7.0
<#
.SYNOPSIS
    大屏时钟（LargeScreenClockRevived）插件构建脚本。

.DESCRIPTION
    统一封装本项目（net8.0-windows）的 NuGet 还原、构建、插件打包与产物定位流程：
      * 默认构建「对外发布版」：等价于 csproj 注释里的
        dotnet build -c Release -t:Rebuild -p:DevBuild=0
      * -Dev 构建「内部开发版」：版本号被 csproj 的 StampDevBuild 盖成五位递增号。
      * 整个构建过程会获取本机命名互斥量 Global\bsclock-build，多人/多窗口同时构建
        同一 obj 时会自动排队，避免写写竞争。
      * 构建失败时打印离线构建方法（本地 NuGet 缓存 / 私有源）。
    脚本只依赖 PowerShell 7 与 .NET SDK 8+，可在仓库根目录之外通过 -File 调用。

.PARAMETER Configuration
    构建配置，默认 Release（也可用 Debug）。

.PARAMETER Dev
    构建内部开发版：不传 -p:DevBuild=0，csproj 会把版本号盖成四位版本 + 递增序号
    （例如 1.2.0.0.1），并在工程根目录维护 devbuild.txt。

.PARAMETER Release
    显式构建对外发布版（默认行为，等价 -p:DevBuild=0）。与 -Dev 互斥。

.PARAMETER NoRestore
    跳过 dotnet restore，构建时加 --no-restore（要求此前已还原过 NuGet 包）。

.PARAMETER Clean
    构建前先执行 dotnet clean 清理当前配置的输出，然后再正常构建。

.PARAMETER Help
    显示帮助并退出。

.EXAMPLE
    pwsh -File ./build.ps1
    构建对外 Release 版（干净重建），打印 dll / manifest / cipx 路径与版本号。

.EXAMPLE
    pwsh -File ./build.ps1 -Dev
    构建内部开发版（五位版本号）。

.EXAMPLE
    pwsh -File ./build.ps1 -NoRestore
    已还原过依赖，跳过 restore 直接构建。
#>

[CmdletBinding()]
param(
    [string]$Configuration = 'Release',
    [switch]$Dev,
    [switch]$Release,
    [switch]$NoRestore,
    [switch]$Clean,
    [switch]$Help
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$LASTEXITCODE = 0

# ===========================================================================
# 基本路径与常量（全部从脚本所在目录推导，不写死任何机器路径）
# ===========================================================================

# 命名互斥量：本机所有构建串行执行，避免并发编译写坏同一个 obj
$script:MutexName = if ($IsWindows) { 'Global\bsclock-build' } else { 'bsclock-build' }

# 目标框架（与 csproj 的 <TargetFramework> 保持一致，仅用于定位 bin 下的输出目录）
$script:TargetFramework = 'net8.0-windows'

# 目标框架要求的最低 .NET SDK 主版本
$script:MinSdkMajor = 8

# 脚本所在目录
$script:ScriptDir = $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($script:ScriptDir)) {
    $script:ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
}
if ([string]::IsNullOrWhiteSpace($script:ScriptDir)) {
    $script:ScriptDir = (Get-Location).Path
}
$script:ScriptDir = (Resolve-Path -LiteralPath $script:ScriptDir).Path

# ===========================================================================
# 输出工具函数
# ===========================================================================

function Write-Step {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Write-Ok {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "  [OK] $Message" -ForegroundColor Green
}

function Write-Warn {
    param([Parameter(Mandatory = $true)][string]$Message)
    Write-Host "  [警告] $Message" -ForegroundColor Yellow
}

# 从 manifest.yml 读取某个字段（version / apiVersion 等）
function Read-ManifestField {
    param(
        [Parameter(Mandatory = $true)][string]$ManifestPath,
        [Parameter(Mandatory = $true)][string]$FieldName
    )
    if (-not (Test-Path -LiteralPath $ManifestPath -PathType Leaf)) { return $null }
    $pattern = '^\s*' + [regex]::Escape($FieldName) + '\s*:\s*(.+?)\s*$'
    foreach ($line in [System.IO.File]::ReadAllLines($ManifestPath)) {
        $match = [regex]::Match($line, $pattern)
        if ($match.Success) { return $match.Groups[1].Value.Trim('"', "'") }
    }
    return $null
}

# 失败提示：无网络 / NuGet 源不可达时怎么办
function Show-OfflineHint {
    Write-Host ''
    Write-Host '离线构建提示（无网络或 NuGet 源不可达时）：' -ForegroundColor Yellow
    Write-Host '  1. 复用本机 NuGet 缓存：在能联网的机器上先执行一次 dotnet restore，把'
    Write-Host '     %USERPROFILE%\.nuget\packages 整个目录拷到本机；之后运行'
    Write-Host '       pwsh -File ./build.ps1 -NoRestore'
    Write-Host '     直接构建（跳过 restore）。'
    Write-Host '  2. 配置私有源：在仓库根目录（或任意上级目录）放 NuGet.config，把 <packageSources>'
    Write-Host '     指向内网源或本地文件夹源，例如 <add key="local" value="D:\nuget-packages" />，'
    Write-Host '     再运行本脚本（不要传 -NoRestore）。'
    Write-Host '  3. 临时指定源：dotnet restore --source D:\nuget-packages 。'
    Write-Host '  4. 日志里出现 NU1101 / NU1301 等错误一般是包源问题，不是源码编译错误。'
}

# 统一失败出口：打印错误、可选离线提示，并以指定退出码结束脚本
function Stop-Build {
    param(
        [Parameter(Mandatory = $true)][string]$Message,
        [int]$ExitCode = 1,
        [switch]$ShowOfflineHint
    )
    Write-Host ''
    Write-Host "? $Message" -ForegroundColor Red
    if ($ShowOfflineHint) { Show-OfflineHint }
    exit $ExitCode
}

# 把参数列表拼成可复制的命令行（含空格的参数加引号）
function Format-CommandLine {
    param([Parameter(Mandatory = $true)][string[]]$Arguments)
    $quoted = foreach ($argument in $Arguments) {
        if ($argument -match '\s') { '"' + $argument + '"' } else { $argument }
    }
    return ($quoted -join ' ')
}

# 执行 dotnet 子命令：实时回显输出、累积日志、返回退出码
function Invoke-DotnetCli {
    param(
        [Parameter(Mandatory = $true)][string[]]$Arguments,
        [System.Collections.Generic.List[string]]$Log
    )
    & dotnet @Arguments 2>&1 | ForEach-Object {
        # PowerShell 7 里被 2>&1 合并进来的 stderr 是 ErrorRecord，统一转成文本
        $text = if ($_ -is [System.Management.Automation.ErrorRecord]) { $_.ToString() } else { [string]$_ }
        if (-not [string]::IsNullOrEmpty($text)) {
            if ($null -ne $Log) { [void]$Log.Add($text) }
            Write-Host $text
        }
    }
    return $LASTEXITCODE
}

# 显示帮助（版本号示例从 manifest.yml 动态读取，避免和工程实际版本不一致）
function Show-Help {
    $baseVersion = Read-ManifestField -ManifestPath (Join-Path $script:ScriptDir 'manifest.yml') -FieldName 'version'
    if ([string]::IsNullOrWhiteSpace($baseVersion)) {
        $devExample = '<四位版本>.1'
    } else {
        $devExample = "${baseVersion}.1"
    }

    Write-Output @"
大屏时钟构建脚本（build.ps1）

用法：
  pwsh -File ./build.ps1 [参数]

参数：
  -Configuration <配置>   构建配置，默认 Release（也可用 Debug）
  -Dev                    构建内部开发版：版本号在四位版本后追加递增序号（如 ${devExample}）
  -Release                显式构建对外发布版（默认行为，等价 dotnet build -c Release -t:Rebuild -p:DevBuild=0）
  -NoRestore              跳过 dotnet restore，直接构建（要求此前已还原过 NuGet 包）
  -Clean                  构建前先执行 dotnet clean 清理当前配置输出
  -Help                   显示本帮助并退出

示例：
  pwsh -File ./build.ps1                         对外发布版（默认，干净重建）
  pwsh -File ./build.ps1 -Dev                    内部开发版（五位版本号，如 ${devExample}）
  pwsh -File ./build.ps1 -NoRestore              已还原过，跳过 restore 直接构建
  pwsh -File ./build.ps1 -Clean                  先 dotnet clean 再构建
  pwsh -File ./build.ps1 -Configuration Debug -Dev

产物：
  bin/<Configuration>/net8.0-windows/LargeScreenClockRevived.dll   插件程序集
  bin/<Configuration>/net8.0-windows/manifest.yml                 已盖号的运行时清单
  cipx/LargeScreenClockRevived.cipx                                 插件包（同级 checksums.md 记录 MD5）

说明：
  * 需要 .NET SDK 8 或更高版本（目标框架 net8.0-windows）。
  * -Dev 与 -Release 互斥，不能同时使用。
  * 构建通过命名互斥量 ${script:MutexName} 串行执行，多个构建同时跑时会自动排队。
  * 构建失败时会打印离线构建方法（本地 NuGet 缓存 / 私有源）。
"@
}

# ===========================================================================
# 0. 帮助
# ===========================================================================
if ($Help) {
    Show-Help
    exit 0
}

# ===========================================================================
# 1. 参数校验
# ===========================================================================
if ($Dev -and $Release) {
    Stop-Build -Message '-Dev 与 -Release 不能同时使用：-Release 是默认行为，-Dev 用于内部开发版。'
}
if ([string]::IsNullOrWhiteSpace($Configuration)) {
    Stop-Build -Message '-Configuration 不能为空（默认 Release，也可用 Debug）。'
}

# ===========================================================================
# 2. 定位工程文件
# ===========================================================================
$csprojFiles = @(Get-ChildItem -LiteralPath $script:ScriptDir -Filter '*.csproj' -File -ErrorAction SilentlyContinue)
if ($csprojFiles.Count -eq 0) {
    Stop-Build -Message "在脚本所在目录找不到 .csproj：$($script:ScriptDir)"
}
if ($csprojFiles.Count -gt 1) {
    Stop-Build -Message "脚本所在目录存在多个 .csproj，无法确定构建目标：$(($csprojFiles.Name) -join ', ')"
}

$projectPath = $csprojFiles[0].FullName
$projectName = [System.IO.Path]::GetFileNameWithoutExtension($csprojFiles[0].Name)
# 程序集名与工程名不同（csproj 里 AssemblyName 带连字符），产物定位统一用这个
$assemblyName = 'LargeScreenClockRevived'

Write-Host ''
Write-Host '大屏时钟构建脚本' -ForegroundColor White
Write-Host ("  工程目录 : {0}" -f $script:ScriptDir)
Write-Host ("  工程文件 : {0}" -f $projectPath)
Write-Host ("  构建配置 : {0}" -f $Configuration)
if ($Dev) {
    Write-Host '  版本模式 : 内部开发版（五位版本号，写入 devbuild.txt）'
} else {
    Write-Host '  版本模式 : 对外发布版（四位版本号，-p:DevBuild=0）'
}

# ===========================================================================
# 3. 检查 .NET SDK
# ===========================================================================
Write-Step '检查 .NET SDK'

$dotnetCommand = Get-Command -Name 'dotnet' -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $dotnetCommand) {
    Stop-Build -Message '.NET SDK 未安装或 dotnet 不在 PATH 中。请安装 .NET SDK 8 或更高版本：https://dotnet.microsoft.com/download'
}

$dotnetVersionText = ''
try {
    $dotnetVersionText = ((& dotnet --version 2>&1 | Out-String).Trim() -split "`r?`n" | Where-Object { $_ -ne '' } | Select-Object -First 1)
} catch {
    Stop-Build -Message "无法执行 dotnet --version：$($_.Exception.Message)"
}
if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($dotnetVersionText)) {
    Stop-Build -Message "无法获取 dotnet 版本（退出码 $LASTEXITCODE），请检查 .NET SDK 安装。"
}

$sdkVersion = $null
if ([version]::TryParse($dotnetVersionText.Trim(), [ref]$sdkVersion)) {
    if ($sdkVersion.Major -lt $script:MinSdkMajor) {
        Stop-Build -Message "当前 .NET SDK 版本为 $dotnetVersionText，目标框架 $($script:TargetFramework) 需要 SDK 主版本 >= $($script:MinSdkMajor)。"
    }
    Write-Ok ("dotnet {0}（目标框架 {1}）" -f $dotnetVersionText.Trim(), $script:TargetFramework)
} else {
    Write-Warn "无法解析 dotnet 版本号「$dotnetVersionText」，跳过 SDK 版本下限检查。"
}

# ===========================================================================
# 4. 串行执行：清理（可选）→ 还原（可选）→ 构建
# ===========================================================================
$mutex = [System.Threading.Mutex]::new($false, $script:MutexName)
$mutexHeld = $false
$buildLog = [System.Collections.Generic.List[string]]::new()

try {
    Write-Step "等待构建锁 $($script:MutexName)"
    try {
        # 最长等待 30 分钟，避免别的构建挂死时本脚本永久卡住
        if (-not $mutex.WaitOne([TimeSpan]::FromMinutes(30))) {
            Stop-Build -Message '等待构建锁超过 30 分钟，已放弃本次构建。'
        }
    } catch [System.Threading.AbandonedMutexException] {
        # 上一个持锁进程异常退出：锁已被本进程接管
        Write-Warn '检测到上一次构建异常退出留下的构建锁，本次已接管。'
    }
    $mutexHeld = $true
    Write-Ok '已获得构建锁'

    # 4.1 可选清理
    if ($Clean) {
        Write-Step "清理 $Configuration 配置输出（dotnet clean）"
        $cleanArgs = @('clean', $projectPath, '-c', $Configuration, '--nologo', '-v:minimal')
        Write-Host ('  dotnet ' + (Format-CommandLine -Arguments $cleanArgs))
        $cleanExit = Invoke-DotnetCli -Arguments $cleanArgs -Log $buildLog
        if ($cleanExit -ne 0) {
            Stop-Build -Message "dotnet clean 失败（退出码 $cleanExit）。" -ExitCode $cleanExit -ShowOfflineHint
        }
        Write-Ok '清理完成'
    }

    # 4.2 可选还原
    if (-not $NoRestore) {
        Write-Step '还原 NuGet 包（dotnet restore）'
        $restoreArgs = @('restore', $projectPath, '--nologo', '-v:minimal')
        Write-Host ('  dotnet ' + (Format-CommandLine -Arguments $restoreArgs))
        $restoreExit = Invoke-DotnetCli -Arguments $restoreArgs -Log $buildLog
        if ($restoreExit -ne 0) {
            Stop-Build -Message "NuGet 还原失败（退出码 $restoreExit），无法继续构建。" -ExitCode $restoreExit -ShowOfflineHint
        }
        Write-Ok 'NuGet 还原完成'
    } else {
        Write-Host '  已指定 -NoRestore，跳过 NuGet 还原。'
    }

    # 4.3 构建
    # 默认（对外版）用 -t:Rebuild 保证干净输出，与 csproj 注释里的命令一致；
    # 指定 -Clean 时已经 clean 过，改用普通 -t:Build。
    $buildArgs = [System.Collections.Generic.List[string]]::new()
    $buildArgs.Add('build')
    $buildArgs.Add($projectPath)
    $buildArgs.Add('-c')
    $buildArgs.Add($Configuration)
    if ($Clean) {
        $buildArgs.Add('-t')
        $buildArgs.Add('Build')
    } else {
        $buildArgs.Add('-t')
        $buildArgs.Add('Rebuild')
    }
    if ($Dev) {
        # 开发版：不传 DevBuild=0，csproj 会按 devbuild.txt 盖五位号
        $buildArgs.Add('-p:DevBuild=1')
    } else {
        # 对外发布版：DevBuild=0 关闭盖号
        $buildArgs.Add('-p:DevBuild=0')
    }
    $buildArgs.Add('--nologo')
    $buildArgs.Add('-v:minimal')
    if ($NoRestore) {
        $buildArgs.Add('--no-restore')
    }

    Write-Step '开始构建（dotnet build）'
    Write-Host ('  dotnet ' + (Format-CommandLine -Arguments $buildArgs.ToArray()))
    $buildExit = Invoke-DotnetCli -Arguments $buildArgs.ToArray() -Log $buildLog
    if ($buildExit -ne 0) {
        if ($NoRestore -and ($buildLog | Where-Object { $_ -like '*NETSDK1004*' })) {
            Write-Host ''
            Write-Host '提示：-NoRestore 但缺少 NuGet 资产文件（NETSDK1004），请先不带 -NoRestore 运行一次。' -ForegroundColor Yellow
        }
        Stop-Build -Message "构建失败（dotnet build 退出码 $buildExit）。" -ExitCode $buildExit -ShowOfflineHint
    }
} finally {
    if ($mutexHeld) {
        try { [void]$mutex.ReleaseMutex() } catch { }
    }
    $mutex.Dispose()
}

# ===========================================================================
# 5. 定位产物并打印绝对路径与版本号
# ===========================================================================
Write-Step '定位构建产物'

$outputBase = Join-Path (Join-Path $script:ScriptDir 'bin') $Configuration
$tfmDir = Join-Path $outputBase $script:TargetFramework
$dllPath = Join-Path $tfmDir "$assemblyName.dll"
$manifestPath = Join-Path $tfmDir 'manifest.yml'

# 兜底：若 TargetFramework 变了，就在 bin/<Configuration> 下按名字搜索
if (-not (Test-Path -LiteralPath $dllPath -PathType Leaf)) {
    $foundDll = Get-ChildItem -LiteralPath $outputBase -Recurse -Filter "$assemblyName.dll" -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($foundDll) {
        $dllPath = $foundDll.FullName
        $manifestPath = Join-Path $foundDll.DirectoryName 'manifest.yml'
    }
}
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    $foundManifest = Get-ChildItem -LiteralPath $outputBase -Recurse -Filter 'manifest.yml' -File -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($foundManifest) { $manifestPath = $foundManifest.FullName }
}

$manifestVersion = Read-ManifestField -ManifestPath $manifestPath -FieldName 'version'
$apiVersion = Read-ManifestField -ManifestPath $manifestPath -FieldName 'apiVersion'

# csproj 的 StampManifestVersion 会打印「插件版本：x」，作为清单读不到时的兜底
$logVersion = $null
foreach ($line in $buildLog) {
    $match = [regex]::Match($line, '插件版本[：:]\s*(\S+)')
    if ($match.Success) {
        $logVersion = $match.Groups[1].Value
        break
    }
}
$displayVersion = if (-not [string]::IsNullOrWhiteSpace($manifestVersion)) { $manifestVersion } else { $logVersion }

$cipxPath = Join-Path (Join-Path $script:ScriptDir 'cipx') "$assemblyName.cipx"

Write-Host ''
Write-Host '构建成功。' -ForegroundColor Green
Write-Host '产物：'

if (Test-Path -LiteralPath $dllPath -PathType Leaf) {
    $dllItem = Get-Item -LiteralPath $dllPath
    Write-Host ("  DLL        : {0}  ({1:N0} 字节)" -f $dllItem.FullName, $dllItem.Length)
} else {
    Write-Warn "未找到程序集：$dllPath"
}

if (Test-Path -LiteralPath $manifestPath -PathType Leaf) {
    $manifestItem = Get-Item -LiteralPath $manifestPath
    Write-Host ("  输出清单   : {0}  (version: {1})" -f $manifestItem.FullName, ($manifestVersion ?? '<未知>'))
} else {
    Write-Warn "未找到输出清单：$manifestPath"
}

if (Test-Path -LiteralPath $cipxPath -PathType Leaf) {
    $cipxItem = Get-Item -LiteralPath $cipxPath
    Write-Host ("  插件包     : {0}  ({1:N0} 字节)" -f $cipxItem.FullName, $cipxItem.Length)
    $checksumsPath = Join-Path $cipxItem.DirectoryName 'checksums.md'
    if (Test-Path -LiteralPath $checksumsPath -PathType Leaf) {
        Write-Host ("  校验清单   : {0}" -f $checksumsPath)
    }
} else {
    Write-Warn "未找到 cipx 插件包：$cipxPath（可能构建时 CreateCipx 未启用）"
}

if (-not [string]::IsNullOrWhiteSpace($displayVersion)) {
    Write-Host ("  版本号     : {0}" -f $displayVersion)
} else {
    Write-Warn '未能确定插件版本号（构建日志与输出清单里都没有找到）。'
}
if (-not [string]::IsNullOrWhiteSpace($apiVersion)) {
    Write-Host ("  apiVersion : {0}" -f $apiVersion)
}

# 版本号自检：对外版应为四位，开发版应为五位
if (-not [string]::IsNullOrWhiteSpace($displayVersion)) {
    $versionParts = $displayVersion.Split('.')
    if ($Dev -and $versionParts.Count -ne 5) {
        Write-Warn "-Dev 开发版版本号应为五位（如 ${displayVersion}.N），实际为 $displayVersion。"
    }
    if (-not $Dev -and $versionParts.Count -ne 4) {
        Write-Warn "对外发布版版本号应为四位，实际为 $displayVersion；请确认输出目录未被其他构建污染。"
    }
}
if ($displayVersion -and $logVersion -and $displayVersion -ne $logVersion) {
    Write-Warn "构建日志中的版本号（$logVersion）与输出清单（$displayVersion）不一致，请检查构建输出。"
}

Write-Host ''
if ($Dev) {
    Write-Host '完成：内部开发版（五位版本号，请勿对外分发）。' -ForegroundColor Green
} else {
    Write-Host '完成：对外发布版已就绪。' -ForegroundColor Green
}
exit 0
