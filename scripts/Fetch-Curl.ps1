<#
.SYNOPSIS
获取并校验 libcurl 源码包（固定版本 + 固定来源 + 固定 SHA-256）。

.DESCRIPTION
- 已缓存且哈希一致：直接复用（支持离线构建）。
- 未缓存：从官方地址下载，校验 SHA-256 后落盘。
- 哈希不一致：删除下载文件并终止，绝不跳过校验。

.EXAMPLE
./scripts/Fetch-Curl.ps1                       # 下载/校验到 third-party\
./scripts/Fetch-Curl.ps1 -OutputDir D:\deps    # 自定义缓存目录
#>
param(
    [string]$Version = '8.23.0',
    [string]$OutputDir = '',
    [string]$SourceUrl = ''
)
$ErrorActionPreference = 'Stop'

# 固定依赖三元组：版本 / 来源 / SHA-256（与本地长期使用的源码包逐字节核对过，
# 亦可用官方 .asc 签名复核）。升级版本必须同步更新此处并重新核验哈希。
$expectedSha256 = 'E3DE77CFA6367083C776A26647FC9F633B538C06C3BAA73D931A252AC8F72B49'
if (-not $SourceUrl) { $SourceUrl = "https://curl.se/download/curl-$Version.tar.gz" }

$archiveName = "curl-$Version.tar.gz"
$legacyAllowed = -not $PSBoundParameters.ContainsKey('OutputDir')
if (-not $OutputDir) { $OutputDir = Join-Path $PSScriptRoot '..\third-party' }
$OutputDir = [IO.Path]::GetFullPath($OutputDir)
$target = Join-Path $OutputDir $archiveName

# 旧版布局兼容：未显式指定缓存目录时，仓库根目录若已有同名包（历史来源），直接作为缓存使用
$legacy = Join-Path (Split-Path -Parent $PSScriptRoot) $archiveName
if ($legacyAllowed -and -not (Test-Path -LiteralPath $target -PathType Leaf) -and (Test-Path -LiteralPath $legacy -PathType Leaf)) {
    Write-Host "使用仓库根目录已有源码包：$legacy"
    $target = $legacy
}

function Test-Archive([string]$path) {
    if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -ne $expectedSha256) {
        throw "curl $Version 源码包 SHA-256 不符（期望 $expectedSha256）：$path。已拒绝使用。"
    }
}

if (Test-Path -LiteralPath $target -PathType Leaf) {
    Test-Archive $target
    Write-Host "依赖已缓存且校验通过：$target"
    Write-Output $target
    exit 0
}

[void](New-Item -ItemType Directory -Path $OutputDir -Force)
Write-Host "下载 $SourceUrl ..."
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    Invoke-WebRequest -Uri $SourceUrl -OutFile $target -UseBasicParsing
} catch {
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue }
    throw "下载失败：$($_.Exception.Message)。可手动放置 $archiveName 到 $OutputDir 后重试（离线构建）。"
}
try { Test-Archive $target }
catch {
    Remove-Item -LiteralPath $target -Force -ErrorAction SilentlyContinue
    throw
}
Write-Host "下载完成，SHA-256 校验通过：$target"
Write-Output $target
