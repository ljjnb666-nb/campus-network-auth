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
Write-Host "BUILD_DEPENDENCY_LOCK_PASS packages=$($lock.msys2Packages.PSObject.Properties.Name.Count)"
