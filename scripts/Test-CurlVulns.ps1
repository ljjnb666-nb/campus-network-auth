<#
.SYNOPSIS
Verify the pinned curl release against the official version-based advisory data.
.DESCRIPTION
Numeric release ranges only. Git commit ranges are not comparable to release
versions. Cross-check the result against curl's version-specific JSON feed.
Findings require release review; malformed data and disagreement fail CI.
#>
param(
    [string]$Version = '8.23.0',
    [string]$VulnUrl = 'https://curl.se/docs/vuln.json',
    [switch]$SelfTest
)
$ErrorActionPreference = 'Stop'

function Compare-Release([string]$a, [string]$b) {
    foreach ($value in @($a, $b)) {
        if ($value -notmatch '^\d+(\.\d+){0,3}$') { throw "Unsupported release version: $value" }
    }
    $pa = $a -split '\.'
    $pb = $b -split '\.'
    for ($i = 0; $i -lt [Math]::Max($pa.Count, $pb.Count); $i++) {
        $x = if ($i -lt $pa.Count) { [long]$pa[$i] } else { 0L }
        $y = if ($i -lt $pb.Count) { [long]$pb[$i] } else { 0L }
        $cmp = $x.CompareTo([long]$y)
        if ($cmp -ne 0) { return $cmp }
    }
    return 0
}

function Test-EntryAffected($entry, [string]$v) {
    $matched = $false
    $hasReleaseEvidence = $false
    foreach ($aff in @($entry.affected)) {
        if ($null -eq $aff) { continue }
        if ($aff.PSObject.Properties['versions'] -and $null -ne $aff.versions) {
            $hasReleaseEvidence = $true
            if (@($aff.versions) -contains $v) { $matched = $true }
        }
        foreach ($range in @($aff.ranges)) {
            if ($null -eq $range) { continue }
            if ($range.type -eq 'GIT') { continue }
            if ($range.type -ne 'SEMVER' -and $range.type -ne 'ECOSYSTEM') {
                throw "Unsupported range type in $($entry.id): $($range.type)"
            }
            $hasReleaseEvidence = $true
            $introduced = $null
            $events = @($range.events)
            if (-not $range.events -or $events.Count -eq 0) { throw "Empty range in $($entry.id)" }
            foreach ($ev in $events) {
                $keys = @($ev.PSObject.Properties.Name)
                if ($keys.Count -ne 1 -or $keys[0] -notin @('introduced','fixed','last_affected','limit')) {
                    throw "Invalid event in $($entry.id)"
                }
                $key = $keys[0]
                $value = [string]$ev.$key
                [void](Compare-Release $value $value)
                if ($key -eq 'introduced') {
                    if ($null -ne $introduced) { throw "Unclosed range in $($entry.id)" }
                    $introduced = $value
                } else {
                    if ($null -eq $introduced -or (Compare-Release $introduced $value) -gt 0) {
                        throw "Invalid range boundary in $($entry.id)"
                    }
                    $upper = Compare-Release $v $value
                    if ((Compare-Release $introduced $v) -le 0 -and
                        ($upper -lt 0 -or ($key -eq 'last_affected' -and $upper -eq 0))) { $matched = $true }
                    $introduced = $null
                }
            }
            if ($null -ne $introduced -and (Compare-Release $introduced $v) -le 0) { $matched = $true }
        }
    }
    # An upper bound alone cannot establish introduction. Do not invent an
    # affected range from database_specific.last_affected.
    if (-not $hasReleaseEvidence) { throw "No release-version evidence in $($entry.id)" }
    return $matched
}

function Assert-Affected($entry, [string]$version, [bool]$expected) {
    if ((Test-EntryAffected $entry $version) -ne $expected) {
        throw "Fixture mismatch: $($entry.id) $version expected=$expected"
    }
}

