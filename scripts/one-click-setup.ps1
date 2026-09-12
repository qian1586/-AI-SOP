<#
    VisionForge 一键部署
    ==================================================================
    干三件事：
      1) 装 .NET 8 桌面运行时
         有管理员权限 → 官方安装包装到 C:\Program Files\dotnet（机器级）
         没管理员权限 → 官方脚本装到 %LOCALAPPDATA%\VisionForge\.dotnet（用户级）
      2) 把程序拷到 D:\VisionForge（不可写时自动退到 %LOCALAPPDATA%\VisionForge）
      3) 在桌面建一个「VisionForge」快捷方式

    日志：%TEMP%\visionforge-setup.log

    用法：
      powershell -ExecutionPolicy Bypass -File one-click-setup.ps1
      powershell -ExecutionPolicy Bypass -File one-click-setup.ps1 -Target "E:\VisionForge"
#>
[CmdletBinding()]
param(
    [string]$Target,
    [switch]$Elevated
)

$ErrorActionPreference = 'Continue'
$ProgressPreference    = 'SilentlyContinue'

$RuntimeVersion = '8.0.21'
$Source         = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$LogFile        = Join-Path $env:TEMP 'visionforge-setup.log'
$UserRuntimeDir = Join-Path $env:LOCALAPPDATA 'VisionForge\.dotnet'

function Log     ([string]$t) { $line = "[{0:HH:mm:ss}] {1}" -f (Get-Date), $t; Write-Host $line; Add-Content -Path $LogFile -Value $line -Encoding UTF8 }
function Head    ([string]$t) { Log ""; Log ("========== " + $t + " ==========") }
function SayOk   ([string]$t) { Log ("[ OK ] " + $t) }
function SayWarn ([string]$t) { Log ("[  ! ] " + $t) }
function SayErr  ([string]$t) { Log ("[  X ] " + $t) }

function Test-Admin {
    return ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Get-DesktopRuntime8 {
    $roots = @()
    foreach ($v in @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)) { if ($v -and (Test-Path $v)) { $roots += $v } }
    $roots += (Join-Path $env:ProgramFiles 'dotnet')
    $roots += $UserRuntimeDir
    $hit = @()
    foreach ($r in ($roots | Select-Object -Unique)) {
        $p = Join-Path $r 'shared\Microsoft.WindowsDesktop.App'
        if (Test-Path $p) { $hit += (Get-ChildItem $p -Directory | Where-Object { $_.Name -like '8.*' }).Name }
    }
    return $hit
}

function Install-MachineWideRuntime {
    $urls = @(
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$RuntimeVersion/windowsdesktop-runtime-$RuntimeVersion-win-x64.exe",
        "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
    )
    $installer = Join-Path $env:TEMP "windowsdesktop-runtime-$RuntimeVersion-win-x64.exe"

    foreach ($u in $urls) {
        for ($attempt = 1; $attempt -le 2; $attempt++) {
            Log ("下载：" + $u)
            try {
                if (Test-Path $installer) { Remove-Item $installer -Force -ErrorAction SilentlyContinue }
                Invoke-WebRequest -Uri $u -OutFile $installer -UseBasicParsing -TimeoutSec 900
                if ((Get-Item $installer).Length -gt 5MB) {
                    SayOk ("下载完成：" + ('{0:N1} MB' -f ((Get-Item $installer).Length / 1MB)))
                    $sig = Get-AuthenticodeSignature -FilePath $installer
                    if ($sig.Status -eq 'Valid') {
                        SayOk ("签名有效：" + ($sig.SignerCertificate.Subject -replace '^CN=([^,]+).*', '$1'))
                    } else {
                        SayWarn ("签名状态：" + $sig.Status)
                    }
                    Log "开始静默安装（约 1 分钟，请勿关闭窗口）…"
                    $p = Start-Process -FilePath $installer -ArgumentList '/install', '/quiet', '/norestart' -Wait -PassThru
                    Log ("安装程序退出码：" + $p.ExitCode)
                    return $true
                }
            } catch {
                SayWarn ("下载失败（第 $attempt 次）：" + $_.Exception.Message)
                Start-Sleep -Seconds 2
            }
        }
    }
    return $false
}

function Install-UserScopeRuntime {
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

    New-Item -ItemType Directory -Force -Path $UserRuntimeDir | Out-Null
    foreach ($rt in @('dotnet', 'windowsdesktop')) {
        Log ("安装 " + $rt + " 运行时到 " + $UserRuntimeDir)
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath -Channel 8.0 -Runtime $rt -InstallDir $UserRuntimeDir -NoPath
        if ($LASTEXITCODE -ne 0) {
            SayErr ($rt + " 安装失败，退出码 " + $LASTEXITCODE)
            return $false
        }
    }

    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $UserRuntimeDir, 'User')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT',     $UserRuntimeDir, 'User')
    $env:DOTNET_ROOT_X64 = $UserRuntimeDir
    $env:DOTNET_ROOT     = $UserRuntimeDir
    SayOk ("已写入用户环境变量 DOTNET_ROOT_X64 = " + $UserRuntimeDir)
    return $true
}

function Copy-Program ([string]$Dest) {
    if (-not (Test-Path $Dest)) { New-Item -ItemType Directory -Force -Path $Dest | Out-Null }

    $rc = Start-Process -FilePath 'robocopy.exe' -Wait -PassThru -NoNewWindow -ArgumentList @(
        ('"' + $Source + '"'), ('"' + $Dest + '"'),
        '/E', '/NFL', '/NDL', '/NJH', '/NJS', '/NP', '/XF', '*.tmp', '/XD', 'data'
    )
    Log ("robocopy 退出码：" + $rc.ExitCode)
    if ($rc.ExitCode -ge 8) { return $false }
    return (Test-Path (Join-Path $Dest 'app\VisionForge.Main.exe'))
}

