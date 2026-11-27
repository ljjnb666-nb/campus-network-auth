# Hosted CI only. Rebuild pinned public SDK, run a real modal authorization
# window against its temporary TLS fixture, never touch campus service/core.
param([Parameter(Mandatory=$true)][string]$SdkSource)
$ErrorActionPreference='Stop'
if ($env:GITHUB_ACTIONS -ne 'true' -or $env:RUNNER_ENVIRONMENT -ne 'github-hosted') { throw 'Hosted CI fixture only' }
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$sdk=(Resolve-Path -LiteralPath $SdkSource).Path
$pin='57369b9097534530db1b018e2ff9adbaa24c6578'
if ((git -C $sdk rev-parse HEAD).Trim() -cne $pin) { throw 'SDK checkout mismatch' }
foreach ($name in @('AuthorizationState.cs','WindowsDeviceKey.cs','DeviceEnrollment.cs','NativeLicenseClient.cs','LicenseCoordinator.cs')) {
    if ((Get-FileHash -LiteralPath (Join-Path $root ('licensing/'+$name)) -Algorithm SHA256).Hash -cne
        (Get-FileHash -LiteralPath (Join-Path $sdk ('sdk/windows/'+$name)) -Algorithm SHA256).Hash) { throw "Vendored SDK source drift: $name" }
}
$temp=Join-Path $env:RUNNER_TEMP ('desktop-online-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $temp)
try {
    Push-Location -LiteralPath $sdk
    try {
        $gcc=Get-Command gcc.exe -ErrorAction SilentlyContinue
        if (-not $gcc) {
            $candidate='C:\mingw64\bin\gcc.exe'
            if (-not (Test-Path -LiteralPath $candidate -PathType Leaf)) {throw 'x64 MinGW unavailable'}
            $env:PATH=(Split-Path -Parent $candidate)+';'+$env:PATH
            $gcc=Get-Command gcc.exe
        }
        $machine=& $gcc.Source -dumpmachine
        if ($LASTEXITCODE -ne 0 -or $machine -notmatch '^x86_64.*mingw') {throw 'Wrong GCC architecture'}
        $env:GOTOOLCHAIN='local'
        $env:CGO_ENABLED='1';$env:GOOS='windows';$env:GOARCH='amd64';$env:CC=$gcc.Source
        go build -trimpath -buildmode=c-shared -o (Join-Path $temp 'chezhi_license_sdk.dll') ./cmd/windows-license-sdk
        if ($LASTEXITCODE -ne 0) {throw 'SDK build failed'}
        $env:CGO_ENABLED='0'
        go build -tags sdkfixture -trimpath -o (Join-Path $temp 'SdkTestServer.exe') ./cmd/sdk-test-server
        if ($LASTEXITCODE -ne 0) {throw 'Fixture build failed'}
    } finally {Pop-Location}
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $sources=@('AuthorizationState.cs','WindowsDeviceKey.cs','DeviceEnrollment.cs','NativeLicenseClient.cs','LicenseCoordinator.cs','DesktopLicenseGate.cs','LicenseCardStore.cs','LicenseRenewalSchedule.cs','LicenseCardInput.cs','DesktopLicenseOnlineSmoke.cs') | ForEach-Object {(Resolve-Path -LiteralPath (Join-Path $root ('licensing/'+$_))).Path}
    $sources+=@('Program.cs','CredentialStore.cs','ConnectionStates.cs','ServiceIdentity.cs','ServiceJournal.cs') | ForEach-Object {(Resolve-Path -LiteralPath (Join-Path $root $_)).Path}
    $exe=Join-Path $temp 'OnlineSmoke.exe'
    & $csc /nologo /target:exe /platform:x64 /main:DesktopLicenseOnlineSmoke /define:CHEZHI_LICENSED,CHEZHI_LICENSE_UI_TEST,CHEZHI_SDK_TEST,CHEZHI_CARD_TEST /r:System.dll /r:System.Core.dll /r:System.Security.dll /r:System.Drawing.dll /r:System.Windows.Forms.dll /r:System.Web.Extensions.dll /r:System.ServiceProcess.dll /r:System.Management.dll "/out:$exe" $sources
    if ($LASTEXITCODE -ne 0) {throw 'Desktop online fixture compile failed'}
    & (Join-Path $sdk 'sdk/windows/Run-OnlineSmoke.ps1') -Bin $temp
} finally {Remove-Item -LiteralPath $temp -Recurse -Force}
