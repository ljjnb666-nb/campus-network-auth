# Source-level CycloneDX inventory; not binary composition or signed provenance.
param([string]$BuildDir = 'build-core')
$ErrorActionPreference = 'Stop'
$m = Get-Content -LiteralPath (Join-Path $BuildDir 'build-manifest.json') -Raw | ConvertFrom-Json
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-dependencies.lock.json') -Raw | ConvertFrom-Json
if ($m.schemaVersion -ne 2 -or $m.gitCommit -notmatch '^[0-9a-f]{40}$') { throw 'Invalid build manifest' }
function HashEntry([string]$value) {
    if ($value -notmatch '^[0-9a-fA-F]{64}$') { throw 'Invalid SHA-256' }
    return [ordered]@{ alg = 'SHA-256'; content = $value.ToLowerInvariant() }
}
$components = @(
    [ordered]@{
        type='library'; 'bom-ref'='libcurl'; name='libcurl'; version=$m.dependencies.curl.version
        hashes=@((HashEntry $m.dependencies.curl.sha256))
        externalReferences=@(@{type='distribution';url=$m.dependencies.curl.source})
        properties=@(@{name='chezhi:hashScope';value='source archive'},@{name='chezhi:linkage';value='static'})
    },
    [ordered]@{
        type='library'; 'bom-ref'='cJSON'; name='cJSON'; version=$m.dependencies.cJSON.version
        externalReferences=@(@{type='vcs';url=$lock.vendoredCJSON.upstreamRepository})
        properties=@(@{name='chezhi:source';value='vendored, locally patched'},@{name='chezhi:linkage';value='static'})
        components=@(foreach ($f in $m.dependencies.cJSON.files) {
            [ordered]@{type='file';'bom-ref'=('cJSON/'+$f.file);name=$f.file;hashes=@((HashEntry $f.sha256))}
        })
    },
    [ordered]@{
        type='application'; 'bom-ref'='auth-core'; name='ESurfingClient modified core'; version='2.3.2-r2'
        properties=@(@{name='chezhi:sourceCommit';value=$m.gitCommit})
    }
)
$properties = @(
    @{name='chezhi:inventoryScope';value='declared source dependencies; not exhaustive binary or OS inventory'},
    @{name='chezhi:sourceCommit';value=$m.gitCommit},
    @{name='chezhi:toolchainLockBaseline';value=$lock.baseline.commit}
)
foreach ($pkg in $lock.msys2Packages.PSObject.Properties) {
    $properties += @{name=('chezhi:buildPackage:'+ $pkg.Name);value=[string]$pkg.Value}
}
foreach ($tool in $m.toolchain.PSObject.Properties) {
    $properties += @{name=('chezhi:buildTool:'+ $tool.Name);value=[string]$tool.Value}
}
$bom = [ordered]@{
    '$schema'='http://cyclonedx.org/schema/bom-1.5.schema.json'
    bomFormat='CycloneDX'; specVersion='1.5'; version=1
    metadata=[ordered]@{
        timestamp=$m.builtAtUtc
        component=[ordered]@{type='application';'bom-ref'='chezhi-desktop';name='CheZhi campus network client';version=$m.appVersion}
        properties=$properties
    }
    components=$components
    dependencies=@(
        @{ref='chezhi-desktop';dependsOn=@('auth-core')},
        @{ref='auth-core';dependsOn=@('libcurl','cJSON')},
        @{ref='libcurl';dependsOn=@()},
        @{ref='cJSON';dependsOn=@()}
    )
}
$out = Join-Path $BuildDir 'sbom.cdx.json'
$bom | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath $out -Encoding utf8
$check = Get-Content -LiteralPath $out -Raw | ConvertFrom-Json
if ($check.bomFormat -cne 'CycloneDX' -or @($check.components).Count -ne 3 -or @($check.dependencies).Count -ne 4) { throw 'SBOM structure check failed' }
Write-Host 'SOURCE_SBOM_GENERATED_PASS'
