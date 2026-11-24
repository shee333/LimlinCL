#Requires -Version 5.1
<#
.SYNOPSIS
    双击即用：编译 LimlinCL 并把单文件 exe 输出到 D:\MiencraftDEV\LimCL\product。

.DESCRIPTION
    流程：
      1. 从 Plain Craft Launcher 2\Modules\Base\ModBase.vb 读取当前版本号
      2. 若 product 目录下已存在同名产物，则自动把 patch 版本号 +1（0.1.0 -> 0.1.1），
         直到产物文件名不冲突，因此每次运行都会得到一个全新版本，不会覆盖旧包
      3. 用 VS2022 的 MSBuild 编译 Release
      4. 把 LimlinLauncher.exe 复制到 product 目录，命名为 LimlinLauncher-<版本>.exe

    版本号改动会写回 ModBase.vb（VersionBaseName 与 VersionCode），方便你随后一起提交。

.EXAMPLE
    .\build-release.ps1
    编译并输出新版本。

.EXAMPLE
    .\build-release.ps1 -NoBump
    不检查冲突、不自动升版本，直接用当前版本号输出（会覆盖同名产物）。

.EXAMPLE
    .\build-release.ps1 -OpenFolder
    构建完成后打开 product 目录。
#>
[CmdletBinding()]
param(
    [switch]$NoBump,
    [switch]$OpenFolder,
    [string]$ProductDir = 'D:\MiencraftDEV\LimCL\product'
)

$ErrorActionPreference = 'Stop'

# ---- 路径 -------------------------------------------------------------
$Script:Repo      = Split-Path -Parent $PSScriptRoot       # tools 的上一级即仓库根
$Script:Project   = Join-Path $Script:Repo 'Plain Craft Launcher 2\Plain Craft Launcher 2.vbproj'
$Script:Solution  = Join-Path $Script:Repo 'Plain Craft Launcher 2.sln'
$Script:ModBase   = Join-Path $Script:Repo 'Plain Craft Launcher 2\Modules\Base\ModBase.vb'
$Script:OutputExe = Join-Path $Script:Repo 'Plain Craft Launcher 2\bin\LimlinLauncher.exe'
$Script:AppName   = 'LimlinLauncher'

