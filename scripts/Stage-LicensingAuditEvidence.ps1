# Stage only non-executable integration evidence. Never package a client.
param(
    [Parameter(Mandatory=$true)][string]$Source,
    [Parameter(Mandatory=$true)][string]$Destination
)
$ErrorActionPreference='Stop'
$names=@('LICENSING_CANDIDATE_NOTICE.txt','build-manifest.json','sbom.cdx.json','pe-imports.json','repeatability.json','build-toolchain-evidence.json','build-evidence.json','gui-compiler-candidate.json')
$notice='NON_RELEASE: legacy offline regression evidence only; no licensed binary or release attestation'
function Assert-Evidence([string]$Path,[string]$Name) {
    $item=Get-Item -LiteralPath $Path -Force
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $item.Length -le 0 -or $item.Length -gt 16777216) {
        throw "Invalid evidence file: $Name"
    }
    $raw=[IO.File]::ReadAllBytes($item.FullName)
    $utf8=[Text.UTF8Encoding]::new($false,$true)
    $body=$utf8.GetString($raw).TrimStart([char]0xFEFF)
    if ($Name -eq 'LICENSING_CANDIDATE_NOTICE.txt') {
        if ($body.Trim() -cne $notice) {throw 'Non-release notice mismatch'}
    } else {
        $parsed=ConvertFrom-Json -InputObject $body -ErrorAction Stop
        if ($null -eq $parsed -or $body.TrimStart()[0] -ne '{') {throw "Expected JSON object: $Name"}
    }
}
$src=(Resolve-Path -LiteralPath $Source).Path
if (Test-Path -LiteralPath $Destination) {throw 'Evidence destination must be new'}
$dest=[IO.Path]::GetFullPath($Destination)
if ($dest.StartsWith($src+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)) {throw 'Evidence destination must be separate'}
foreach ($name in $names) {Assert-Evidence (Join-Path $src $name) $name}
[void](New-Item -ItemType Directory -Path $dest)
try {
    foreach ($name in $names) {
        $from=Join-Path $src $name
        $to=Join-Path $dest $name
        Copy-Item -LiteralPath $from -Destination $to
        Assert-Evidence $to $name
        if ((Get-FileHash -LiteralPath $from).Hash -cne (Get-FileHash -LiteralPath $to).Hash) {throw "Evidence copy mismatch: $name"}
    }
    $actual=@(Get-ChildItem -LiteralPath $dest -Force | ForEach-Object {$_.Name})
    if ($actual.Count -ne $names.Count -or @(Compare-Object $names $actual -CaseSensitive).Count -ne 0) {throw 'Unexpected staged evidence content'}
    Write-Host 'NON_RELEASE_EVIDENCE_STAGE_PASS files=8'
} catch {
    # Destination is newly created by this invocation; remove only the files
    # from the allowlist, never recurse into unexpected content.
    foreach ($name in $names) {
        $path=Join-Path $dest $name
        if (Test-Path -LiteralPath $path -PathType Leaf) {Remove-Item -LiteralPath $path -Force}
    }
    throw
}
