# Verify installed packages before compiling. Version drift is a hard gate;
# this is not a package-archive digest lock or a reproducible installer.
param([Parameter(Mandatory=$true)][string]$MsysRoot)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'build-dependencies.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1) { throw 'Unsupported dependency lock schema' }
$bash = Join-Path $MsysRoot 'usr\bin\bash.exe'
if (-not (Test-Path -LiteralPath $bash -PathType Leaf)) { throw 'MSYS2 bash not found' }
$lines = @(& $bash -lc 'pacman -Q')
if ($LASTEXITCODE -ne 0) { throw 'pacman inventory failed' }
$installed = @{}
foreach ($line in $lines) {
    if ($line -match '^(\S+)\s+(\S+)$') {
        if ($installed.ContainsKey($Matches[1])) { throw 'Duplicate package inventory entry' }
        $installed[$Matches[1]] = $Matches[2]
    }
}
foreach ($pkg in $lock.msys2Packages.PSObject.Properties) {
    if (-not $installed.ContainsKey($pkg.Name) -or $installed[$pkg.Name] -cne $pkg.Value) {
        throw "Toolchain drift: $($pkg.Name), expected $($pkg.Value), observed $($installed[$pkg.Name])"
    }
}
$cmakeVersion = (& cmake --version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0 -or $cmakeVersion -cne "cmake version $($lock.cmake)") { throw "CMake drift: $cmakeVersion" }
Push-Location $root
try {
    foreach ($f in $lock.vendoredCJSON.files.PSObject.Properties) {
        $path = "认证核心源码/esurfingclient/app/third_party/cJSON/$($f.Name)"
        $actual = (& git hash-object -- $path | Select-Object -First 1)
        if ($LASTEXITCODE -ne 0 -or $actual -cne $f.Value.gitBlob) { throw "Vendored cJSON source drift: $path" }
    }
} finally { Pop-Location }
# Record observed package archive hashes, without inventing hashes when the
# setup action or runner has removed its package cache.
$evidence = [ordered]@{
    schemaVersion = 1
    scope = 'observed build toolchain; repository-declared digests and available cache bytes are evidence, not an independently verified archive lock'
    cmake = $cmakeVersion
    packages = @()
    tools = @()
}
# Read the package manager's existing sync database, without refreshing or
# installing anything. %h is the repository's declared archive SHA-256.
$names = @($lock.msys2Packages.PSObject.Properties.Name)
foreach ($name in $names) {
    if ($name -cnotmatch '^mingw-w64-ucrt-x86_64-[a-z0-9+_-]+$') { throw "Unexpected locked package name: $name" }
}
$printCommand = "pacman -Sddp --noconfirm --print-format '%n|%v|%r|%f|%h' " + ($names -join ' ')
$repoLines = @(& $bash -lc $printCommand)
if ($LASTEXITCODE -ne 0) { throw 'Package repository metadata query failed' }
$repo = @{}
foreach ($line in $repoLines) {
    $parts = ([string]$line).Split('|')
    if ($parts.Length -ne 5) { continue }
    $name,$version,$repository,$filename,$sha = $parts
    if ($names -cnotcontains $name -or $repo.ContainsKey($name)) { throw "Unexpected package metadata: $name" }
    if ($repository -cne 'ucrt64' -or $sha -cnotmatch '^[0-9a-fA-F]{64}$' -or
        $filename -cnotmatch '^[a-zA-Z0-9+_.-]+\.pkg\.tar\.(zst|xz|gz)$' -or
        -not $filename.StartsWith($name + '-' + $version + '-', [StringComparison]::Ordinal)) {
        throw "Invalid repository archive metadata: $name"
    }
    if ($version -cne $installed[$name]) { throw "Repository package version differs from installed lock: $name" }
    $repo[$name] = [ordered]@{repository=$repository;file=$filename;sha256=$sha.ToUpperInvariant()}
}
if ($repo.Count -ne $names.Count) { throw 'Incomplete repository archive digest inventory' }
Write-Host "BUILD_REPOSITORY_DIGESTS_PASS packages=$($repo.Count)"
$cache = Join-Path $MsysRoot 'var/cache/pacman/pkg'
foreach ($pkg in $lock.msys2Packages.PSObject.Properties) {
    $stem = $pkg.Name + '-' + $pkg.Value + '-'
    $archives = @()
    if (Test-Path -LiteralPath $cache -PathType Container) {
        $archives = @(Get-ChildItem -LiteralPath $cache -File | Where-Object {
            $_.Name.StartsWith($stem,[StringComparison]::Ordinal) -and $_.Name -match '\.pkg\.tar\.(zst|xz|gz)$'
        } | ForEach-Object {
            [ordered]@{file=$_.Name;sha256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash;bytes=$_.Length}
        })
    }
    foreach ($archive in $archives) {
        if ($archive.file -cne $repo[$pkg.Name].file -or $archive.sha256 -cne $repo[$pkg.Name].sha256) { throw "Cached archive differs from repository metadata: $($pkg.Name)" }
    }
    $evidence.packages += [ordered]@{name=$pkg.Name;version=$installed[$pkg.Name];repositoryMetadata=$repo[$pkg.Name];archives=$archives}
}
foreach ($tool in @('gcc.exe','g++.exe','ninja.exe','objdump.exe','ld.exe','strip.exe')) {
    $file = Join-Path $MsysRoot ('ucrt64/bin/' + $tool)
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw "Missing build tool: $tool" }
    $evidence.tools += [ordered]@{name=$tool;sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash}
}
foreach ($file in @((Get-Command cmake.exe).Source,(Join-Path $env:WINDIR 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'))) {
    if (-not (Test-Path -LiteralPath $file -PathType Leaf)) { throw 'Missing build tool executable' }
    $evidence.tools += [ordered]@{name=[IO.Path]::GetFileName($file);sha256=(Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash}
}
if (-not $env:RUNNER_TEMP) { throw 'Missing CI evidence directory' }
$evidence | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $env:RUNNER_TEMP 'build-toolchain-evidence.json') -Encoding utf8
$observed = @($evidence.packages | Where-Object { @($_.archives).Count -gt 0 }).Count
Write-Host "BUILD_TOOLCHAIN_EVIDENCE packagesWithArchives=$observed tools=$($evidence.tools.Count)"
Write-Host "BUILD_DEPENDENCY_LOCK_PASS packages=$($lock.msys2Packages.PSObject.Properties.Name.Count)"
