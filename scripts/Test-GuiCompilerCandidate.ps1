# Isolated diagnostic only. No downloads, no execution of candidate GUI, no
# replacement of the default compiler or output. Observations are not locks.
param([string]$BuildDirectory = 'build-core')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$reportPath = Join-Path $build 'gui-compiler-candidate.json'
if (Test-Path -LiteralPath $reportPath) { throw 'Candidate report already exists' }
$report = [ordered]@{
    schemaVersion = 1
    scope = 'isolated same-runner GUI compile experiment; no runtime or independent provenance verification'
    checkout = (git -C $root rev-parse HEAD).Trim()
    status = 'not_started'
    compiler = $null
    referenceFiles = @()
    coreSha256 = $null
    outputs = @()
    byteIdentical = $false
}
function Write-Report {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "GUI_COMPILER_CANDIDATE status=$($report.status) byteIdentical=$($report.byteIdentical)"
}
$vswhere = Join-Path __PF86__ 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) {
    $report.status = 'vswhere_unavailable'; Write-Report; return
}
$vs = @(& $vswhere -latest -products '*' -requires Microsoft.Component.MSBuild -property installationPath)
if ($LASTEXITCODE -ne 0 -or $vs.Count -ne 1 -or -not $vs[0]) {
    $report.status = 'vs_installation_unavailable'; Write-Report; return
}
$compiler = Join-Path $vs[0] 'MSBuild\Current\Bin\Roslyn\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) {
    $report.status = 'compiler_unavailable'; Write-Report; return
}
$signature = Get-AuthenticodeSignature -LiteralPath $compiler
$report.compiler = [ordered]@{
    version = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    sha256 = (Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash
    signatureStatus = [string]$signature.Status
    signer = if ($signature.SignerCertificate) { $signature.SignerCertificate.Subject } else { $null }
    observedComponents = @(foreach ($name in @('csc.exe','Microsoft.CodeAnalysis.dll','Microsoft.CodeAnalysis.CSharp.dll')) {
        $component = Join-Path (Split-Path -Parent $compiler) $name
        if (Test-Path -LiteralPath $component -PathType Leaf) {
            [ordered]@{ file=$name; sha256=(Get-FileHash -LiteralPath $component -Algorithm SHA256).Hash }
        }
    })
}
if ($signature.Status -ne 'Valid' -or $report.compiler.signer -notmatch 'Microsoft Corporation') {
    $report.status = 'compiler_signature_not_verified'; Write-Report; return
}
# Use explicit references from the same installed Framework runtime family
# as the existing compiler; this is not a new target framework declaration.
$framework = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319'
$names = @('mscorlib.dll','System.dll','System.Core.dll','System.Drawing.dll',
    'System.Windows.Forms.dll','System.Web.Extensions.dll','System.ServiceProcess.dll',
    'System.Management.dll','System.Security.dll')
$refs = @(foreach ($name in $names) {
    $path = Join-Path $framework $name
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Missing Framework reference: $name" }
    $report.referenceFiles += [ordered]@{file=$name;sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash}
    "/r:$path"
})
$core = Join-Path $build '认证核心-源码重建.exe'
$manifest = Get-Content -LiteralPath (Join-Path $build 'build-manifest.json') -Raw | ConvertFrom-Json
$report.coreSha256 = (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash
$coreEntry = @($manifest.outputs | Where-Object { $_.file -ceq '认证核心-源码重建.exe' })
if ($coreEntry.Count -ne 1 -or $coreEntry[0].sha256 -cne $report.coreSha256) { throw 'Candidate baseline core digest mismatch' }
$program = Get-Content -LiteralPath (Join-Path $root 'Program.cs') -Raw -Encoding UTF8
$pattern = 'private const string CoreHash = "[0-9A-Fa-f]{64}";'
if ([regex]::Matches($program, $pattern).Count -ne 1) { throw 'Candidate core hash anchor mismatch' }
$program = [regex]::Replace($program, $pattern, ('private const string CoreHash = "' + $report.coreSha256 + '";'))
$temp = Join-Path $env:RUNNER_TEMP ('gui-compiler-candidate-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $temp)
try {
    foreach ($name in @('one','two')) {
        $dir = Join-Path $temp $name
        [void](New-Item -ItemType Directory -Path $dir)
        $generated = Join-Path $dir 'Program.generated.cs'
        [IO.File]::WriteAllText($generated, $program, [Text.UTF8Encoding]::new($true))
        $sources = @($generated)
        foreach ($sourceName in @('CredentialStore.cs','ConnectionStates.cs','ServiceIdentity.cs','ServiceJournal.cs','chezhi.ico','app.manifest','Licenses.txt')) {
            Copy-Item -LiteralPath (Join-Path $root $sourceName) -Destination (Join-Path $dir $sourceName)
            if ($sourceName.EndsWith('.cs')) { $sources += (Join-Path $dir $sourceName) }
        }
        $out = Join-Path $dir 'Candidate.exe'
        $compilerArgs = @('/nologo','/noconfig','/nostdlib+','/target:winexe','/optimize+','/platform:anycpu','/langversion:5','/deterministic+',"/pathmap:$dir=C:\rc-source",
            "/out:$out",('/win32icon:' + (Join-Path $dir 'chezhi.ico')),
            ('/win32manifest:' + (Join-Path $dir 'app.manifest')),
            "/resource:$core,Chezhi.AuthCore",('/resource:' + (Join-Path $dir 'Licenses.txt') + ',Chezhi.Licenses'))
        $messages = @(& $compiler @compilerArgs @refs @sources 2>&1)
        $code = $LASTEXITCODE
        $messages | ForEach-Object { Write-Host "$_" }
        if ($code -ne 0) {
            $report.outputs += [ordered]@{attempt=$name;exitCode=$code}
            $report.status = 'compile_failed'; Write-Report; return
        }
        if (-not (Test-Path -LiteralPath $out -PathType Leaf)) { throw 'Candidate compiler exited successfully without output' }
        $report.outputs += [ordered]@{attempt=$name;exitCode=$code;bytes=(Get-Item -LiteralPath $out).Length;sha256=(Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash}
    }
    $report.byteIdentical = $report.outputs[0].sha256 -ceq $report.outputs[1].sha256
    $report.status = if ($report.byteIdentical) { 'same_runner_gui_compile_identical' } else { 'same_runner_gui_compile_differs' }
    Write-Report
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
