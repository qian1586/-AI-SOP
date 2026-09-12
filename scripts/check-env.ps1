<#
    VisionForge 部署辅助 —— 环境自检
    一次性回答三个问题：
      1) 这台机器能不能跑 VisionForge？（.NET 8 桌面运行时）
      2) 程序文件齐不齐？（exe / dll / config）
      3) 目录能不能写？（运行要在同级目录建 data\ 写历史、日志、NG 图）
#>
[CmdletBinding()]
param()

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

$AppSrc  = Join-Path $PSScriptRoot '..'
$AppDir  = Join-Path $AppSrc 'app'
if (-not (Test-Path $AppDir)) { $AppDir = $AppSrc }
$AppExe  = Join-Path $AppDir 'VisionForge.Main.exe'
$AppCfg  = Join-Path $AppDir 'config\appsettings.json'

$script:Problems = @()

function Head    ([string]$t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
function SayOk   ([string]$t) { Write-Host "[ OK ] $t" -ForegroundColor Green }
function SayWarn ([string]$t) { Write-Host "[  ! ] $t" -ForegroundColor Yellow }
function SayErr  ([string]$t) { Write-Host "[  X ] $t" -ForegroundColor Red; $script:Problems += $t }

Head "1. 操作系统"
$osName = $null
$osVer  = $null
$osArch = $env:PROCESSOR_ARCHITECTURE

try {
    $key = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion' -ErrorAction Stop
    $osName = $key.ProductName
    # 注册表里的 ProductName 至今仍写着 "Windows 10"，用内部版本号校正一下
    if ([int]$key.CurrentBuildNumber -ge 22000) { $osName = $osName -replace 'Windows 10', 'Windows 11' }
    $osVer  = $key.DisplayVersion + "  Build " + $key.CurrentBuildNumber
} catch { }

if (-not $osName) {
    try {
        $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
        $osName = $os.Caption
        $osVer  = $os.Version
        $osArch = $os.OSArchitecture
    } catch { }
}

if (-not $osName) {
    $osName = [Environment]::OSVersion.VersionString
    $osVer  = ''
}

SayOk ($osName + "  " + $osVer)
SayOk ("处理器架构：" + $osArch)
if ($env:PROCESSOR_ARCHITECTURE -notmatch '64') { SayErr "系统看起来不是 64 位，本程序只有 win-x64 版本。" }

Head "2. .NET 运行时"
$roots = New-Object System.Collections.Generic.List[string]
foreach ($v in @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)) {
    if ($v -and (Test-Path $v)) { $roots.Add($v) }
}
foreach ($p in @((Join-Path $env:ProgramFiles 'dotnet'),
                 (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'),
                 (Join-Path $env:LOCALAPPDATA 'VisionForge\.dotnet'))) {
    if (Test-Path $p) { $roots.Add($p) }
}

$desktop8 = @()
foreach ($r in ($roots | Select-Object -Unique)) {
    foreach ($fw in @('Microsoft.WindowsDesktop.App', 'Microsoft.NETCore.App')) {
        $p = Join-Path $r "shared\$fw"
        if (Test-Path $p) {
            foreach ($d in Get-ChildItem $p -Directory) {
                Write-Host ("       " + $fw.PadRight(28) + " " + $d.Name)
                if ($fw -eq 'Microsoft.WindowsDesktop.App' -and $d.Name -like '8.*') { $desktop8 += $d.Name }
            }
        }
    }
}

if ($desktop8.Count -gt 0) {
    SayOk ("找到 .NET 8 桌面运行时：v" + ($desktop8 -join '、'))
} else {
    SayErr "缺少 Microsoft.WindowsDesktop.App 8.x —— 程序无法启动（最常见的部署故障）。"
    Write-Host "       解决办法：双击本目录下的『1-安装.NET8桌面运行时.cmd』。" -ForegroundColor Yellow
}

Head "3. 程序文件"
if (Test-Path $AppExe) {
    $mb = '{0:N1} MB' -f ((Get-Item $AppExe).Length / 1MB)
    SayOk ("主程序：VisionForge.Main.exe（" + $mb + "）")
} else {
    SayErr "找不到 VisionForge.Main.exe —— 程序文件不完整。"
}

foreach ($dll in @('VisionForge.Common.dll','VisionForge.Core.dll','VisionForge.Hardware.dll','VisionForge.Infrastructure.dll')) {
    $f = Join-Path $AppDir $dll
    if (Test-Path $f) { SayOk $dll } else { SayErr ("缺少 " + $dll) }
}

if (Test-Path $AppCfg) {
    SayOk "配置文件：config\appsettings.json"
    try {
        $cfg = Get-Content $AppCfg -Raw -Encoding UTF8 | ConvertFrom-Json
        $cam = $cfg.Cameras | Select-Object -First 1
        $plc = $cfg.Plc
        Write-Host ("       相机：" + $cam.DisplayName + "（" + $cam.Vendor + " / " + $cam.Model + "）") -ForegroundColor Gray
        Write-Host ("       PLC ：" + $plc.Name + "（" + $plc.Brand + " @ " + $plc.IpAddress + ":" + $plc.Port + "）") -ForegroundColor Gray

        if ($cam.Vendor -ne 'Mock' -and $cam.IpAddress) {
            $ping = Test-Connection -ComputerName $cam.IpAddress -Count 2 -Quiet -ErrorAction SilentlyContinue
            if ($ping) { SayOk ("相机 " + $cam.IpAddress + " 网络可达") }
            else       { SayWarn ("相机 " + $cam.IpAddress + " ping 不通 —— 检查网线/网段，或用 MstarViewer 连一下") }
        }
        if ($plc.Brand -ne 'Mock' -and $plc.IpAddress) {
            $tcp = Test-NetConnection -ComputerName $plc.IpAddress -Port $plc.Port -InformationLevel Quiet -WarningAction SilentlyContinue -ErrorAction SilentlyContinue
            if ($tcp) { SayOk ("PLC " + $plc.IpAddress + ":" + $plc.Port + " 可连接") }
            else      { SayWarn ("PLC " + $plc.IpAddress + ":" + $plc.Port + " 连不上 —— 先确认 PLC 上电、IP 正确") }
        }
        if ($cam.Vendor -eq 'Mock' -and $plc.Brand -eq 'Mock') {
            SayOk "当前是 Mock 模式（模拟相机 + 模拟 PLC），不接硬件也能跑通全流程。"
        }
    } catch {
        SayErr ("配置解析失败：" + $_.Exception.Message)
    }
} else {
    SayErr "缺少 config\appsettings.json"
}

Head "4. 目录权限"
if (Test-Path $AppDir) {
    $probe = Join-Path $AppDir '.write-probe.tmp'
    try {
        'probe' | Out-File -FilePath $probe -Encoding ascii -Force
        Remove-Item $probe -Force
        SayOk ("程序目录可写：" + [IO.Path]::GetFullPath($AppDir))
    } catch {
        SayErr "程序目录不可写 —— 程序要在同级目录建 data\。请把整个目录换到 D:\VisionForge 这类可写位置，不要放 C:\Program Files 下。"
    }
} else {
    SayErr ("找不到程序目录：" + $AppDir)
}

$dataDir = Join-Path $AppDir 'data'
if (Test-Path $dataDir) {
    SayOk "已存在运行数据目录 data\ —— 说明程序至少启动过一次。"
} else {
    Write-Host "       首次运行会自动创建 data\{recipes, sop.json, history, ng-images, logs}。" -ForegroundColor Gray
}

Head "总结"
if ($script:Problems.Count -eq 0) {
    SayOk "全部检查通过，可以启动：双击『3-启动VisionForge.cmd』。"
    exit 0
}

Write-Host ("[  X ] 有 " + $script:Problems.Count + " 项未通过：") -ForegroundColor Red
foreach ($p in $script:Problems) { Write-Host ("       · " + $p) -ForegroundColor Red }
if ($desktop8.Count -eq 0) {
    Write-Host ""
    Write-Host "最快的修复方式：双击『1-安装.NET8桌面运行时.cmd』，装完再跑一次本自检。" -ForegroundColor Yellow
}
exit 1