function New-Shortcut ([string]$Dest) {
    try {
        $desktop = [Environment]::GetFolderPath('Desktop')
        $lnkPath = Join-Path $desktop 'VisionForge.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $lnk = $shell.CreateShortcut($lnkPath)
        $lnk.TargetPath       = Join-Path $Dest '3-启动VisionForge.cmd'
        $lnk.WorkingDirectory = $Dest
        $lnk.Description      = 'VisionForge 人工标准化操作监测系统'
        $lnk.IconLocation     = (Join-Path $Dest 'app\VisionForge.Main.exe') + ',0'
        $lnk.Save()
        SayOk ("桌面快捷方式已创建：" + $lnkPath)
    } catch {
        SayWarn ("快捷方式创建失败（不影响使用）：" + $_.Exception.Message)
    }
}

# ==================================================================
Head "VisionForge 一键部署"
$isAdmin = Test-Admin
Log ("当前用户：" + [Security.Principal.WindowsIdentity]::GetCurrent().Name + "    管理员=" + $isAdmin)

# ---- 不是管理员：先试着提权；提权不了就走用户级 ----
if (-not $isAdmin) {
    Write-Host ""
    Write-Host "需要管理员权限来安装 .NET 运行时，正在申请提权…" -ForegroundColor Yellow
    Write-Host "马上会弹出一个窗口问『是否允许此应用对你的设备进行更改』，请点『是』。" -ForegroundColor Yellow
    Write-Host "（如果你点『否』，我会自动改用不需要管理员的用户级安装。）" -ForegroundColor DarkGray
    Write-Host ""
    $scriptPath = $PSCommandPath
    $argList = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', ('"' + $scriptPath + '"'), '-Elevated')
    if ($Target) { $argList += @('-Target', ('"' + $Target + '"')) }

    $elevatedExit = $null
    try {
        $proc = Start-Process -FilePath 'powershell.exe' -Verb RunAs -PassThru -ArgumentList $argList
        $proc.WaitForExit()
        $elevatedExit = $proc.ExitCode
    } catch {
        SayWarn ("提权不可用或被取消：" + $_.Exception.Message)
    }

    if ($elevatedExit -eq 0) {
        Write-Host ""
        Write-Host "管理员模式部署完成。" -ForegroundColor Green
        exit 0
    }
    if ($elevatedExit -ne $null) {
        SayWarn ("管理员模式部署未成功（退出码 " + $elevatedExit + "），改用不需要管理员的用户级安装。")
    } else {
        SayWarn "改用不需要管理员的用户级安装。"
    }
}

# ---- 第一步：运行时 ----
Head "1/3 .NET 8 桌面运行时"
$v8 = Get-DesktopRuntime8
if ($v8.Count -gt 0) {
    SayOk ("已安装：v" + ($v8 -join '、') + "，跳过安装。")
} else {
    SayWarn "未检测到 Microsoft.WindowsDesktop.App 8.x，开始安装。"
    $ok = $false
    if ($isAdmin) {
        $ok = Install-MachineWideRuntime
    } else {
        $ok = Install-UserScopeRuntime
    }

    $v8 = Get-DesktopRuntime8
    if ($v8.Count -eq 0) {
        SayErr "运行时没装成 —— 大概率是这台机器上不了外网。"
        Log "离线做法：在有网的电脑上下载 windowsdesktop-runtime-$RuntimeVersion-win-x64.exe（约 60MB）"
        Log "  https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$RuntimeVersion/windowsdesktop-runtime-$RuntimeVersion-win-x64.exe"
        Log "拷到本机双击安装，然后重新运行本脚本。"
        Log ("完整日志：" + $LogFile)
        Write-Host ""
        Write-Host "请把上面的提示截图发给部署人员。" -ForegroundColor Yellow
        Read-Host "按回车关闭"
        exit 2
    }
    SayOk ("安装成功：v" + ($v8 -join '、'))
}

# ---- 第二步：装程序 ----
Head "2/3 复制程序"
if (-not $Target) { $Target = 'D:\VisionForge' }

Log ("目标目录：" + $Target)
$copied = Copy-Program -Dest $Target
if (-not $copied) {
    $fallback = Join-Path $env:LOCALAPPDATA 'VisionForge'
    SayWarn ("装不到 " + $Target + "，改用 " + $fallback)
    $Target = $fallback
    $copied = Copy-Program -Dest $Target
}
if (-not $copied) {
    SayErr "复制失败，请把日志发给部署人员：" + $LogFile
    Read-Host "按回车关闭"
    exit 5
}
SayOk ("程序就位：" + (Join-Path $Target 'app\VisionForge.Main.exe'))

if ($isAdmin) {
    & icacls.exe $Target /grant '*S-1-5-32-545:(OI)(CI)M' /T /C /Q | Out-Null
    SayOk "已放开目录写权限给本机普通用户。"
}

# ---- 第三步：快捷方式 ----
Head "3/3 桌面快捷方式"
New-Shortcut -Dest $Target

Head "部署完成"
SayOk ("运行时：.NET 桌面运行时 v" + ((Get-DesktopRuntime8) -join '、'))
SayOk ("程序位置：" + $Target)
SayOk "以后双击桌面上的「VisionForge」图标就能启动。"
Log ("日志文件：" + $LogFile)
Write-Host ""
Read-Host "按回车关闭"
exit 0
