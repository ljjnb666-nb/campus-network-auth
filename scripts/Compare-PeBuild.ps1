# Read-only PE comparison. Neither assembly is loaded or executed.
param(
    [Parameter(Mandatory=$true)][string]$First,
    [Parameter(Mandatory=$true)][string]$Second
)
$ErrorActionPreference = 'Stop'
function Read-PeEvidence([string]$Path) {
    $bytes = [IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $Path).Path)
    if ($bytes.Length -lt 64 -or [BitConverter]::ToUInt16($bytes,0) -ne 0x5a4d) { throw 'Invalid DOS header' }
    $pe = [int64][BitConverter]::ToUInt32($bytes,60)
    if ($pe -gt $bytes.Length - 24 -or [BitConverter]::ToUInt32($bytes,[int]$pe) -ne 0x4550) { throw 'Invalid PE signature' }
    $coff = $pe + 4
    $count = [BitConverter]::ToUInt16($bytes,[int]($coff+2))
    $timestamp = [BitConverter]::ToUInt32($bytes,[int]($coff+4))
    $optionalSize = [BitConverter]::ToUInt16($bytes,[int]($coff+16))
    $sectionTable = $coff + 20 + $optionalSize
    if ($count -eq 0 -or $sectionTable + 40L*$count -gt $bytes.Length) { throw 'Invalid PE section table' }
    $sections = @()
    $hasher = [Security.Cryptography.SHA256]::Create()
    try {
        for ($i=0; $i -lt $count; $i++) {
            $h = [int]($sectionTable+40L*$i)
            $name = [Text.Encoding]::ASCII.GetString($bytes,$h,8).TrimEnd([char]0)
            $size = [int64][BitConverter]::ToUInt32($bytes,$h+16)
            $offset = [int64][BitConverter]::ToUInt32($bytes,$h+20)
            if ($offset -gt $bytes.Length -or $size -gt $bytes.Length-$offset) { throw "Invalid PE raw section range: $name" }
            $digest = [Convert]::ToHexString($hasher.ComputeHash($bytes,[int]$offset,[int]$size))
            $sections += [ordered]@{index=$i;name=$name;offset=$offset;bytes=$size;sha256=$digest}
        }
    } finally { $hasher.Dispose() }
    return [ordered]@{coffTimestamp=$timestamp;sections=$sections}
}
$a = Read-PeEvidence $First
$b = Read-PeEvidence $Second
$diff = @()
$max = [Math]::Max($a.sections.Count,$b.sections.Count)
for ($i=0;$i -lt $max;$i++) {
    $left = if ($i -lt $a.sections.Count) { $a.sections[$i] } else { $null }
    $right = if ($i -lt $b.sections.Count) { $b.sections[$i] } else { $null }
    if ($null -eq $left -or $null -eq $right -or $left.name -cne $right.name -or $left.sha256 -cne $right.sha256 -or $left.bytes -ne $right.bytes -or $left.offset -ne $right.offset) {
        $diff += [ordered]@{index=$i;first=$left;second=$right}
    }
}
[ordered]@{firstCoffTimestamp=$a.coffTimestamp;secondCoffTimestamp=$b.coffTimestamp;timestampEqual=($a.coffTimestamp -eq $b.coffTimestamp);changedSections=$diff;scope='raw section hashes and COFF timestamp only; not proof of semantic equivalence'}
