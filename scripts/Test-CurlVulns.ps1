<#
.SYNOPSIS
对照 curl 官方漏洞数据库（https://curl.se/docs/vuln.json）精确检查固定版本的已知 CVE。

.DESCRIPTION
按 OSV 语义评估 affected 区间（introduced <= V < fixed，含开放式区间）、
显式 versions 列表与 last_affected 兜底；同时报告"提到该版本但不受影响"
（例如在该版本修复）的条目数量，避免子串误报。结果作为人工复核证据，
不阻断构建（CI 中 continue-on-error）。
#>
param(
    [string]$Version = '8.23.0',
    [string]$VulnUrl = 'https://curl.se/docs/vuln.json'
)
$ErrorActionPreference = 'Stop'

function Compare-Semver([string]$a, [string]$b) {
    # curl 版本均为数字点分（如 8.23.0），逐段数值比较；非数字段按字符串比较
    $pa = $a -split '\.'
    $pb = $b -split '\.'
    for ($i = 0; $i -lt [Math]::Max($pa.Count, $pb.Count); $i++) {
        $x = if ($i -lt $pa.Count) { $pa[$i] } else { '0' }
        $y = if ($i -lt $pb.Count) { $pb[$i] } else { '0' }
        $nx = 0; $ny = 0
        $bothNum = [int]::TryParse($x, [ref]$nx) -and [int]::TryParse($y, [ref]$ny)
        $cmp = if ($bothNum) { $nx.CompareTo($ny) } else { [string]::Compare($x, $y) }
        if ($cmp -ne 0) { return $cmp }
    }
    return 0
}

function Test-EntryAffected($entry, [string]$v) {
    foreach ($aff in @($entry.affected)) {
        if ($null -eq $aff) { continue }
        # 显式版本列表
        if ($aff.versions -and (@($aff.versions) -contains $v)) { return $true }
        # OSV 区间: introduced <= v < fixed（成对消费事件流，fixed 缺失=开放式）
        foreach ($range in @($aff.ranges)) {
            if ($null -eq $range) { continue }
            $introduced = $null
            foreach ($ev in @($range.events)) {
                if ($null -eq $ev) { continue }
                if ($ev.PSObject.Properties['introduced']) {
                    if ($introduced -ne $null -and (Compare-Semver $introduced $v) -le 0) { return $true } # 开放式上一段
                    $introduced = [string]$ev.introduced
                } elseif ($ev.PSObject.Properties['fixed']) {
                    if ($introduced -ne $null -and (Compare-Semver $introduced $v) -le 0 -and (Compare-Semver $v ([string]$ev.fixed)) -lt 0) { return $true }
                    $introduced = $null
                } elseif ($ev.PSObject.Properties['last_affected']) {
                    if ($introduced -ne $null -and (Compare-Semver $introduced $v) -le 0 -and (Compare-Semver $v ([string]$ev.last_affected)) -le 0) { return $true }
                    $introduced = $null
                }
            }
            if ($introduced -ne $null -and (Compare-Semver $introduced $v) -le 0) { return $true } # 区间未闭合
        }
    }
    # database_specific.last_affected 兜底（无区间数据时）
    if ($entry.database_specific -and $entry.database_specific.last_affected) {
        if ((Compare-Semver $v ([string]$entry.database_specific.last_affected)) -le 0) { return $true }
    }
    return $false
}

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $raw = (Invoke-WebRequest -Uri $VulnUrl -UseBasicParsing).Content
    $vulns = ConvertFrom-Json $raw
    $affected = @()
    $mentioned = 0
    foreach ($v in $vulns) {
        if (Test-EntryAffected $v $Version) { $affected += $v }
        elseif (($v | ConvertTo-Json -Depth 8 -Compress) -match [regex]::Escape($Version)) { $mentioned++ }
    }
    Write-Host "curl $Version 已知漏洞核查（OSV 区间精确判定）：受影响 $($affected.Count) 条"
    foreach ($v in $affected) {
        $sev = if ($v.database_specific -and $v.database_specific.severity) { $v.database_specific.severity } else { '?' }
        $sum = if ($v.summary) { $v.summary } else { '' }
        Write-Host ("  - {0} [{1}] {2}" -f $v.id, $sev, $sum)
    }
    if ($mentioned -gt 0) { Write-Host "另有 $mentioned 条记录提及该版本但按区间判定不受影响（多为其修复版本或无关区间）。" }
    if ($affected.Count -gt 0) { Write-Host '请人工核对上述条目是否影响本项目的使用面（纯 HTTP、Schannel、静态链接、无代理/SSH/多协议）。' }
    else { Write-Host '官方漏洞库中未发现影响该版本的记录。' }
} catch {
    Write-Host "漏洞库获取失败（不阻断构建）：$($_.Exception.Message)"
}
exit 0
