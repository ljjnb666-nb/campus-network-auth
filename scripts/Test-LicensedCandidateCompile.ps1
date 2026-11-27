# Compile-only source check, with deliberately unusable trust configuration.
# No resources embedded or uploaded. Negative-only CLI and UI probes execute.
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$temp=Join-Path ([IO.Path]::GetTempPath()) ('chezhi-license-compile-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $temp)
# Parallel negative process probes only. Never send exact restore or an
# empty argument vector to the production-shaped executable.
function Test-DeniedProcessBatch {
    param([string]$Binary,[object[]]$Cases)
    $children=[System.Collections.Generic.List[object]]::new()
    try {
        foreach ($case in $Cases) {
            $opts=@{FilePath=$Binary;PassThru=$true}
            if ($case.arg.Length -gt 0) { $opts.ArgumentList=$case.arg }
            $child=Start-Process @opts
            $children.Add(@{process=$child;expected=$case.code;label=$case.label})
        }
        foreach ($entry in $children) {
            if (-not $entry.process.WaitForExit(10000)) { throw "Denial probe timed out: $($entry.label)" }
            if ($entry.process.ExitCode -ne $entry.expected) { throw "Unexpected denial result: $($entry.label), code $($entry.process.ExitCode)" }
        }
    } finally {
        foreach ($entry in $children) {
            try {
                if (-not $entry.process.HasExited) {
                    $entry.process.Kill()
                    if (-not $entry.process.WaitForExit(5000)) { throw 'Probe termination not confirmed' }
                }
            } finally { $entry.process.Dispose() }
        }
    }
}
try {
    $trust=Join-Path $temp 'CandidateTrust.cs'
    @'
internal static class CandidateTrust {
    internal static readonly string Origin="https://compile-only.invalid";
    internal static readonly byte[] DllHash=new byte[32], Scope=new byte[32], SigningKey=new byte[32], CA=null;
}
'@ | Set-Content -LiteralPath $trust -Encoding UTF8
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.ServiceProcess.dll','/r:System.Management.dll','/r:System.Security.dll')
    $sources=@('Program.cs','CredentialStore.cs','ConnectionStates.cs','ServiceIdentity.cs','ServiceJournal.cs','licensing/AuthorizationState.cs','licensing/WindowsDeviceKey.cs','licensing/DeviceEnrollment.cs','licensing/NativeLicenseClient.cs','licensing/LicenseCoordinator.cs','licensing/DesktopLicenseGate.cs','licensing/LicenseCardStore.cs','licensing/LicenseRenewalSchedule.cs','licensing/LicenseCardInput.cs') | ForEach-Object { (Resolve-Path -LiteralPath (Join-Path $root $_)).Path }
    $exe=Join-Path $temp 'compile-only.exe'
    & $csc /nologo /target:winexe /platform:x64 /define:CHEZHI_LICENSED "/out:$exe" $refs $sources $trust
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe)) { throw 'Licensed candidate source compile failed' }
    # These exact paths must return before any service/network/core access.
    # Do not invoke restore or general startup in the resource-less fixture.
    foreach ($test in @(@{arg='--admin-prepare';code=28},@{arg='--not-a-supported-command';code=2},@{arg='--self-test-license-boundary';code=2},@{arg='--self-test-core';code=2})) {
        $p=Start-Process -FilePath $exe -ArgumentList $test.arg -PassThru
        try {
            if (-not $p.WaitForExit(5000)) { $p.Kill(); throw 'Candidate CLI probe timed out' }
            if ($p.ExitCode -ne $test.code) { throw "Unexpected candidate CLI exit: $($p.ExitCode)" }
        } finally { $p.Dispose() }
    }
    $negativeCases=@(
        @{arg='--admin-prepare';code=28;label='exact prepare'},
        @{arg='--admin-prepare';code=28;label='duplicate prepare'},
        @{arg='--admin-prepare unexpected';code=2;label='extra argument'},
        @{arg='--ADMIN-PREPARE';code=2;label='case variant'},
        @{arg='--admin-restore unexpected';code=2;label='malformed restore'},
        @{arg='--self-test-service-gate invalid';code=2;label='legacy gate command'},
        @{arg='--self-test-stub';code=2;label='legacy stub'}
    )
    Test-DeniedProcessBatch -Binary $exe -Cases $negativeCases
    Write-Host 'PASS licensed candidate x64 compile, privileged prepare and self-test denial; concurrent negative CLI probes'
    $fixture=Join-Path $temp 'negative-ui-fixture.exe'
    & $csc /nologo /target:winexe /platform:x64 /define:CHEZHI_LICENSED,CHEZHI_LICENSED_TEST "/out:$fixture" $refs $sources $trust
    if ($LASTEXITCODE -ne 0) { throw 'Negative-only licensed UI fixture compile failed' }
    Test-DeniedProcessBatch -Binary $fixture -Cases ($negativeCases+@(
        @{arg='--admin-restore';code=2;label='fixture restore forbidden'},
        @{arg='';code=2;label='fixture production startup forbidden'},
        @{arg='--self-test-ui';code=2;label='fixture legacy UI forbidden'},
        @{arg='--self-test-license-boundary unexpected';code=2;label='malformed fixture entry'}
    ))
    Write-Host 'PASS isolated negative fixture entrypoint allowlist and concurrent denial'
    $p=Start-Process -FilePath $fixture -ArgumentList '--self-test-license-boundary' -PassThru
    try {
        if (-not $p.WaitForExit(30000)) { $p.Kill(); throw 'Licensed UI negative fixture timed out' }
        if ($p.ExitCode -ne 0) { throw "Licensed UI negative fixture failed: $($p.ExitCode)" }
    } finally { $p.Dispose() }
    Write-Host 'PASS licensed negative-only UI/core admission, cancellation, restore availability, tray and disposal (no service/network changes)'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
