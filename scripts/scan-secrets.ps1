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
param([switch]$SelfTest)
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

# Report locations and rule names only. A real secret must never be copied
# into CI logs by the scanner intended to detect it.
$ruleNames = @('credential-literal', 'secret-key-literal', 'private-key-header')
function Get-ScanFindings([string]$scanRoot, [scriptblock]$Reader) {
    foreach ($f in (Get-TextFiles $scanRoot)) {
        $rel = $f.Substring($scanRoot.Length + 1)
        try { $text = & $Reader $f } catch {
            # Do not print exception messages or file contents.
            [pscustomobject]@{ File = $rel; Line = 0; Rule = 'read-failure' }
            continue
        }
        for ($ruleIndex = 0; $ruleIndex -lt $patterns.Count; $ruleIndex++) {
            foreach ($m in [regex]::Matches([string]$text, $patterns[$ruleIndex])) {
                $v = $m.Groups['v'].Value
                # Exact fixture/structural literal only; a permitted substring
                # must not exempt a different credential.
                if ($v -and $allow -contains $v) { continue }
                $line = ([regex]::Matches($text.Substring(0, $m.Index), "`n")).Count + 1
                [pscustomobject]@{ File = $rel; Line = $line; Rule = $ruleNames[$ruleIndex] }
            }
        }
    }
}

if ($SelfTest) {
    $fixture = Join-Path ([IO.Path]::GetTempPath()) ('scan-audit-' + [Guid]::NewGuid().ToString('N'))
    [void](New-Item -ItemType Directory -Path $fixture)
    try {
        # Assemble inert fixture text so the repository scan does not
        # detect its own test source. Nothing here is a real credential.
        $key = 'pass' + 'word'
        $allowed = 'dummy-' + 'pass'
        $other = $allowed + '-fixture'
        $file = Join-Path $fixture 'fixture.txt'
        $read = { param($p) [IO.File]::ReadAllText($p) }
        [IO.File]::WriteAllText($file, ($key + ' = "' + $allowed + '"'))
        if (@(Get-ScanFindings $fixture $read).Count -ne 0) { throw 'Scanner exact allow regression' }
        [IO.File]::WriteAllText($file, ('first' + "`n" + $key + ' = "' + $other + '"'))
        $hits = @(Get-ScanFindings $fixture $read)
        if ($hits.Count -ne 1 -or $hits[0].Rule -cne 'credential-literal' -or
            $hits[0].Line -ne 2 -or $hits[0].File -cne 'fixture.txt') { throw 'Scanner substring regression' }
        if (($hits | ConvertTo-Json -Compress).Contains($other)) { throw 'Scanner redaction regression' }
        $hits = @(Get-ScanFindings $fixture { param($p) throw 'synthetic read failure' })
        if ($hits.Count -ne 1 -or $hits[0].Rule -cne 'read-failure') { throw 'Scanner unreadable file regression' }
        Write-Host 'SECRET_SCAN_SELFTEST_PASS cases=4'
        exit 0
    } finally {
        Remove-Item -LiteralPath $fixture -Recurse -Force
    }
}

$findings = @(Get-ScanFindings $root { param($p) [IO.File]::ReadAllText($p) })
if ($findings.Count -gt 0) {
    Write-Host '发现疑似敏感信息或无法完成的文件读取：'
    $findings | Format-Table -AutoSize | Out-String | Write-Host
    exit 1
}
Write-Host '敏感信息扫描：未发现命中。'
exit 0
