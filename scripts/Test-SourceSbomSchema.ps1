# Validate against the official CycloneDX 1.5 Draft-07 schema and its two
# external schemas. Downloads are accepted only if their Git blob IDs match
# the independently retrieved baseline; all refs are localized before validation.
param([string]$SbomPath = 'build-core/sbom.cdx.json')
$ErrorActionPreference = 'Stop'
$pins = [ordered]@{
    'bom-1.5.schema.json' = 'c149728127117c39317b795032ed6e9a92e2f950'
    'spdx.schema.json' = 'f04d013c74c48de35726e93e367f0f738f51135d'
    'jsf-0.82.schema.json' = 'f46bfb1e52731ad1280123ff3e2bd29bd18d4bc2'
}
function Get-GitBlobId([byte[]]$Bytes) {
    $prefix = [Text.Encoding]::ASCII.GetBytes(('blob ' + $Bytes.Length.ToString([Globalization.CultureInfo]::InvariantCulture) + [char]0))
    $sha1 = [Security.Cryptography.SHA1]::Create()
    try {
        [void]$sha1.TransformBlock($prefix, 0, $prefix.Length, $prefix, 0)
        [void]$sha1.TransformFinalBlock($Bytes, 0, $Bytes.Length)
        return [BitConverter]::ToString($sha1.Hash).Replace('-','').ToLowerInvariant()
    } finally { $sha1.Dispose() }
}
function Assert-GitBlobId([byte[]]$Bytes, [string]$Expected) {
    $actual = Get-GitBlobId $Bytes
    if (-not [string]::Equals($actual, $Expected, [StringComparison]::Ordinal)) {
        throw "Schema digest mismatch: expected=$Expected observed=$actual"
    }
}
# Git's empty blob and a known nonempty blob exercise the header and bytes.
Assert-GitBlobId ([byte[]]@()) 'e69de29bb2d1d6434b8b29ae775ad8c2e48c5391'
Assert-GitBlobId ([Text.Encoding]::ASCII.GetBytes(('hello' + [char]10))) 'ce013625030ba8dba906f756967f9e9ca394464a'
$rejected = $false
try { Assert-GitBlobId ([byte[]]@(1)) 'e69de29bb2d1d6434b8b29ae775ad8c2e48c5391' } catch { $rejected = $true }
if (-not $rejected) { throw 'Git blob hash negative control failed' }
Write-Host 'SCHEMA_BLOB_HASH_SELFTEST_PASS'
$temp = Join-Path ([IO.Path]::GetTempPath()) ('ChezhiSchema-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $temp)
try {
    $schemas = @{}
    foreach ($name in $pins.Keys) {
        $file = Join-Path $temp $name
        # Fetch the exact reviewed Git object, not the mutable 1.5 branch
        # or tag. Decode its bytes without newline/encoding conversion.
        $expected = $pins[$name]
        $blob = Invoke-RestMethod -Uri "https://api.github.com/repos/CycloneDX/specification/git/blobs/$expected" -Headers @{Accept='application/vnd.github+json'} -TimeoutSec 60
        if ($blob.sha -cne $expected -or $blob.encoding -cne 'base64' -or -not $blob.content) { throw "Unexpected schema blob response: $name" }
        $bytes = [Convert]::FromBase64String([string]$blob.content)
        if ($bytes.Length -ne $blob.size) { throw "Schema blob size mismatch: $name" }
        [IO.File]::WriteAllBytes($file, $bytes)
        Assert-GitBlobId ([IO.File]::ReadAllBytes($file)) $expected
        Write-Host "SCHEMA_BLOB_VERIFIED $name $expected"
        $schemas[$name] = Get-Content -LiteralPath $file -Raw | ConvertFrom-Json -AsHashtable
    }
    function Rewrite-References($node, [bool]$jsf) {
        if ($node -is [System.Collections.IDictionary]) {
            if ($node.Contains('$ref')) {
                $value = [string]$node['$ref']
                if ($jsf -and $value.StartsWith('#/')) {
                    $node['$ref'] = '#/definitions/chezhiJsf/' + $value.Substring(2)
                } elseif ($value -ceq 'spdx.schema.json') {
                    $node['$ref'] = '#/definitions/chezhiSpdx'
                } elseif ($value -ceq 'jsf-0.82.schema.json#/definitions/signature') {
                    $node['$ref'] = '#/definitions/chezhiJsf/definitions/signature'
                } elseif (-not $value.StartsWith('#/')) {
                    throw "Unreviewed external schema reference: $value"
                }
            }
            foreach ($key in @($node.Keys)) { Rewrite-References $node[$key] $jsf }
        } elseif ($node -is [System.Collections.IList]) {
            foreach ($item in $node) { Rewrite-References $item $jsf }
        }
    }
    $schema = $schemas['bom-1.5.schema.json']
    $spdx = $schemas['spdx.schema.json']
    $jsf = $schemas['jsf-0.82.schema.json']
    foreach ($child in @($spdx,$jsf)) {
        [void]$child.Remove('$id')
        [void]$child.Remove('$schema')
    }
    Rewrite-References $schema $false
    Rewrite-References $jsf $true
    Rewrite-References $spdx $false
    if ($schema.definitions.Contains('chezhiSpdx') -or $schema.definitions.Contains('chezhiJsf')) { throw 'Schema definition collision' }
    $schema.definitions['chezhiSpdx'] = $spdx
    $schema.definitions['chezhiJsf'] = $jsf
    $schemaText = ConvertTo-Json -InputObject $schema -Depth 100 -Compress
    $jsonText = Get-Content -LiteralPath $SbomPath -Raw
    if (-not (Test-Json -Json $jsonText -Schema $schemaText -ErrorAction Stop)) { throw 'SBOM schema validation failed' }
    # Negative control: the same generated document with a disallowed format.
    $invalid = $jsonText | ConvertFrom-Json -AsHashtable
    $invalid.bomFormat = 'invalid-format'
    $invalidText = ConvertTo-Json -InputObject $invalid -Depth 100 -Compress
    if (Test-Json -Json $invalidText -Schema $schemaText -ErrorAction SilentlyContinue) { throw 'Schema accepted invalid format' }
    Write-Host 'CYCLONEDX_SCHEMA_VERIFY_PASS'
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
