# Non-release integration candidate. Requires independently trusted inputs.
# Does not alter the legacy build or the stable desktop executable.
[CmdletBinding()]
param(
    [Parameter(Mandatory=$true)][string]$CorePath,
    [Parameter(Mandatory=$true)][string]$SdkPath,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$SdkSha256,
    [Parameter(Mandatory=$true)][string]$Origin,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$Deployment,
    [Parameter(Mandatory=$true)][ValidatePattern('^[0-9a-fA-F]{64}$')][string]$SigningKey,
    [string]$CaDerPath = ''
)
$ErrorActionPreference='Stop'
$uri=$null
if (-not [Uri]::TryCreate($Origin,[UriKind]::Absolute,[ref]$uri) -or $uri.Scheme -cne 'https' -or
    $uri.UserInfo -or $uri.Query -or $uri.Fragment -or $uri.AbsolutePath -ne '/' -or
    [Text.Encoding]::UTF8.GetByteCount($Origin) -gt 2048 -or $Origin -match '[%#\r\n]') { throw 'Invalid HTTPS origin' }
if ($Deployment -match '^0{64}$' -or $SigningKey -match '^0{64}$') { throw 'Invalid trust configuration' }
$core=(Resolve-Path -LiteralPath $CorePath).Path
$sdk=(Resolve-Path -LiteralPath $SdkPath).Path
$source=Join-Path $PSScriptRoot 'Program.cs'
$raw=Get-Content -LiteralPath $source -Raw -Encoding UTF8
$m=[regex]::Match($raw,'private const string CoreHash = "([0-9a-fA-F]{64})";')
if (-not $m.Success -or (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash -ne $m.Groups[1].Value) { throw 'Core hash mismatch: use the exact reviewed core or reconcile the generated source first' }
if ((Get-FileHash -LiteralPath $sdk -Algorithm SHA256).Hash -ne $SdkSha256) { throw 'SDK hash mismatch' }
$ca='null'
if ($CaDerPath) {
    $caBytes=[IO.File]::ReadAllBytes((Resolve-Path -LiteralPath $CaDerPath).Path)
    if ($caBytes.Length -lt 1 -or $caBytes.Length -gt 16384) { throw 'Invalid CA DER length' }
    $ca='Convert.FromBase64String("'+[Convert]::ToBase64String($caBytes)+'")'
}
# Embed public trust configuration; no external configuration override.
function HexBase64([string]$hex) {
    $bytes=New-Object byte[] ($hex.Length/2)
    for ($i=0;$i -lt $bytes.Length;$i++) { $bytes[$i]=[Convert]::ToByte($hex.Substring($i*2,2),16) }
    return [Convert]::ToBase64String($bytes)
}
$out=Join-Path $PSScriptRoot ('build-licensed-candidate-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $out)
try {
    $trust=Join-Path $out 'CandidateTrust.cs'
    $origin64=[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Origin))
    @"
using System;
using System.Text;
internal static class CandidateTrust {
    internal static readonly string Origin=Encoding.UTF8.GetString(Convert.FromBase64String("$origin64"));
    internal static readonly byte[] DllHash=Convert.FromBase64String("$(HexBase64 $SdkSha256)");
    internal static readonly byte[] Scope=Convert.FromBase64String("$(HexBase64 $Deployment)");
    internal static readonly byte[] SigningKey=Convert.FromBase64String("$(HexBase64 $SigningKey)");
    internal static readonly byte[] CA=$ca;
}
"@ | Set-Content -LiteralPath $trust -Encoding UTF8
    $dll=Join-Path $out 'chezhi_license_sdk.dll'
    Copy-Item -LiteralPath $sdk -Destination $dll
    if ((Get-FileHash -LiteralPath $dll -Algorithm SHA256).Hash -ne $SdkSha256) { throw 'Copied SDK hash mismatch' }
    $coreCopy=Join-Path $out 'core-resource.bin'
    Copy-Item -LiteralPath $core -Destination $coreCopy
    if ((Get-FileHash -LiteralPath $coreCopy -Algorithm SHA256).Hash -ne $m.Groups[1].Value) { throw 'Copied core hash mismatch' }
    $exe=Join-Path $out 'CheZhi-LicensedCandidate.exe'
    $csc=Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
    $refs=@('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.ServiceProcess.dll','/r:System.Management.dll','/r:System.Security.dll')
    $sources=@('Program.cs','CredentialStore.cs','ConnectionStates.cs','ServiceIdentity.cs','ServiceJournal.cs','licensing/AuthorizationState.cs','licensing/WindowsDeviceKey.cs','licensing/DeviceEnrollment.cs','licensing/NativeLicenseClient.cs','licensing/LicenseCoordinator.cs','licensing/DesktopLicenseGate.cs') | ForEach-Object { (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot $_)).Path }
    $arguments=@('/nologo','/target:winexe','/platform:x64','/optimize+','/define:CHEZHI_LICENSED',"/out:$exe",('/win32icon:'+(Join-Path $PSScriptRoot 'chezhi.ico')),('/win32manifest:'+(Join-Path $PSScriptRoot 'app.manifest')),("/resource:$coreCopy"+',Chezhi.AuthCore'),('/resource:'+(Join-Path $PSScriptRoot 'Licenses.txt')+',Chezhi.Licenses'))
    & $csc $arguments $refs $sources $trust
    if ($LASTEXITCODE -ne 0) { throw 'Licensed candidate compile failed' }
    Remove-Item -LiteralPath $trust,$coreCopy -Force
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination ($exe+'.config')
    $manifest=[ordered]@{status='NON_RELEASE_CANDIDATE';sdkSource='57369b9097534530db1b018e2ff9adbaa24c6578';sdkSha256=$SdkSha256;coreSha256=$m.Groups[1].Value;origin=$Origin;deployment=$Deployment;signingKey=$SigningKey;exeSha256=(Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash}
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $out 'candidate-manifest.json') -Encoding UTF8
    Write-Host "NON-RELEASE candidate: $out"
} catch {
    Remove-Item -LiteralPath $out -Recurse -Force
    throw
}
