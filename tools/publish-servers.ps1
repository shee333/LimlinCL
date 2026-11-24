#Requires -Version 5.1
<#
.SYNOPSIS
    校验、递增版本号并（可选）上传社区服务器列表到腾讯云 COS。

.DESCRIPTION
    管理员发布服务器列表的辅助脚本：
      1. 校验 servers.json 结构（id 唯一、ip/端口合法、必填字段齐全）
      2. -Bump 时自动把 revision +1 并刷新 updatedAt
      3. 输出 SHA256（可用于发布页公示）
      4. -Upload 时调用 coscli 上传到 COS（需已配置 coscli 与桶信息）

    没有 coscli 时，脚本会打印上传须知，可手动在 COS 控制台拖拽上传。

.EXAMPLE
    .\publish-servers.ps1 -Path .\servers.json
    只校验并打印信息。

.EXAMPLE
    .\publish-servers.ps1 -Path .\servers.json -Bump
    校验后把 revision 加一并写回文件。

.EXAMPLE
    .\publish-servers.ps1 -Path .\servers.json -Bump -Upload
    校验、递增版本号并上传到 COS。

.NOTES
    上传需要的环境变量：
      $env:LIMLIN_COS_BUCKET = "bucket-1250000000"     # 存储桶名（含 APPID）
      $env:LIMLIN_COS_KEY    = "launcher/community/servers.json"  # 对象键
      $env:LIMLIN_COS_URL    = "https://bucket-1250000000.cos.ap-guangzhou.myqcloud.com/launcher/community/servers.json"
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Path,
    [switch]$Bump,
    [switch]$Upload
)

$ErrorActionPreference = 'Stop'

function Write-Step([string]$text) { Write-Host ">> $text" -ForegroundColor Cyan }
function Write-Ok([string]$text) { Write-Host "   $text" -ForegroundColor Green }

if (-not (Test-Path $Path)) { throw "文件不存在：$Path" }

Write-Step "读取并校验 $Path"
$raw = [System.IO.File]::ReadAllText((Resolve-Path $Path), [System.Text.Encoding]::UTF8)
try { $json = $raw | ConvertFrom-Json } catch { throw "JSON 语法错误：$($_.Exception.Message)" }

# ---- 结构校验 ----
$errors = @()
if ($null -eq $json.servers) { $errors += '缺少 servers 数组' }
if ($null -eq $json.revision) { $errors += '缺少 revision' }
elseif ([int]$json.revision -le 0) { $errors += 'revision 必须为正整数' }
if ($json.schemaVersion -and [int]$json.schemaVersion -gt 1) { $errors += "schemaVersion 高于客户端支持（当前支持 1）" }

$ids = @{}
$index = 0
foreach ($server in @($json.servers)) {
    $index++
    $label = "第 $index 个服务器"
    if ([string]::IsNullOrWhiteSpace($server.id)) { $errors += "$label 缺少 id" }
    elseif ($ids.ContainsKey($server.id)) { $errors += "id 重复：$($server.id)" }
    else { $ids[$server.id] = $true }
    if ([string]::IsNullOrWhiteSpace($server.ip)) { $errors += "$label 缺少 ip" }
    if ([string]::IsNullOrWhiteSpace($server.name)) { $errors += "$label 缺少 name" }
    if ($null -ne $server.port) {
        $port = [int]$server.port
        if ($port -le 0 -or $port -gt 65535) { $errors += "$label 端口非法：$port" }
    }
}

if ($errors.Count -gt 0) {
    Write-Host ''
    Write-Host '校验失败：' -ForegroundColor Red
    $errors | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
    exit 1
}
Write-Ok "校验通过：$($json.servers.Count) 台服务器，revision $($json.revision)"

# ---- 递增版本号 ----
if ($Bump) {
    $json.revision = [int]$json.revision + 1
    $json.updatedAt = (Get-Date).ToString('yyyy-MM-ddTHH:mm:sszzz')
    $json | ConvertTo-Json -Depth 10 | Set-Content -Path $Path -Encoding UTF8
    Write-Ok "revision 已递增到 $($json.revision)，updatedAt 已刷新"
}

# ---- 输出摘要 ----
$file = Get-Item $Path
$hash = (Get-FileHash $Path -Algorithm SHA256).Hash
Write-Host ''
Write-Host "文件：$($file.FullName)"
Write-Host "大小：$([math]::Round($file.Length / 1KB, 2)) KB"
Write-Host "revision：$($json.revision)"
Write-Host "SHA256：$hash"
Write-Host ''
Write-Host '服务器清单：'
foreach ($server in @($json.servers)) {
    $state = if ($server.enabled -eq $false) { '（已下架）' } else { '' }
    $addr = if ($server.port) { "$($server.ip):$($server.port)" } elseif ($server.useSrv -ne $false) { "$($server.ip)（SRV）" } else { "$($server.ip):25565" }
    Write-Host ("  - {0}  {1}  {2}{3}" -f $server.name, $addr, $server.group, $state)
}
Write-Host ''

# ---- 上传到 COS ----
if ($Upload) {
    Write-Step '上传到腾讯云 COS'
    $bucket = $env:LIMLIN_COS_BUCKET
    $key = if ($env:LIMLIN_COS_KEY) { $env:LIMLIN_COS_KEY } else { 'launcher/community/servers.json' }
    if (-not $bucket) { throw '未设置 $env:LIMLIN_COS_BUCKET（形如 bucket-1250000000）' }
    $coscli = Get-Command coscli -ErrorAction SilentlyContinue
    if (-not $coscli) {
        Write-Host '   未找到 coscli，手动上传方法：' -ForegroundColor Yellow
        Write-Host "   1. 打开 COS 控制台 → 存储桶 $bucket → 对象存储 → 上传"
        Write-Host "   2. 上传到 $key（覆盖旧文件）"
        Write-Host '   3. 建议设置响应头 Cache-Control: max-age=60'
        Write-Host '   安装 coscli：https://cloud.tencent.com/document/product/436/63144'
        exit 0
    }
    & coscli cp $Path "cos://$bucket/$key" 2>&1 | ForEach-Object { "   $_" }
    if ($LASTEXITCODE -ne 0) { throw "coscli 上传失败（exit $LASTEXITCODE）" }
    Write-Ok '上传完成'
    if ($env:LIMLIN_COS_URL) {
        Write-Step '回读校验'
        try {
            $remote = (Invoke-WebRequest -Uri $env:LIMLIN_COS_URL -UseBasicParsing -TimeoutSec 20).Content
            $remoteJson = $remote | ConvertFrom-Json
            if ([int]$remoteJson.revision -eq [int]$json.revision) { Write-Ok "回读成功，线上 revision = $($remoteJson.revision)" }
            else { Write-Host "   回读 revision 不一致：线上 $($remoteJson.revision)，期望 $($json.revision)（可能 CDN 缓存未刷新）" -ForegroundColor Yellow }
        } catch {
            Write-Host "   回读失败：$($_.Exception.Message)" -ForegroundColor Yellow
        }
    }
}
