<#
    VisionForge 部署辅助 —— 安装 / 校验 .NET 8 桌面运行时
    ------------------------------------------------------------------
    为什么需要它：
      VisionForge.Main.exe 是「框架依赖」发布包，目标机必须先有
      Microsoft.WindowsDesktop.App 8.x 才能启动，否则会直接报
      "You must install or update .NET to run this application."

    两种安装方式：
      默认        —— 机器级安装，走官方安装包，会弹一次 UAC，需要管理员
      -UserScope  —— 当前用户级安装，不弹 UAC、不需要管理员，
                     运行时装到 %LOCALAPPDATA%\VisionForge\.dotnet，
                     同时写入用户环境变量 DOTNET_ROOT_X64

    用法：
      powershell -ExecutionPolicy Bypass -File install-runtime.ps1
      powershell -ExecutionPolicy Bypass -File install-runtime.ps1 -UserScope
#>
[CmdletBinding()]
param(
    [switch]$UserScope,
    [switch]$Force
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'

$RuntimeVersion  = '8.0.21'
$UserInstallDir  = Join-Path $env:LOCALAPPDATA 'VisionForge\.dotnet'

function Say     ([string]$t) { Write-Host $t }
function Head    ([string]$t) { Write-Host ""; Write-Host "=== $t ===" -ForegroundColor Cyan }
function SayOk   ([string]$t) { Write-Host "[ OK ] $t" -ForegroundColor Green }
function SayWarn ([string]$t) { Write-Host "[  ! ] $t" -ForegroundColor Yellow }
function SayErr  ([string]$t) { Write-Host "[  X ] $t" -ForegroundColor Red }

function Get-DotnetRoots {
    $roots = New-Object System.Collections.Generic.List[string]
    foreach ($v in @($env:DOTNET_ROOT_X64, $env:DOTNET_ROOT)) {
        if ($v -and (Test-Path $v)) { $roots.Add($v) }
    }
    foreach ($p in @((Join-Path $env:ProgramFiles 'dotnet'),
                     (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet'),
                     $UserInstallDir)) {
        if (Test-Path $p) { $roots.Add($p) }
    }
    return ($roots | Select-Object -Unique)
}

function Get-DesktopRuntimes {
    $result = @()
    foreach ($r in Get-DotnetRoots) {
        $p = Join-Path $r 'shared\Microsoft.WindowsDesktop.App'
        if (Test-Path $p) {
            foreach ($d in Get-ChildItem $p -Directory) {
                $result += [pscustomobject]@{ Version = $d.Name; Root = $r }
            }
        }
    }
    return $result
}

function Get-DesktopRuntime8 {
    return @(Get-DesktopRuntimes | Where-Object { $_.Version -like '8.*' })
}

function Test-Online ([string]$TargetHost, [int]$Port = 443, [int]$TimeoutMs = 5000) {
    try {
        $client = New-Object System.Net.Sockets.TcpClient
        $iar    = $client.BeginConnect($TargetHost, $Port, $null, $null)
        $ok     = $iar.AsyncWaitHandle.WaitOne($TimeoutMs)
        if ($ok) { $client.EndConnect($iar) }
        $client.Close()
        return $ok
    } catch {
        return $false
    }
}

function Invoke-Download ([string]$Url, [string]$OutFile) {
    if (Test-Path $OutFile) { Remove-Item $OutFile -Force -ErrorAction SilentlyContinue }

    try {
        Invoke-WebRequest -Uri $Url -OutFile $OutFile -UseBasicParsing -TimeoutSec 600
        if ((Get-Item $OutFile).Length -gt 5MB) { return 'Web' }
    } catch { SayWarn ("Web 方式下载失败：" + $_.Exception.Message) }

    try {
        if (Test-Path $OutFile) { Remove-Item $OutFile -Force -ErrorAction SilentlyContinue }
        Start-BitsTransfer -Source $Url -Destination $OutFile -ErrorAction Stop
        if ((Get-Item $OutFile).Length -gt 5MB) { return 'BITS' }
    } catch { SayWarn ("BITS 方式下载失败：" + $_.Exception.Message) }

    try {
        if (Test-Path $OutFile) { Remove-Item $OutFile -Force -ErrorAction SilentlyContinue }
        & curl.exe -L --fail --silent --show-error --max-time 900 -o $OutFile $Url
        if ($LASTEXITCODE -eq 0 -and (Test-Path $OutFile) -and (Get-Item $OutFile).Length -gt 5MB) { return 'curl' }
    } catch { SayWarn ("curl 方式下载失败：" + $_.Exception.Message) }

    return $null
}

function Install-MachineWide {
    Head "机器级安装（需要管理员，会弹一次 UAC）"

    $urls = @(
        "https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$RuntimeVersion/windowsdesktop-runtime-$RuntimeVersion-win-x64.exe",
        "https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe"
    )

    Say "先探一下外网能不能通 …"
    if (-not (Test-Online 'builds.dotnet.microsoft.com')) {
        SayErr "连不上 builds.dotnet.microsoft.com —— 这台机器当前上不了外网（或防火墙拦了）。"
        Say   "请在有网的电脑上下载下面这个文件，拷到本机后双击安装："
        Say   "  https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$RuntimeVersion/windowsdesktop-runtime-$RuntimeVersion-win-x64.exe"
        Say   "  （文件名：windowsdesktop-runtime-$RuntimeVersion-win-x64.exe，约 60MB）"
        return 2
    }
    SayOk "外网连通，开始下载。"

    $installer = Join-Path $env:TEMP "windowsdesktop-runtime-$RuntimeVersion-win-x64.exe"
    $got = $false
    foreach ($u in $urls) {
        Say "下载：$u"
        $how = Invoke-Download -Url $u -OutFile $installer
        if ($how) {
            SayOk ("下载完成（$how）：" + ('{0:N1} MB' -f ((Get-Item $installer).Length / 1MB)))
            $got = $true
            break
        }
    }
    if (-not $got) {
        SayErr "几种下载方式都没成功 —— 这台机器当前上不了外网。"
        Say   "请在有网的电脑上下载下面这个文件，拷到本机后双击安装："
        Say   "  https://builds.dotnet.microsoft.com/dotnet/WindowsDesktop/$RuntimeVersion/windowsdesktop-runtime-$RuntimeVersion-win-x64.exe"
        Say   "  （文件名：windowsdesktop-runtime-$RuntimeVersion-win-x64.exe）"
        return 2
    }

    $sig = Get-AuthenticodeSignature -FilePath $installer
    if ($sig.Status -eq 'Valid') {
        SayOk ("安装包数字签名有效：" + ($sig.SignerCertificate.Subject -replace '^CN=([^,]+).*', '$1'))
    } else {
        SayWarn ("安装包签名状态为 $($sig.Status) —— 来源非官方时请勿继续。")
    }

    Say "正在提权运行安装程序（UAC 弹窗请点『是』）…"
    $exit = -1
    try {
        $p = Start-Process -FilePath $installer -ArgumentList '/install', '/quiet', '/norestart' -Verb RunAs -Wait -PassThru
        $exit = $p.ExitCode
    } catch {
        SayErr ("提权失败或被取消：" + $_.Exception.Message)
        return 3
    }

    switch ($exit) {
        0       { SayOk   "安装程序返回 0：安装成功。"; return 0 }
        3010    { SayOk   "安装程序返回 3010：安装成功，建议重启一次。"; return 0 }
        1641    { SayOk   "安装程序返回 1641：安装成功，已请求重启。"; return 0 }
        1602    { SayWarn "安装程序返回 1602：用户取消了安装。"; return 3 }
        1603    { SayErr  "安装程序返回 1603：致命错误，常见原因是重启前残留，重启后重试。"; return 4 }
        default { SayErr  "安装程序返回 $exit。"; return 4 }
    }
}

function Install-UserScope {
    Head "当前用户级安装（不需要管理员）"

    if (-not (Test-Online 'dot.net')) {
        SayErr "连不上 dot.net —— 这台机器当前上不了外网。"
        Say   "可在有网机器上用浏览器打开 https://dot.net/v1/dotnet-install.ps1 另存为文件，"
        Say   "拷到本机后执行下面两条命令（缺一不可）："
        Say   "  powershell -ExecutionPolicy Bypass -File dotnet-install.ps1 -Channel 8.0 -Runtime dotnet -InstallDir `"$UserInstallDir`" -NoPath"
        Say   "  powershell -ExecutionPolicy Bypass -File dotnet-install.ps1 -Channel 8.0 -Runtime windowsdesktop -InstallDir `"$UserInstallDir`" -NoPath"
        Say   "更省事的做法：直接在有网机器上下载 windowsdesktop-runtime-$RuntimeVersion-win-x64.exe，拷过来双击装。"
        return 2
    }

    $scriptPath = Join-Path $env:TEMP 'dotnet-install.ps1'
    if (-not (Test-Path $scriptPath)) {
        Say "下载微软官方安装脚本 dotnet-install.ps1 …"
        $how = Invoke-Download -Url 'https://dot.net/v1/dotnet-install.ps1' -OutFile $scriptPath
        if (-not $how) {
            SayErr "脚本下载失败 —— 这台机器当前上不了外网。"
            Say   "可在有网机器上先用浏览器打开 https://dot.net/v1/dotnet-install.ps1 另存为文件，"
            Say   "拷到本机后执行："
            Say   "  powershell -ExecutionPolicy Bypass -File dotnet-install.ps1 -Channel 8.0 -Runtime dotnet -InstallDir `"$UserInstallDir`" -NoPath"
            Say   "  powershell -ExecutionPolicy Bypass -File dotnet-install.ps1 -Channel 8.0 -Runtime windowsdesktop -InstallDir `"$UserInstallDir`" -NoPath"
            return 2
        }
    }

    New-Item -ItemType Directory -Force -Path $UserInstallDir | Out-Null

    foreach ($rt in @('dotnet', 'windowsdesktop')) {
        Say "安装 $rt 运行时到 $UserInstallDir …"
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $scriptPath -Channel 8.0 -Runtime $rt -InstallDir $UserInstallDir -NoPath
        if ($LASTEXITCODE -ne 0) { SayErr "安装 $rt 失败（退出码 $LASTEXITCODE）。"; return 4 }
    }

    [Environment]::SetEnvironmentVariable('DOTNET_ROOT_X64', $UserInstallDir, 'User')
    [Environment]::SetEnvironmentVariable('DOTNET_ROOT',     $UserInstallDir, 'User')
    $env:DOTNET_ROOT_X64 = $UserInstallDir
    $env:DOTNET_ROOT     = $UserInstallDir

    SayOk "已写入用户环境变量 DOTNET_ROOT_X64 = $UserInstallDir"
    SayWarn "已打开的窗口读不到新变量，请用本包里的『3-启动VisionForge.cmd』启动（脚本会自己补上）。"
    return 0
}

function Show-Result {
    Head "校验结果"
    $all = Get-DesktopRuntimes
    if ($all.Count -eq 0) {
        SayErr "没有找到任何 Microsoft.WindowsDesktop.App 运行时。"
    } else {
        foreach ($r in $all) { Say ("  " + $r.Version + "   <-  " + $r.Root) }
    }

    $v8 = Get-DesktopRuntime8
    if ($v8.Count -gt 0) {
        SayOk ("VisionForge 需要的 .NET 8 桌面运行时已就位：v" + ($v8.Version -join '、'))
        Say   "下一步：双击本目录下的『3-启动VisionForge.cmd』启动程序。"
        return 0
    }

    SayErr "仍未检测到 8.x 桌面运行时。"
    Say   "注意：本机已有的 .NET 6 桌面运行时、以及 8.0 的 NETCore 运行时都不够用，"
    Say   "      VisionForge 需要的是 Microsoft.WindowsDesktop.App 8.x（WPF 那一套）。"
    return 1
}

Head "VisionForge 部署辅助 · .NET 8 桌面运行时"
Say  ("本机系统：" + [Environment]::OSVersion.VersionString + "  /  " + $env:PROCESSOR_ARCHITECTURE)

if (-not $Force) {
    $v8 = Get-DesktopRuntime8
    if ($v8.Count -gt 0) {
        SayOk ("已经装好了：v" + ($v8.Version -join '、') + "（位置：" + ($v8.Root -join '、') + "）")
        Say   "无需重复安装。要强制重装请加参数 -Force。"
        exit (Show-Result)
    }
}

if ($UserScope) {
    $rc = Install-UserScope
} else {
    $rc = Install-MachineWide
    if ($rc -ne 0 -and $rc -ne 4) {
        SayWarn "机器级安装没成功，自动改用『当前用户级安装』再试一次…"
        $rc = Install-UserScope
    }
}

$verify = Show-Result
if ($verify -ne 0) {
    Say ""
    SayWarn "安装未完成，程序暂时无法启动。把上面的提示截图给部署人员即可。"
}

exit $verify
