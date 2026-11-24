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
$raw = [System.IO.File]::ReadAllText((Resolve-Path $Path), [System.Text.Encoding]::UTF8).TrimStart([char]0xFEFF)
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
    $jsonText = $json | ConvertTo-Json -Depth 10
    [System.IO.File]::WriteAllText((Resolve-Path $Path), $jsonText, (New-Object System.Text.UTF8Encoding($false)))
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

# ---- 上传到 COS（原生 REST API，无需 coscli） ----

function Get-CosAuthorization {
    param([string]$SecretId, [string]$SecretKey, [string]$HostName, [string]$UriPath)
    $start = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
    $keyTime = "$start;$($start + 900)"
    # SignKey = hex(HMAC-SHA1(SecretKey, KeyTime))
    $hmac1 = New-Object System.Security.Cryptography.HMACSHA1(,[Text.Encoding]::UTF8.GetBytes($SecretKey))
    $signKeyBytes = $hmac1.ComputeHash([Text.Encoding]::UTF8.GetBytes($keyTime))
    $signKey = (($signKeyBytes | ForEach-Object { $_.ToString('x2') }) -join '')
    # HttpString = method + "\n" + path + "\n" + params + "\n" + headers + "\n"
    $httpString = "put`n$UriPath`n`nhost=$HostName`n"
    $sha1 = [System.Security.Cryptography.SHA1]::Create()
    $httpHash = (($sha1.ComputeHash([Text.Encoding]::UTF8.GetBytes($httpString)) | ForEach-Object { $_.ToString('x2') }) -join '')
    $stringToSign = "sha1`n$keyTime`n$httpHash`n"
    $hmac2 = New-Object System.Security.Cryptography.HMACSHA1(,[Text.Encoding]::UTF8.GetBytes($signKey))
    $signature = (($hmac2.ComputeHash([Text.Encoding]::UTF8.GetBytes($stringToSign)) | ForEach-Object { $_.ToString('x2') }) -join '')
    return "q-sign-algorithm=sha1&q-ak=$SecretId&q-sign-time=$keyTime&q-key-time=$keyTime&q-header-list=host&q-url-param-list=&q-signature=$signature"
}

if ($Upload) {
    Write-Step '上传到腾讯云 COS'
    [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

    $cosPath = Join-Path $PSScriptRoot 'cos.local.json'
    $cos = $null
    if (Test-Path $cosPath) { $cos = Get-Content $cosPath -Raw -Encoding UTF8 | ConvertFrom-Json }

    $bucket    = if ($env:LIMLIN_COS_BUCKET)     { $env:LIMLIN_COS_BUCKET }     elseif ($cos) { $cos.bucket }    else { $null }
    $region    = if ($env:LIMLIN_COS_REGION)     { $env:LIMLIN_COS_REGION }     elseif ($cos) { $cos.region }    else { $null }
    $secretId  = if ($env:LIMLIN_COS_SECRET_ID)  { $env:LIMLIN_COS_SECRET_ID }  elseif ($cos) { $cos.secretId }  else { $null }
    $secretKey = if ($env:LIMLIN_COS_SECRET_KEY) { $env:LIMLIN_COS_SECRET_KEY } elseif ($cos) { $cos.secretKey } else { $null }
    $keyPrefix = if ($env:LIMLIN_COS_KEY_PREFIX) { $env:LIMLIN_COS_KEY_PREFIX } elseif ($cos) { $cos.keyPrefix } else { 'launcher/community/' }

    if (-not $bucket)    { throw '未配置 COS 存储桶（tools\cos.local.json 或 LIMLIN_COS_BUCKET）' }
    if (-not $region)    { throw '未配置 COS 地域（tools\cos.local.json 或 LIMLIN_COS_REGION）' }
    if (-not $secretId)  { throw '未配置 SecretId（tools\cos.local.json 或 LIMLIN_COS_SECRET_ID）' }
    if (-not $secretKey) { throw '未配置 SecretKey（tools\cos.local.json 或 LIMLIN_COS_SECRET_KEY）' }

    $fileName = Split-Path $Path -Leaf
    $objectKey = $keyPrefix.Trim('/') + '/' + $fileName
    $hostName = "$bucket.cos.$region.myqcloud.com"
    $encodedKey = (($objectKey -split '/') | ForEach-Object { [Uri]::EscapeDataString($_) }) -join '/'
    $uriPath = '/' + $encodedKey
    $url = "https://$hostName$uriPath"

    $authorization = Get-CosAuthorization -SecretId $secretId -SecretKey $secretKey -HostName $hostName -UriPath $uriPath
    $body = [System.IO.File]::ReadAllBytes((Resolve-Path $Path))
    $headers = @{
        Authorization   = $authorization
        'Content-Type'  = 'application/json; charset=utf-8'
        'Cache-Control' = 'max-age=60'
    }
    try {
        $response = Invoke-WebRequest -Uri $url -Method Put -Body $body -Headers $headers -UseBasicParsing -TimeoutSec 30
        Write-Ok "上传成功（HTTP $($response.StatusCode)）"
        Write-Host "   对象地址：$url"
    } catch {
        Write-Host "   上传失败：$($_.Exception.Message)" -ForegroundColor Red
        if ($_.Exception.Response -and $_.Exception.Response.StatusCode) {
            Write-Host "   HTTP $([int]$_.Exception.Response.StatusCode)" -ForegroundColor Red
        }
        Write-Host '   排查：子账号是否有该桶 launcher/community/* 的 PutObject 权限？' -ForegroundColor Yellow
        exit 1
    }

    Write-Step '回读校验（匿名 GET）'
    try {
        $remote = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 20).Content
        $remoteJson = ($remote -replace "^\uFEFF", "") | ConvertFrom-Json
        if ([int]$remoteJson.revision -eq [int]$json.revision) {
            Write-Ok "回读成功，线上 revision = $($remoteJson.revision)"
        } else {
            Write-Host "   回读 revision 不一致：线上 $($remoteJson.revision)，期望 $($json.revision)" -ForegroundColor Yellow
        }
    } catch {
        Write-Host "   回读失败：$($_.Exception.Message)" -ForegroundColor Red
        Write-Host '   若返回 403，请把存储桶权限设为「公有读私有写」' -ForegroundColor Yellow
        exit 1
    }
    Write-Host ''
    Write-Host "客户端会自动从该地址同步：$url" -ForegroundColor Green
}