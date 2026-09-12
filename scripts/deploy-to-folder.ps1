<#
    VisionForge 部署辅助 —— 把本包整体安装到目标目录
    默认装到 D:\VisionForge（手册里约定的现场路径），并可选建桌面快捷方式。

    为什么需要这个脚本：
      · 程序运行要在同级目录建 data\，所以不能放 C:\Program Files
      · C:\ 根目录建文件夹需要管理员，D:\ 根目录一般不需要
      · 目录权限不够时它能自动提权重试，操作员不用懂 UAC

    用法：
      powershell -ExecutionPolicy Bypass -File deploy-to-folder.ps1
      powershell -ExecutionPolicy Bypass -File deploy-to-folder.ps1 -Target "E:\VisionForge"
      powershell -ExecutionPolicy Bypass -File deploy-to-folder.ps1 -NoShortcut
#>
[CmdletBinding()]
param(
    [string]$Target = 'D:\VisionForge',
    [switch]$NoShortcut,
    [switch]$Elevated
)

$ErrorActionPreference = 'Stop'

$Source = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path

function Say     ([string]$t) { Write-Host $t }
function Head    ([string]$t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
function SayOk   ([string]$t) { Write-Host "[ OK ] $t" -ForegroundColor Green }
function SayWarn ([string]$t) { Write-Host "[  ! ] $t" -ForegroundColor Yellow }
function SayErr  ([string]$t) { Write-Host "[  X ] $t" -ForegroundColor Red }

function Test-Writable ([string]$Path) {
    if (-not (Test-Path $Path)) { return $false }
    $probe = Join-Path $Path ('.vf-probe-' + [guid]::NewGuid().ToString('N').Substring(0,6))
    try {
        'x' | Out-File -FilePath $probe -Encoding ascii -Force -ErrorAction Stop
        Remove-Item $probe -Force -ErrorAction SilentlyContinue
        return $true
    } catch { return $false }
}

Head "VisionForge 部署到目标目录"
Say ("源目录：" + $Source)
Say ("目标目录：" + $Target)

# 目标目录不存在先建；建不了就提权重试一次
if (-not (Test-Path $Target)) {
    try {
        New-Item -ItemType Directory -Force -Path $Target -ErrorAction Stop | Out-Null
        SayOk "已创建目标目录。"
    } catch {
        if ($Elevated) {
            SayErr ("即使提权也建不出目录：" + $_.Exception.Message)
            exit 4
        }
        SayWarn "普通权限建不了这个目录，申请管理员权限重试（UAC 弹窗请点『是』）…"
        $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $PSCommandPath + '"'),
                     '-Target', ('"' + $Target + '"'), '-Elevated')
        if ($NoShortcut) { $argList += '-NoShortcut' }
        try {
            $p = Start-Process -FilePath 'powershell.exe' -Verb RunAs -Wait -PassThru -ArgumentList $argList
            exit $p.ExitCode
        } catch {
            SayErr ("提权失败或被取消：" + $_.Exception.Message)
            exit 3
        }
    }
}

if (-not (Test-Writable $Target)) {
    SayErr "目标目录不可写，换一个位置再试（例如 E:\VisionForge）。"
    exit 4
}

Head "复制文件"
$rc = Start-Process -FilePath 'robocopy.exe' -Wait -PassThru -NoNewWindow -ArgumentList @(
    ('"' + $Source + '"'), ('"' + $Target + '"'),
    '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/XF', '*.tmp', '/XD', 'data'
)
if ($rc.ExitCode -ge 8) {
    SayErr ("robocopy 失败，退出码 " + $rc.ExitCode + "。")
    exit 5
}
SayOk ("复制完成（robocopy 退出码 " + $rc.ExitCode + "）。")

$exe = Join-Path $Target 'app\VisionForge.Main.exe'
if (-not (Test-Path $exe)) {
    SayErr "复制后没找到 app\VisionForge.Main.exe，请检查目标目录。"
    exit 5
}
SayOk ("程序就位：" + $exe)

if (-not $NoShortcut) {
    Head "桌面快捷方式"
    try {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $lnkPath = Join-Path $desktop 'VisionForge.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($lnkPath)
        $lnk.TargetPath       = Join-Path $Target '3-启动VisionForge.cmd'
        $lnk.WorkingDirectory = $Target
        $lnk.Description      = 'VisionForge 人工标准化操作监测系统'
        $lnk.IconLocation     = $exe + ',0'
        $lnk.Save()
        SayOk ("已创建桌面快捷方式：VisionForge.lnk")
    } catch {
        SayWarn ("快捷方式创建失败（不影响使用）：" + $_.Exception.Message)
    }
}

Head "完成"
SayOk ("部署完成，位置：" + $Target)
Say   "启动方式一：双击桌面『VisionForge』快捷方式"
Say   ("启动方式二：双击 " + (Join-Path $Target '3-启动VisionForge.cmd'))
Say   ("首次启动前建议先跑一次：" + (Join-Path $Target '2-环境自检.cmd'))
exit 0
