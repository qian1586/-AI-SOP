<#
    VisionForge 部署辅助 —— 启动程序
    先校验运行时，缺了就直接用中文说清楚并问一句是否当场安装，
    避免出现「双击 exe 弹一段看不懂的英文报错」。

    用法：
      powershell -ExecutionPolicy Bypass -File launch-app.ps1
      powershell -ExecutionPolicy Bypass -File launch-app.ps1 -Yes    # 缺运行时就直接装，不询问
#>
[CmdletBinding()]
param(
    [switch]$Yes
)

$ErrorActionPreference = 'Stop'

$AppRoot = Join-Path $PSScriptRoot '..'
$AppDir  = Join-Path $AppRoot 'app'
if (-not (Test-Path $AppDir)) { $AppDir = $AppRoot }
$AppExe  = Join-Path $AppDir 'VisionForge.Main.exe'

function Say     ([string]$t) { Write-Host $t }
function SayOk   ([string]$t) { Write-Host "[ OK ] $t" -ForegroundColor Green }
function SayWarn ([string]$t) { Write-Host "[  ! ] $t" -ForegroundColor Yellow }
function SayErr  ([string]$t) { Write-Host "[  X ] $t" -ForegroundColor Red }

function Get-DesktopRuntime8 {
    $roots = @()
    foreach ($v in @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)) {
        if ($v -and (Test-Path $v)) { $roots += $v }
    }
    foreach ($p in @((Join-Path $env:ProgramFiles 'dotnet'),
                     (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'),
                     (Join-Path $env:LOCALAPPDATA 'VisionForge\.dotnet'))) {
        if (Test-Path $p) { $roots += $p }
    }
    $hit = @()
    foreach ($r in ($roots | Select-Object -Unique)) {
        $p = Join-Path $r 'shared\Microsoft.WindowsDesktop.App'
        if (Test-Path $p) { $hit += (Get-ChildItem $p -Directory | Where-Object { $_.Name -like '8.*' }).Name }
    }
    return $hit
}

Say ""
Say "VisionForge 启动检查 …"

if (-not (Test-Path $AppExe)) {
    SayErr ("找不到程序：" + $AppExe)
    Say   "看起来程序文件没拷全，请把整个部署包重新拷一遍。"
    exit 2
}

$v8 = Get-DesktopRuntime8
if ($v8.Count -eq 0) {
    SayErr "缺少 .NET 8 桌面运行时（Microsoft.WindowsDesktop.App 8.x），程序起不来。"
    Say   "（本机自带的 .NET 6 桌面运行时、或者 8.0 的控制台运行时都不管用）"
    Say ""
    Say   "现在安装吗？装完会自动接着启动程序。"

    if ($Yes) {
        $answer = 'Y'
    } else {
        $answer = Read-Host "输入 Y 安装 / 直接回车跳过"
    }

    if ($answer -match '^(y|yes|是)$') {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot 'install-runtime.ps1')
        $v8 = Get-DesktopRuntime8
        if ($v8.Count -eq 0) {
            SayErr "运行时仍未就位，先把安装问题解决再启动。"
            exit 3
        }
    } else {
        SayWarn "已跳过。需要时双击『1-安装.NET8桌面运行时.cmd』。"
        exit 1
    }
}

SayOk ("运行时就绪：.NET 桌面运行时 v" + ($v8 -join '、'))

$dataDir = Join-Path $AppDir 'data'
if (-not (Test-Path $dataDir)) {
    try { New-Item -ItemType Directory -Force -Path $dataDir | Out-Null }
    catch { SayWarn "程序目录不可写，运行数据（历史/日志/NG 图）会落不了盘。建议把部署包放到 D:\VisionForge。" }
}

Say ("正在启动：" + $AppExe)
Start-Process -FilePath $AppExe -WorkingDirectory $AppDir
SayOk "已启动。首次运行会自动生成示例配方和 data\ 目录。"
Say   "（当前配置是模拟相机 + 模拟 PLC，不接硬件也能把流程跑通）"
exit 0
