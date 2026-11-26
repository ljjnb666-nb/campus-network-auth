# Synthetic byte buffers only: never load or execute a PE image.
$ErrorActionPreference = 'Stop'
$compare = Join-Path $PSScriptRoot 'Compare-PeBuild.ps1'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('pe-audit-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $tmp)
function Set-U16([byte[]]$buf, [int]$offset, [uint16]$value) {
    [Array]::Copy([BitConverter]::GetBytes($value), 0, $buf, $offset, 2)
}
function Set-U32([byte[]]$buf, [int]$offset, [uint32]$value) {
    [Array]::Copy([BitConverter]::GetBytes($value), 0, $buf, $offset, 4)
}
function Assert-Rejected([byte[]]$buf, [string]$expected) {
    [IO.File]::WriteAllBytes($right, $buf)
    $rejected = $false
    try { $null = & $compare -First $left -Second $right }
    catch {
        if (-not $_.Exception.Message.StartsWith($expected, [StringComparison]::Ordinal)) { throw }
        $rejected = $true
    }
    if (-not $rejected) { throw "PE negative control was accepted: $expected" }
}
try {
    $left = Join-Path $tmp 'first.bin'
    $right = Join-Path $tmp 'second.bin'
    # Minimal structural fixture, not a runnable executable.
    $base = [byte[]]::new(256)
    Set-U16 $base 0 0x5a4d
    Set-U32 $base 60 64
    Set-U32 $base 64 0x4550
    Set-U16 $base 70 1
    Set-U32 $base 72 42
    [Array]::Copy([Text.Encoding]::ASCII.GetBytes('.test'), 0, $base, 88, 5)
    Set-U32 $base 104 4
    Set-U32 $base 108 128
    $base[128] = 17
    [IO.File]::WriteAllBytes($left, $base)
    [IO.File]::WriteAllBytes($right, $base)
    $result = & $compare -First $left -Second $right
    if (-not $result.timestampEqual -or @($result.changedSections).Count -ne 0) { throw 'Identical fixture comparison failed' }
    $copy = [byte[]]$base.Clone()
    Set-U32 $copy 72 43
    [IO.File]::WriteAllBytes($right, $copy)
    $result = & $compare -First $left -Second $right
    if ($result.timestampEqual -or $result.secondCoffTimestamp -ne 43 -or @($result.changedSections).Count -ne 0) { throw 'Timestamp-only regression failed' }
    $copy = [byte[]]$base.Clone()
    $copy[128] = 18
    [IO.File]::WriteAllBytes($right, $copy)
    $result = & $compare -First $left -Second $right
    if (-not $result.timestampEqual -or @($result.changedSections).Count -ne 1 -or
        $result.changedSections[0].first.sha256 -ceq $result.changedSections[0].second.sha256) { throw 'Section-content regression failed' }

    Assert-Rejected ([byte[]]::new(32)) 'Invalid DOS header'
    $copy = [byte[]]$base.Clone()
    Set-U32 $copy 60 16
    Set-U32 $copy 16 0x4550
    Assert-Rejected $copy 'Invalid PE signature'
    $copy = [byte[]]$base.Clone()
    Set-U16 $copy 70 65535
    Assert-Rejected $copy 'Invalid PE section table'
    $copy = [byte[]]$base.Clone()
    Set-U32 $copy 108 88
    Assert-Rejected $copy 'Invalid PE raw section range'
    $copy = [byte[]]$base.Clone()
    Set-U32 $copy 108 255
    Assert-Rejected $copy 'Invalid PE raw section range'
    Write-Host 'PE_EVIDENCE_SELFTEST_PASS cases=8'
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force
}
