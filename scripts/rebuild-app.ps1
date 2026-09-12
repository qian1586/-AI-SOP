<#
    VisionForge 修复重建
    ==================================================================
    用本包里的源码重新编译一遍，覆盖已经部署的程序。

    修的是什么：
      1) 启动死锁 —— 首次运行时写示例配方那一步在 UI 线程上同步等异步，
         await 的续体回不到被自己堵住的 UI 线程，界面永远出不来
         （现象：日志停在第 2 行、recipes 目录里留一个 .json.tmp、进程活着但没窗口）
      2) 关窗口死锁 —— 窗口关闭时释放相机走同样的同步等待
      3) 相机断开死锁 —— DisconnectAsync 里也是同步等待

    日志：%TEMP%\visionforge-rebuild.log
    用法：
      powershell -ExecutionPolicy Bypass -File rebuild-app.ps1
      powershell -ExecutionPolicy Bypass -File rebuild-app.ps1 -Target "E:\VisionForge" -NoLaunch
#>
[CmdletBinding()]
param(
    [string]$Target = 'D:\VisionForge',
    [string]$Source,
    [switch]$NoLaunch,
    [switch]$SkipSelfTest
)

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

$Root    = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
if (-not $Source) { $Source = Join-Path $Root 'src' }

$LogFile    = Join-Path $env:TEMP 'visionforge-rebuild.log'
$SdkDir     = Join-Path $env:LOCALAPPDATA 'VisionForge\.dotnet'
$AppDir     = Join-Path $Target 'app'
$ProjectFile = Join-Path $Source 'src\VisionForge.Main\VisionForge.Main.csproj'

function Log     ([string]$t) { $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $t; Write-Host $line; Add-Content -Path $LogFile -Value $line -Encoding UTF8 }
function Head    ([string]$t) { Log ""; Log ("========== " + $t + " ==========") }
function SayOk   ([string]$t) { Log ("[ OK ] " + $t) }
function SayWarn ([string]$t) { Log ("[  ! ] " + $t) }
function SayErr  ([string]$t) { Log ("[  X ] " + $t) }

function Get-DotnetWithSdk {
    $candidates = New-Object System.Collections.Generic.List[string]
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates.Add($cmd.Source) }
    $candidates.Add((Join-Path $SdkDir 'dotnet.exe'))
    $candidates.Add((Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))

    foreach ($c in ($candidates | Select-Object -Unique)) {
        if (-not $c -or -not (Test-Path $c)) { continue }
        $sdks = @(& $c --list-sdks 2>$null)
        if ($sdks.Count -gt 0) {
            return [pscustomobject]@{ Exe = $c; Dir = (Split-Path $c -Parent); Sdks = $sdks }
        }
    }
    return $null
}

function Install-Sdk {
    $scriptPath = Join-Path $env:TEMP 'dotnet-install.ps1'
    if (-not (Test-Path $scriptPath)) {
        Log "下载官方安装脚本 dotnet-install.ps1 …"
        try {
            Invoke-WebRequest -Uri 'https://dot.net/v1/dotnet-install.ps1' -OutFile $scriptPath -UseBasicParsing -TimeoutSec 300
        } catch {
            SayErr ("脚本下载失败：" + $_.Exception.Message)
            return $false
        }
    }

    New-Item -ItemType Directory -Force -Path $SdkDir | Out-Null
    Log ("安装 .NET 8 SDK 到 " + $SdkDir + "（约 200MB，几分钟，请勿关窗口）…")
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath -Channel 8.0 -InstallDir $SdkDir -NoPath
    return ($LASTEXITCODE -eq 0)
}

function Get-LatestLog {
    $dir = Join-Path $AppDir 'data\logs'
    if (-not (Test-Path $dir)) { return $null }
    return (Get-ChildItem $dir -File -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1)
}

# ==================================================================
Head "VisionForge 修复重建"

if (-not (Test-Path (Join-Path $AppDir 'VisionForge.Main.exe'))) {
    SayErr ("没找到已部署的程序：" + $AppDir + "\VisionForge.Main.exe")
    Log "先跑一次『0-一键部署.cmd』把程序装到 " + $Target + "，再跑本脚本。"
    exit 2
}
if (-not (Test-Path $ProjectFile)) {
    SayErr ("没找到源码工程：" + $ProjectFile)
    exit 2
}

# ---- 1. 停掉正在运行（卡死）的实例 ----
Head "1/4 停掉正在运行的 VisionForge"
$procs = @(Get-Process -Name 'VisionForge.Main' -ErrorAction SilentlyContinue)
if ($procs.Count -eq 0) {
    SayOk "当前没有在运行的实例。"
} else {
    foreach ($p in $procs) {
        try { $p.Kill(); SayOk ("已结束进程 PID=" + $p.Id) } catch { SayWarn ("结束 PID " + $p.Id + " 失败：" + $_.Exception.Message) }
    }
    Start-Sleep -Seconds 2
}

