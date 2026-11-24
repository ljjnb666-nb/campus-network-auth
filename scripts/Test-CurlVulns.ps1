<#
.SYNOPSIS
对照 curl 官方漏洞数据库（https://curl.se/docs/vuln.json）检查固定版本是否存在已知 CVE。

.DESCRIPTION
以"版本字符串是否出现在对应 CVE 记录中"为启发式判定，输出命中清单作为人工复核证据；
权威结论仍需对照官方安全页面。脚本不阻断构建（CI 中 continue-on-error）。
#>
param(
    [string]$Version = '8.20.0',
    [string]$VulnUrl = 'https://curl.se/docs/vuln.json'
)
$ErrorActionPreference = 'Stop'
try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $raw = (Invoke-WebRequest -Uri $VulnUrl -UseBasicParsing).Content
    $vulns = ConvertFrom-Json $raw
    $hits = @()
    foreach ($v in $vulns) {
        $blob = ($v | ConvertTo-Json -Depth 8 -Compress)
        if ($blob -match [regex]::Escape("`"$Version`"")) { $hits += $v.id }
        elseif ($blob -match [regex]::Escape($Version)) { $hits += $v.id }
    }
    Write-Host "curl $Version 已知漏洞核查：命中 $($hits.Count) 条"
    if ($hits.Count -gt 0) { $hits | ForEach-Object { Write-Host "  - $_" }; Write-Host '请人工核对上述 CVE 是否影响本项目的使用方式（静态链接、仅 HTTP+Schannel、不启用代理协议等）。' }
    else { Write-Host '官方漏洞库中未发现直接标记该版本的记录。' }
} catch {
    Write-Host "漏洞库获取失败（不阻断构建）：$($_.Exception.Message)"
}
exit 0