function Write-Step([string]$text) { Write-Host ">> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text)   { Write-Host "   $text" -ForegroundColor Green }

# ---- 找 MSBuild -------------------------------------------------------
function Resolve-MSBuild {
    $candidates = @(
        'D:\VSBuild2022\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Community\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Professional\MSBuild\Current\Bin\MSBuild.exe',
        'C:\Program Files\Microsoft Visual Studio\2022\Enterprise\MSBuild\Current\Bin\MSBuild.exe'
    )
    foreach ($c in $candidates) { if (Test-Path $c) { return $c } }

    $vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
    if (Test-Path $vswhere) {
        $found = & $vswhere -latest -products * -requires Microsoft.Component.MSBuild -find 'MSBuild\**\Bin\MSBuild.exe' 2>$null | Select-Object -First 1
        if ($found -and (Test-Path $found)) { return $found }
    }
    throw '未找到 MSBuild 17。请安装 VS2022 Build Tools（.NET 桌面生成工具）后重试。'
}

# ---- 版本号读写（保持文件原编码） --------------------------------------
function Read-Version {
    $bytes = [System.IO.File]::ReadAllBytes($Script:ModBase)
    $hasBom = ($bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF)
    $enc = New-Object System.Text.UTF8Encoding($false, $true)
    $offset = if ($hasBom) { 3 } else { 0 }
    $text = $enc.GetString($bytes, $offset, $bytes.Length - $offset)
    $m = [regex]::Match($text, 'Public Const VersionBaseName As String = "([0-9]+(?:\.[0-9]+)+)"')
    if (-not $m.Success) { throw '未能从 ModBase.vb 解析版本号' }
    return [pscustomobject]@{ Version = $m.Groups[1].Value; Bom = $hasBom }
}

function Set-Version([string]$oldVersion, [string]$newVersion, [bool]$hasBom) {
    $bytes = [System.IO.File]::ReadAllBytes($Script:ModBase)
    $enc = New-Object System.Text.UTF8Encoding($false, $true)
    $offset = if ($hasBom) { 3 } else { 0 }
    $text = $enc.GetString($bytes, $offset, $bytes.Length - $offset)

    $text = $text.Replace(
        "Public Const VersionBaseName As String = `"$oldVersion`"",
        "Public Const VersionBaseName As String = `"$newVersion`"")

    # 内部版本代号同步 +1（Release / 非 Release 两处）
    $text = [regex]::Replace($text, '(Public Const VersionCode As Integer = )(\d+)', {
        param($m) $m.Groups[1].Value + ([int]$m.Groups[2].Value + 1)
    })

    [System.IO.File]::WriteAllText($Script:ModBase, $text, (New-Object System.Text.UTF8Encoding($hasBom)))
}

function Get-NextVersion([string]$version) {
    $parts = $version.Split('.')
    while ($parts.Count -lt 3) { $parts += '0' }
    $parts[$parts.Count - 1] = [string]([int]$parts[$parts.Count - 1] + 1)
    return ($parts -join '.')
}

# ---- 主流程 -----------------------------------------------------------
if (-not (Test-Path $Script:Project))  { throw "未找到项目文件：$Script:Project" }
if (-not (Test-Path $Script:ModBase))  { throw "未找到 ModBase.vb：$Script:ModBase" }

if (-not (Test-Path $ProductDir)) { New-Item -ItemType Directory -Force -Path $ProductDir | Out-Null }

$msbuild = Resolve-MSBuild

# 读取版本；若产物已存在则自动升版本
$vi = Read-Version
$version = $vi.Version
if (-not $NoBump) {
    $guard = 0
    while (Test-Path (Join-Path $ProductDir "$Script:AppName-$version.exe")) {
        $next = Get-NextVersion $version
        Set-Version $version $next $vi.Bom
        Write-Host "   检测到 $($Script:AppName)-$version.exe 已存在，版本号自动升为 $next" -ForegroundColor Yellow
        $version = $next
        $guard++
        if ($guard -gt 100) { throw '自动升版本次数过多，请检查 product 目录' }
    }
}

Write-Step "编译 $($Script:AppName) $version（Release）"
$buildStart = Get-Date
$env:NUGET_PACKAGES    = 'D:\MiencraftDEV\.nuget-packages'
$env:DOTNET_CLI_HOME   = 'D:\MiencraftDEV\.dotnet-home'

$logFile = Join-Path $env:TEMP 'limlin-build.log'
# 注意：必须直接编译 vbproj 且不传 Platform。解决方案里的 "Any CPU" 平台名带空格，
# 会让项目内 'Release|AnyCPU' 的配置条件失配，导致编译成快照版（无 RELEASE 常量）。
& $msbuild $Script:Project /p:Configuration=Release /m /v:m /nologo 2>&1 |
    Tee-Object -FilePath $logFile | Out-Null

if ($LASTEXITCODE -ne 0) {
    Write-Host ''
    Write-Host '编译失败，错误摘要：' -ForegroundColor Red
    Get-Content $logFile | Select-String -Pattern 'error|错误' | Select-Object -First 20 | ForEach-Object { Write-Host ("  " + $_.Line) -ForegroundColor Red }
    Write-Host ''
    Write-Host "完整日志：$logFile" -ForegroundColor Yellow
    exit 1
}

if (-not (Test-Path $Script:OutputExe)) { throw "编译成功但未找到产物：$Script:OutputExe" }
if ((Get-Item $Script:OutputExe).LastWriteTime -lt $buildStart.AddMinutes(-1)) {
    # 增量构建在源码无改动时会跳过编译，exe 时间戳保持不变，这是正常情况
    Write-Host "   源码无改动，MSBuild 跳过重新编译，直接复用现有产物" -ForegroundColor DarkGray
}

$target = Join-Path $ProductDir "$Script:AppName-$version.exe"
Copy-Item $Script:OutputExe $target -Force
$size = [math]::Round((Get-Item $target).Length / 1MB, 2)

Write-Host ''
Write-Ok "版本 $version"
Write-Ok "产物 $target  ($size MB)"
Write-Host ''
Write-Host "提示：版本号已写入 ModBase.vb（VersionBaseName / VersionCode），记得随代码一起提交。" -ForegroundColor DarkGray

if ($OpenFolder) { Start-Process explorer.exe $ProductDir }
exit 0