# ---- 2. 准备编译器 ----
Head "2/4 准备编译环境（.NET 8 SDK）"
$dn = Get-DotnetWithSdk
if ($dn) {
    SayOk ("已找到可用的 .NET SDK：" + ($dn.Sdks -join '  |  '))
} else {
    SayWarn "本机没有 .NET SDK（注意：运行时 ≠ SDK，运行时不能编译）。"
    if (-not (Install-Sdk)) {
        SayErr "SDK 安装失败 —— 大概率是上不了外网。"
        Log "离线做法：在有网机器上下载 dotnet-sdk-8.0-win-x64.exe 拷过来安装后重跑本脚本。"
        Log ("完整日志：" + $LogFile)
        exit 3
    }
    $dn = Get-DotnetWithSdk
}
if (-not $dn) { SayErr "仍然找不到 SDK，退出。"; exit 3 }

$env:DOTNET_ROOT     = $dn.Dir
$env:DOTNET_ROOT_X64 = $dn.Dir
$env:PATH            = $dn.Dir + ';' + $env:PATH
if (-not $env:APPDATA) { $env:APPDATA = Join-Path $env:USERPROFILE 'AppData\Roaming' }
$env:DOTNET_CLI_TELEMETRY_OPTOUT   = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

# ---- 3. 编译 ----
Head "3/4 编译源码"
Log ("编译器：" + $dn.Exe)
Log ("工程文件：" + $ProjectFile)

$buildArgs = @('build', $ProjectFile, '-c', 'Release', '--nologo', '-v', 'minimal')
Log ("执行：" + $dn.Exe + " " + ($buildArgs -join ' '))
& $dn.Exe @buildArgs 2>&1 | Tee-Object -FilePath (Join-Path $env:TEMP 'visionforge-build-raw.txt') | ForEach-Object { Log ("    " + $_) }
$buildExit = $LASTEXITCODE

if ($buildExit -ne 0) {
    SayWarn "第一次编译失败（退出码 $buildExit），允许联网后再试一次（取缺失的编译包）…"
    $buildArgs2 = $buildArgs + @('-p:RestoreSources=https://api.nuget.org/v3/index.json')
    & $dn.Exe @buildArgs2 2>&1 | Tee-Object -FilePath (Join-Path $env:TEMP 'visionforge-build-raw2.txt') | ForEach-Object { Log ("    " + $_) }
    $buildExit = $LASTEXITCODE
}
if ($buildExit -ne 0) {
    SayErr "编译失败（退出码 $buildExit）—— 程序没有被更新。"
    Log "下面是编译错误行（把窗口内容截图发给我即可）："
    $raw = Join-Path $env:TEMP 'visionforge-build-raw.txt'
    if (Test-Path $raw) {
        Get-Content $raw -Encoding UTF8 |
            Where-Object { $_ -match 'error|错误' } |
            Select-Object -First 12 |
            ForEach-Object { Write-Host ("    " + $_) -ForegroundColor Yellow }
    }
    SayWarn ("D:\VisionForge 里仍然是上一版（可以正常使用），现在替你启动它，不耽误干活。")
    try {
        Start-Process -FilePath (Join-Path $AppDir 'VisionForge.Main.exe') -WorkingDirectory $AppDir
    } catch { }
    exit 4
}
SayOk "编译通过。"

$outDir = Join-Path $Source 'src\VisionForge.Main\bin\Release\net8.0-windows'
if (-not (Test-Path (Join-Path $outDir 'VisionForge.Main.exe'))) {
    SayErr ("编译输出里没有 VisionForge.Main.exe：" + $outDir)
    exit 4
}

# ---- 4. 覆盖部署 ----
Head "4/5 覆盖程序"
Log ("复制 " + $outDir + "  →  " + $AppDir)
$rc = Start-Process -FilePath 'robocopy.exe' -Wait -PassThru -NoNewWindow -ArgumentList @(
    ('"' + $outDir + '"'), ('"' + $AppDir + '"'),
    '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/XF', '*.pdb', '*.tmp', '/XD', 'data'
)
if ($rc.ExitCode -ge 8) { SayErr ("复制失败，robocopy 退出码 " + $rc.ExitCode); exit 5 }
SayOk "程序已更新（robocopy 退出码 " + $rc.ExitCode + "）。"

