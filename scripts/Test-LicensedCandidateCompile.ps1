# Compile-only source check, with deliberately unusable trust configuration.
# No resources embedded, no executable launched or uploaded.
$ErrorActionPreference='Stop'
$root=(Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$temp=Join-Path ([IO.Path]::GetTempPath()) ('chezhi-license-compile-'+[Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $temp)
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
    $sources=@('Program.cs','CredentialStore.cs','ConnectionStates.cs','ServiceIdentity.cs','ServiceJournal.cs','licensing/AuthorizationState.cs','licensing/WindowsDeviceKey.cs','licensing/DeviceEnrollment.cs','licensing/NativeLicenseClient.cs','licensing/LicenseCoordinator.cs','licensing/DesktopLicenseGate.cs') | ForEach-Object { (Resolve-Path -LiteralPath (Join-Path $root $_)).Path }
    $exe=Join-Path $temp 'compile-only.exe'
    & $csc /nologo /target:winexe /platform:x64 /define:CHEZHI_LICENSED "/out:$exe" $refs $sources $trust
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $exe)) { throw 'Licensed candidate source compile failed' }
    Write-Host 'PASS licensed candidate x64 compile-only (no runtime or production trust)'
} finally { Remove-Item -LiteralPath $temp -Recurse -Force }
