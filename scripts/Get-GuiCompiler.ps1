param([string]$CompilerPath = '')
$ErrorActionPreference = 'Stop'

if (!$CompilerPath) {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (!(Test-Path -LiteralPath $vswhere -PathType Leaf)) { throw '没有找到 Visual Studio 定位工具。请指定已核验的 Roslyn csc.exe。' }
    $installs = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath)
    if ($LASTEXITCODE -ne 0 -or $installs.Count -ne 1) { throw '没有找到 Visual Studio Roslyn。请指定已核验的 Roslyn csc.exe。' }
    $CompilerPath = Join-Path $installs[0] 'MSBuild\Current\Bin\Roslyn\csc.exe'
}
$compiler = (Resolve-Path -LiteralPath $CompilerPath).Path
if ([IO.Path]::GetFileName($compiler) -cne 'csc.exe') { throw '编译器必须为锁定的 Roslyn csc.exe。' }
$directory = Split-Path -Parent $compiler
$lock = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'gui-compiler.lock.json') -Raw | ConvertFrom-Json
if ($lock.schemaVersion -ne 1 -or !$lock.files.'csc.exe') { throw 'Roslyn 编译器锁无效。' }
foreach ($entry in $lock.files.PSObject.Properties) {
    $actual = (Get-FileHash -LiteralPath (Join-Path $directory $entry.Name) -Algorithm SHA256).Hash
    if ($actual -cne $entry.Value) { throw "Roslyn 组件摘要不符：$($entry.Name)" }
}
$signature = Get-AuthenticodeSignature -LiteralPath $compiler
if ($signature.Status -ne 'Valid' -or $signature.SignerCertificate.Subject -notmatch '(?:^|, )O=Microsoft Corporation(?:,|$)') {
    throw 'Roslyn 编译器的 Microsoft 签名未获验证。'
}

$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$referenceEvidence = @(foreach ($name in 'mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll',
    'System.Windows.Forms.dll','System.Web.Extensions.dll','System.ServiceProcess.dll','System.Management.dll','System.Security.dll') {
    $path = Join-Path $framework $name
    if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少 .NET Framework 引用：$name" }
    [pscustomobject]@{ File = $name; Path = $path; Sha256 = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash }
})
[pscustomobject]@{
    Path = $compiler
    References = @($referenceEvidence | ForEach-Object { "/r:$($_.Path)" })
    Evidence = [ordered]@{
        version = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
        sha256 = $lock.files.'csc.exe'
        componentLock = $lock.files
        referenceFiles = @($referenceEvidence | Select-Object File,Sha256)
    }
    Options = @('/nologo','/noconfig','/nostdlib+','/target:winexe','/optimize+','/platform:anycpu','/langversion:5','/deterministic+')
}
