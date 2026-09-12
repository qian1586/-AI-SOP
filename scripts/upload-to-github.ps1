<#
  一键上传到 GitHub（不依赖 git）。

  做四件事：
    ① 找到本机的 Python（Codex 自带运行时 / 已安装的 Python 都认）
    ② 保证 GitHub 凭据可用：没登录过就用 gh 引导登录一次（以后长期有效）
    ③ 调 upload-to-github.py：版本号 +0.1 → 逐文件上传 → 提交 → 更新 main
    ④ 失败时说清原因（没登录 / 没权限 / 网络不通），并给出下一步

  为什么不用 git push：
    目标机器上没装 Git for Windows；Codex 自带的那个 git 缺少 git-remote-https，
    任何 https 推送都会报 `git: 'remote-https' is not a git command`。
    所以这里走 GitHub 官方 REST API（Git Data API），一次调用等于一次 push。
#>
param(
    [string]$Note = "",
    [string]$Remote = "https://github.com/qian1586/-AI-SOP.git",
    [string]$Branch = "main",
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'
$Root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Script = Join-Path $PSScriptRoot 'upload-to-github.py'

function Say([string]$m)  { Write-Host $m }
function Ok([string]$m)   { Write-Host $m -ForegroundColor Green }
function Warn([string]$m) { Write-Host $m -ForegroundColor Yellow }
function Err([string]$m)  { Write-Host $m -ForegroundColor Red }

Say "=============================================="
Say " 上传到 GitHub：$Remote"
Say " 目录：$Root"
Say "=============================================="

if (-not (Test-Path -LiteralPath $Script)) {
    Err "找不到上传程序：$Script"
    Err "（部署包可能被拷漏了文件，请重新解压一份完整的）"
    exit 1
}

# ---------------------------------------------------------------- ① 找 Python
function Find-Python {
    $candidates = New-Object System.Collections.Generic.List[string]

    # Codex 自带运行时（这台机器上一定有）
    $candidates.Add((Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'))

    # 用户自己装的 Python
    foreach ($pattern in @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Python\Python*\python.exe'),
        'C:\Python*\python.exe',
        'C:\Program Files\Python*\python.exe')) {
        foreach ($hit in @(Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue)) {
            $candidates.Add($hit.FullName)
        }
    }

    # PATH 上的
    foreach ($name in @('python', 'python3')) {
        $cmd = Get-Command $name -ErrorAction SilentlyContinue
        if ($cmd) { $candidates.Add($cmd.Source) }
    }

    foreach ($path in $candidates) {
        if (-not $path) { continue }
        if (-not (Test-Path -LiteralPath $path)) { continue }

        try {
            $ver = & $path --version 2>&1
            if ($LASTEXITCODE -eq 0 -and "$ver" -match 'Python 3') { return @($path, @()) }
        } catch { }
    }

    # py 启动器是特例：要写成 "py -3" 两个词
    $pyLauncher = Get-Command 'py' -ErrorAction SilentlyContinue
    if ($pyLauncher) {
        try {
            $ver = & $pyLauncher.Source -3 --version 2>&1
            if ($LASTEXITCODE -eq 0) { return @($pyLauncher.Source, @('-3')) }
        } catch { }
    }

    return $null
}

$python = Find-Python
if (-not $python) {
    Err ""
    Err "没找到 Python 3。上传程序需要它（本机自带的那个通常就够了）。"
    Err "请把这个窗口截图发给开发人员。"
    Err ""
    exit 1
}

$pythonExe = $python[0]
$pythonArgs = @($python[1])
Say "使用 Python：$pythonExe $($pythonArgs -join ' ')"

# ---------------------------------------------------------------- ② 拿凭据
$token = $env:GH_TOKEN
if ([string]::IsNullOrWhiteSpace($token)) { $token = $env:GITHUB_TOKEN }

if ([string]::IsNullOrWhiteSpace($token)) {
    $tokenFile = Join-Path $Root '.github-token'
    if (Test-Path -LiteralPath $tokenFile) {
        $token = (Get-Content -LiteralPath $tokenFile -TotalCount 1).Trim()
    }
}

if ([string]::IsNullOrWhiteSpace($token)) {
    # 找 gh：PATH 上没有就去常见安装路径找
    $gh = Get-Command gh -ErrorAction SilentlyContinue
    $ghPath = if ($gh) { $gh.Source } else { $null }
    if (-not $ghPath) {
        foreach ($p in @('C:\Program Files\GitHub CLI\gh.exe',
                         (Join-Path $env:LOCALAPPDATA 'Programs\GitHub CLI\gh.exe'))) {
            if (Test-Path -LiteralPath $p) { $ghPath = $p; break }
        }
    }

    if ($ghPath) {
        $loggedIn = $false
        try {
            & $ghPath auth status 2>&1 | Out-Null
            $loggedIn = ($LASTEXITCODE -eq 0)
        } catch { $loggedIn = $false }

        if (-not $loggedIn) {
            Warn ""
            Warn "还没登录 GitHub —— 现在引导你登录一次（只需这一次，以后长期有效）。"
            Warn "接下来浏览器会自动打开一个页面，把页面上那串 8 位代码填进去即可。"
            Warn ""
            & $ghPath auth login --hostname github.com --git-protocol https --web --skip-ssh-key
        }

        try { $token = (& $ghPath auth token 2>$null | Select-Object -First 1).Trim() } catch { }
    }
}

if ([string]::IsNullOrWhiteSpace($token)) {
    Warn ""
    Warn "没有拿到 GitHub 凭据。"
    Warn "如果你已经在浏览器里登录了 GitHub，可以直接粘贴一个访问令牌（Personal access token）："
    $secure = Read-Host -AsSecureString "  粘贴令牌后回车（没有就直接回车跳过）"
    if ($secure) {
        $bstr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try   { $token = [Runtime.InteropServices.Marshal]::PtrToStringBSTR($bstr) }
        finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($bstr) }

        if (-not [string]::IsNullOrWhiteSpace($token)) {
            Set-Content -LiteralPath (Join-Path $Root '.github-token') -Value $token.Trim() -Encoding ASCII
            Ok "令牌已保存到 .github-token（这个文件不会被上传）。"
        }
    }
}

if ([string]::IsNullOrWhiteSpace($token)) {
    Err ""
    Err "上传需要 GitHub 授权，没有授权就无法上传。"
    Err "最简单的方式：先打开 https://cli.github.com/ 装一次 GitHub CLI，"
    Err "然后在命令行执行 gh auth login，再双击本脚本。"
    Err ""
    exit 1
}

$env:GH_TOKEN = $token.Trim()
Ok "GitHub 凭据就绪。"

# ---------------------------------------------------------------- ③ 上传
Say ""
Say "开始上传…"
Say ""

$pyArgList = @()
$pyArgList += $pythonArgs
$pyArgList += @($Script)
if ($DryRun) { $pyArgList += '--dry-run' }
if (-not [string]::IsNullOrWhiteSpace($Note)) { $pyArgList += @('--note', $Note) }

& $pythonExe @pyArgList
$code = $LASTEXITCODE

Say ""
if ($code -eq 0) {
    Ok "=============================================="
    Ok " 上传完成：$Remote"
    Ok " 版本号已自动 +0.1（见本目录 .version）"
    Ok "=============================================="
    exit 0
}

Err "=============================================="
Err " 上传失败（退出码 $code）。常见原因与处理："
Err ""
Err " ① 没登录 / 登录过期 → 重新执行：gh auth login"
Err " ② 没有写权限       → 确认仓库 $Remote 存在，且当前账号有 Write 权限"
Err " ③ 连不上 GitHub    → 检查这台电脑能不能上网；公司网络可能挡了 github.com"
Err ""
Err " 把上面那段红字截图发给开发人员即可。"
Err "=============================================="
exit 1
