<#
.SYNOPSIS
对仓库内文本文件做敏感信息启发式扫描（CI 步骤之一）。

.DESCRIPTION
扫描疑似真实凭据的字面量（password/secret/token/key 赋值、私钥块）。
启发式扫描：能发现粗心的泄漏，不能证明"不存在敏感信息"；排除项集中在
测试假数据（dummy-*）与结构性常量。命中即退出码 1。
枚举采用手动递归+剪枝，避免 PowerShell 5.1 对深目录全量递归的稳定性问题，
也避免无谓地遍历 .git 等目录。
#>
param()
$ErrorActionPreference = 'Stop'
$root = (Get-Item -LiteralPath (Join-Path $PSScriptRoot '..')).FullName

$excludeDirs = @('.git', 'third-party', 'third-party-test', 'build-core', '.mimosa', '.zcode', 'node_modules')
$skipExt = @('.exe', '.ico', '.dll', '.png', '.jpg', '.dat', '.gz')
$allow = @(
    'dummy-user', 'dummy-pass', 'dummy-secret-', 'another-dummy-password', '测试12',   # 自测假数据
    'CheZhiTech.CampusNetwork.Credentials.v1',               # DPAPI 熵常量
    'ESURFING_CONTROL_TOKEN', 'control-token', 'control_token', 'controltoken', 'web_external_acc' # 参数名/环境变量名/配置键
)

function Get-TextFiles([string]$dir) {
    foreach ($item in [IO.Directory]::EnumerateFileSystemEntries($dir)) {
        $name = [IO.Path]::GetFileName($item)
        if ([IO.Directory]::Exists($item)) {
            if ($excludeDirs -notcontains $name) { Get-TextFiles $item }
        } else {
            if ($skipExt -notcontains [IO.Path]::GetExtension($name).ToLower()) { $item }
        }
    }
}

$patterns = @(
    '(?i)(password|passwd|pwd)\s*[=:]\s*["''](?<v>[^"'']{6,})["'']',
    '(?i)(secret|api[_-]?key|access[_-]?key)\s*[=:]\s*["''](?<v>[^"'']{10,})["'']',
    '-----BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY-----'
)

$findings = @()
foreach ($f in (Get-TextFiles $root)) {
    $text = $null
    try { $text = [IO.File]::ReadAllText($f) } catch { continue }
    foreach ($p in $patterns) {
        foreach ($m in [regex]::Matches($text, $p)) {
            $v = $m.Groups['v'].Value
            $hit = $true
            foreach ($a in $allow) { if ($v -and $v.IndexOf($a, [StringComparison]::OrdinalIgnoreCase) -ge 0) { $hit = $false; break } }
            if ($hit) {
                $rel = $f.Substring($root.Length + 1)
                $line = ([regex]::Matches($text.Substring(0, $m.Index), "`n")).Count + 1
                $findings += [pscustomobject]@{ File = $rel; Line = $line; Value = $v }
            }
        }
    }
}

if ($findings.Count -gt 0) {
    Write-Host '发现疑似敏感信息：'
    $findings | Format-Table -AutoSize | Out-String | Write-Host
    exit 1
}
Write-Host '敏感信息扫描：未发现命中。'
exit 0