function Invoke-RangeTests {
    # Reduced fixtures using the shape of curl's public advisory JSON:
    # CVE-2023-38545: fixed at 8.4.0, alongside a nonnumeric Git range.
    $mixed = ConvertFrom-Json '{"id":"mixed","affected":[{"ranges":[{"type":"SEMVER","events":[{"introduced":"7.69.0"},{"fixed":"8.4.0"}]},{"type":"GIT","events":[{"introduced":"4a4b63daaa"},{"fixed":"fb4415d8aa"}]}],"versions":["8.3.0"]}],"database_specific":{"last_affected":"8.3.0"}}'
    Assert-Affected $mixed '8.23.0' $false
    Assert-Affected $mixed '8.4.0' $false
    Assert-Affected $mixed '8.3.0' $true
    Assert-Affected $mixed '7.68.0' $false
    # CVE-2026-107929: disjoint release intervals, fixed at 8.23.0.
    $multi = ConvertFrom-Json '{"id":"multi","affected":[{"ranges":[{"type":"SEMVER","events":[{"introduced":"8.9.0"},{"fixed":"8.14.3"},{"introduced":"8.15.0"},{"fixed":"8.16.2"},{"introduced":"8.17.0"},{"fixed":"8.20.2"},{"introduced":"8.21.0"},{"fixed":"8.23.0"}]}]}],"database_specific":{"last_affected":"8.22.0"}}'
    Assert-Affected $multi '8.23.0' $false
    Assert-Affected $multi '8.22.0' $true
    Assert-Affected $multi '8.14.3' $false
    Assert-Affected $multi '8.16.2' $false
    Assert-Affected $multi '8.17.0' $true
    $inclusive = ConvertFrom-Json '{"id":"inclusive","affected":[{"ranges":[{"type":"SEMVER","events":[{"introduced":"0"},{"last_affected":"8.2.0"}]}]}]}'
    Assert-Affected $inclusive '8.2.0' $true
    Assert-Affected $inclusive '8.2.1' $false
    $limit = ConvertFrom-Json '{"id":"limit","affected":[{"ranges":[{"type":"SEMVER","events":[{"introduced":"8.1.0"},{"limit":"8.3.0"}]}]}]}'
    Assert-Affected $limit '8.3.0' $false
    Assert-Affected $limit '8.2.0' $true
    $open = ConvertFrom-Json '{"id":"open","affected":[{"ranges":[{"type":"SEMVER","events":[{"introduced":"8.23.0"}]}]}]}'
    Assert-Affected $open '8.23.0' $true
    Assert-Affected $open '8.22.0' $false
    $explicit = ConvertFrom-Json '{"id":"explicit","affected":[{"versions":["8.23.0"]}]}'
    Assert-Affected $explicit '8.23.0' $true
    Assert-Affected $explicit '8.24.0' $false
    $invalid = ConvertFrom-Json '{"id":"invalid","affected":[{"ranges":[{"type":"GIT","events":[{"introduced":"abcd"}]}]}]}'
    $rejected = $false
    try { [void](Test-EntryAffected $invalid '8.23.0') } catch { $rejected = $true }
    if (-not $rejected) { throw 'Git-only record must not be accepted as release evidence' }
    Console-WriteTestResult
}
function Console-WriteTestResult { Write-Host 'CURL_RANGE_TEST_PASS' }

Invoke-RangeTests
if ($SelfTest) { exit 0 }

function Read-AdvisoryFeed([string]$url, [bool]$allowEmpty) {
    $raw = (Invoke-WebRequest -Uri $url -UseBasicParsing -TimeoutSec 30).Content
    if ($raw -is [byte[]]) { $raw = [Text.Encoding]::UTF8.GetString($raw) }
    $trimmed = ([string]$raw).Trim()
    if (-not $trimmed.StartsWith('[') -or -not $trimmed.EndsWith(']')) { throw "Expected JSON array: $url" }
    $items = @(ConvertFrom-Json $trimmed | Where-Object { $null -ne $_ })
    if (-not $allowEmpty -and $items.Count -eq 0) { throw "Empty advisory database: $url" }
    $seen = @{}
    foreach ($item in $items) {
        if ([string]$item.id -notmatch '^CURL-CVE-\d{4}-\d+$' -or $seen.ContainsKey([string]$item.id)) {
            throw "Invalid or duplicate advisory ID: $url"
        }
        $seen[[string]$item.id] = $true
    }
    return $items
}

[void](Compare-Release $Version $Version)
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
$vulns = @(Read-AdvisoryFeed $VulnUrl $false)
$affected = @($vulns | Where-Object { Test-EntryAffected $_ $Version })
$official = @(Read-AdvisoryFeed "https://curl.se/docs/vuln-$Version.json" $true)
$computedIds = @($affected | ForEach-Object { $_.id } | Sort-Object)
$officialIds = @($official | ForEach-Object { $_.id } | Sort-Object)
if (($computedIds -join ',') -cne ($officialIds -join ',')) {
    throw "Advisory mismatch: computed=$($computedIds -join ',') official=$($officialIds -join ',')"
}
Write-Host "curl $Version release advisory count: $($affected.Count)"
Write-Host "CURL_FEED_CROSSCHECK_PASS"
foreach ($v in $affected) {
    Write-Host ("  - {0} [{1}] {2}" -f $v.id, $v.database_specific.severity, $v.summary)
}
if ($affected.Count -gt 0) { Write-Host 'Findings require release applicability review.' }
else { Write-Host 'No published advisories for this release in both feeds at scan time.' }
exit 0