# ---- 图标刷新 ----
# exe 换了图标之后，Windows 的图标缓存不会自动更新，桌面快捷方式还会显示旧图标。
# 这里两手一起做：① 把快捷方式的图标重新指向新 exe ② 通知系统重建图标缓存。
$exeForIcon = Join-Path $AppDir 'VisionForge.Main.exe'
try {
    $shell = New-Object -ComObject WScript.Shell
    $desktopDir = [Environment]::GetFolderPath('Desktop')
    $refreshed = 0

    foreach ($lnkFile in (Get-ChildItem $desktopDir -Filter *.lnk -ErrorAction SilentlyContinue)) {
        $lnk = $shell.CreateShortcut($lnkFile.FullName)
        if ($lnk.TargetPath -like '*VisionForge*') {
            $lnk.IconLocation = $exeForIcon + ',0'
            $lnk.Save()
            $refreshed++
        }
    }

    if ($refreshed -gt 0) { SayOk ("已把 $refreshed 个桌面快捷方式的图标指向新程序。") }

    # 桌面上再放一个"软件本体"的正式快捷方式：
    # 现场操作员应该双击的是软件本身，而不是"修复并重启"这种维护脚本。
    # 名字与图标都用正式的，避免桌面上摆着一个看不出是什么的快捷方式。
    $appLinkPath = Join-Path $desktopDir '追觅洗地机AI-SOP监测系统.lnk'
    $appLink = $shell.CreateShortcut($appLinkPath)
    $appLink.TargetPath       = $exeForIcon
    $appLink.WorkingDirectory = $AppDir
    $appLink.IconLocation     = $exeForIcon + ',0'
    $appLink.Description      = '追觅洗地机AI-SOP监测系统'
    $appLink.Save()
    SayOk "已创建/更新桌面快捷方式：追觅洗地机AI-SOP监测系统"

    # 名字改过之后，把桌面上那个老快捷方式清掉，免得桌面上摆着两个一样的程序
    $legacyLinkPath = Join-Path $desktopDir '追觅洗地机操作监测系统.lnk'
    if (Test-Path $legacyLinkPath) {
        try {
            Remove-Item -LiteralPath $legacyLinkPath -Force
            SayOk "已删除改名前的旧快捷方式：追觅洗地机操作监测系统.lnk"
        } catch {
            SayWarn ("旧快捷方式删除失败（可手动删）：" + $_.Exception.Message)
        }
    }

    # 让资源管理器重读图标缓存，否则可能还要等一会儿才刷新
    & ie4uinit.exe -show 2>$null
} catch {
    SayWarn ("图标刷新失败（不影响使用）：" + $_.Exception.Message)
}

# ---- 5. 监测逻辑自检 ----
# 这一节是这次改动的重点：不接产线、不开界面，把"人做对/做错"的各类场景
# 真跑一遍（取图、算法、状态机、计数、落历史），并留下可核对的报告。
Head "5/5 监测逻辑自检"

$appExe = Join-Path $AppDir 'VisionForge.Main.exe'
$report = Join-Path $AppDir 'data\selftest\report.txt'
$selfTestExit = -1

if ($SkipSelfTest) {
    SayWarn "按参数跳过自检。"
} else {
    Log ("执行：" + $appExe + " --selftest")
    $st = Start-Process -FilePath $appExe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow
    $selfTestExit = $st.ExitCode
    Log ("自检退出码：" + $selfTestExit)

    if (Test-Path $report) {
        Log "---------- 自检报告 ----------"
        foreach ($line in Get-Content $report -Encoding UTF8) {
            Write-Host ("    " + $line)
            Add-Content -Path $LogFile -Value ("    " + $line) -Encoding UTF8
        }
        Log "------------------------------"
    } else {
        SayWarn ("没找到自检报告：" + $report)
    }

    if ($selfTestExit -eq 0) {
        SayOk "监测逻辑自检全部通过（判定 / 锁线 / 复检 / 计数 / 落历史）。"
    } else {
        SayErr "自检有未通过项（退出码 $selfTestExit），详见上面的报告。"
    }
}

if ($NoLaunch) { SayWarn "按参数要求不启动程序。"; exit 0 }

Head "启动并观察初始化"
Log "启动程序并观察初始化日志 …"
Start-Process -FilePath (Join-Path $AppDir 'VisionForge.Main.exe') -WorkingDirectory $AppDir

$passed = $false
for ($i = 1; $i -le 30; $i++) {
    Start-Sleep -Seconds 1
    $lf = Get-LatestLog
    if ($lf) {
        $txt = Get-Content $lf.FullName -Raw -Encoding UTF8 -ErrorAction SilentlyContinue
        if ($txt -and $txt -match '已注册算法插件') { $passed = $true; break }
    }
}

$proc = @(Get-Process -Name 'VisionForge.Main' -ErrorAction SilentlyContinue)
$lf = Get-LatestLog

Head "结果"
if ($selfTestExit -eq 0) {
    SayOk "监测逻辑自检：全部通过"
} elseif ($selfTestExit -gt 0) {
    SayErr "监测逻辑自检：有未通过项（见上面的报告）"
} else {
    SayWarn "监测逻辑自检：未执行"
}

if ($passed -and $proc.Count -gt 0) {
    SayOk "程序已启动：界面已正常加载。"
} elseif ($proc.Count -gt 0) {
    SayWarn "进程在运行，但 30 秒内没看到初始化日志，请把日志发我。"
} else {
    SayErr "程序没起来。请把日志发我。"
}

if ($lf) {
    Log ("最近日志（" + $lf.FullName + "）：")
    Get-Content $lf.FullName -Encoding UTF8 -Tail 15 | ForEach-Object { Write-Host ("    " + $_) }
}
Log ("完整日志：" + $LogFile)

if ($passed -and $selfTestExit -le 0) { exit 0 } else { exit 6 }
