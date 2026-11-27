# Isolated diagnostic only. No downloads or replacement of the default
# compiler or output. Only the existing explicit offline self-test entries are executed.
# Observations are not locks.
param([string]$BuildDirectory = 'build-core')
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$root = Split-Path -Parent $PSScriptRoot
$build = (Resolve-Path -LiteralPath $BuildDirectory).Path
$reportPath = Join-Path $build 'gui-compiler-candidate.json'
if (Test-Path -LiteralPath $reportPath) { throw 'Candidate report already exists' }
$report = [ordered]@{
    schemaVersion = 2
    scope = 'isolated same-runner GUI compile and existing offline self-test suite; no normal launch or independent provenance verification'
    checkout = (git -C $root rev-parse HEAD).Trim()
    status = 'not_started'
    compiler = $null
    referenceFiles = @()
    coreSha256 = $null
    outputs = @()
    byteIdentical = $false
    uiStress = [ordered]@{status='not_run';exitCode=$null;sha256=$null;configSha256=$null}
    offlineSuite = [ordered]@{status='not_run';expectedCount=11;passedCount=0;results=@()}
}
function Write-Report {
    $report | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $reportPath -Encoding utf8
    Write-Host "GUI_COMPILER_CANDIDATE status=$($report.status) byteIdentical=$($report.byteIdentical)"
    Write-Host "GUI_CANDIDATE_UI_STRESS status=$($report.uiStress.status) exitCode=$($report.uiStress.exitCode)"
    Write-Host "GUI_CANDIDATE_OFFLINE_SUITE status=$($report.offlineSuite.status) passed=$($report.offlineSuite.passedCount)/$($report.offlineSuite.expectedCount)"
}
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
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
    if ($report.byteIdentical) {
        # Test the actual candidate, not the baseline executable. Preserve the
        # existing config bytes and run only the explicitly listed offline entries.
        $runDir = Join-Path $temp 'one'
        $candidate = Join-Path $runDir 'Candidate.exe'
        $configName = '澈知科技-源码重建.exe.config'
        $baselineConfig = Join-Path $build $configName
        $configEntry = @($manifest.outputs | Where-Object { $_.file -ceq $configName })
        $configHash = (Get-FileHash -LiteralPath $baselineConfig -Algorithm SHA256).Hash
        if ($configEntry.Count -ne 1 -or $configEntry[0].sha256 -cne $configHash) { throw 'Candidate config digest mismatch' }
        $candidateConfig = $candidate + '.config'
        Copy-Item -LiteralPath $baselineConfig -Destination $candidateConfig
        if ((Get-FileHash -LiteralPath $candidateConfig -Algorithm SHA256).Hash -cne $configHash) { throw 'Copied candidate config mismatch' }
        $candidateHash = (Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash
        if ($candidateHash -cne $report.outputs[0].sha256) { throw 'Candidate changed before UI test' }
        $report.uiStress.sha256 = $candidateHash
        $report.uiStress.configSha256 = $configHash
        # The baseline workflow uses the same signed, embedded-signature fixture.
        $fixtures = @(
            (Get-Command pwsh.exe).Source,
            "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe",
            "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
        )
        $fixture = $fixtures | Where-Object {
            if ($_ -and (Test-Path -LiteralPath $_ -PathType Leaf)) {
                $sig = Get-AuthenticodeSignature -LiteralPath $_
                $sig.Status -eq 'Valid' -and $sig.SignatureType -eq 'Authenticode'
            }
        } | Select-Object -First 1
        if (-not $fixture) {
            $report.offlineSuite.status = 'signature_fixture_unavailable'
            Write-Report
            return
        }
        $report.offlineSuite.signatureFixtureSha256 = (Get-FileHash -LiteralPath $fixture -Algorithm SHA256).Hash
        $tests = @(
            @{ name='logic'; arguments='--self-test-logic'; markers=@('^LOGIC_PASS$','^OPERATION_MATRIX_PASS cases=256$') },
            @{ name='credentials'; arguments='--self-test-credentials'; markers=@('^CREDENTIALS_PASS$') },
            @{ name='core'; arguments='--self-test-core'; markers=@('^CORE_PASS$') },
            @{ name='native-input'; arguments='--self-test-native-input'; markers=@('^NATIVE_INPUT_PASS cases=19$') },
            @{ name='native-net'; arguments='--self-test-native-net'; markers=@('^NATIVE_NET_RESOURCE_PASS$') },
            @{ name='stop'; arguments='--self-test-stop'; markers=@('^STOP_PASS$') },
            @{ name='resources'; arguments='--self-test-resources'; markers=@('^CORE_INTEGRITY_HANDLE_PASS$','^CORE_RESOURCE_PASS$') },
            @{ name='ui'; arguments='--self-test-ui'; markers=@('^UI_LAYOUT_MATRIX_PASS cases=36(?: |$)','^UI_DISPOSE_PASS$','^UI_TRAY_PASS$') },
            @{ name='app-lifecycle'; arguments='--self-test-app-lifecycle'; markers=@('^APP_LIFECYCLE_PASS cycles=100(?: |$)') },
            @{ name='ui-stress'; arguments='--self-test-ui-stress'; markers=@('^UI_SCROLL_PASS checks=715(?: |$)','^UI_STRESS_PASS warmup=10 measured=100(?: |$)') },
            @{ name='signature'; arguments=('--self-test-signature "' + $fixture + '"'); markers=@('^SIGNATURE_PASS$') }
        )
        if ($tests.Count -ne $report.offlineSuite.expectedCount) { throw 'Candidate test count mismatch' }
        $report.offlineSuite.status = 'running'
        foreach ($test in $tests) {
            $entry = [ordered]@{ name=$test.name; status='not_run'; exitCode=$null; matchedMarkers=@() }
            $report.offlineSuite.results += $entry
            if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -cne $candidateHash -or
                (Get-FileHash -LiteralPath $candidateConfig -Algorithm SHA256).Hash -cne $configHash) {
                throw 'Candidate bytes changed before test'
            }
            $stdout = Join-Path $runDir ($test.name + '.stdout')
            $stderr = Join-Path $runDir ($test.name + '.stderr')
            $process = Start-Process -FilePath $candidate -WorkingDirectory $runDir -ArgumentList $test.arguments -RedirectStandardOutput $stdout -RedirectStandardError $stderr -PassThru
            try {
                if (-not $process.WaitForExit(120000)) {
                    # Only terminate the process started by this diagnostic.
                    $process.Kill()
                    if (-not $process.WaitForExit(5000)) { throw 'Candidate process did not exit after termination' }
                    $entry.status = 'timeout'
                } else {
                    $entry.exitCode = $process.ExitCode
                    $lines = @(Get-Content -LiteralPath $stdout)
                    foreach ($pattern in $test.markers) {
                        $matched = @($lines | Where-Object { $_ -cmatch $pattern })
                        if ($matched.Count -eq 1) {
                            $entry.matchedMarkers += $matched[0]
                            Write-Host $matched[0]
                        }
                    }
                    $entry.status = if ($process.ExitCode -eq 0 -and $entry.matchedMarkers.Count -eq $test.markers.Count) { 'passed' } else { 'failed' }
                }
            } finally {
                $process.Dispose()
            }
            if ((Get-FileHash -LiteralPath $candidate -Algorithm SHA256).Hash -cne $candidateHash -or
                (Get-FileHash -LiteralPath $candidateConfig -Algorithm SHA256).Hash -cne $configHash) {
                throw 'Candidate bytes changed during test'
            }
            if ($test.name -eq 'ui-stress') {
                $report.uiStress.status = $entry.status
                $report.uiStress.exitCode = $entry.exitCode
            }
            Write-Host "GUI_CANDIDATE_TEST name=$($entry.name) status=$($entry.status) exitCode=$($entry.exitCode)"
            if ($entry.status -ne 'passed') {
                $report.offlineSuite.status = $entry.status
                # Stop on the first failed or timed-out case. Later cases
                # remain explicitly unverified, not implicitly passed.
                break
            }
            $report.offlineSuite.passedCount++
        }
        if ($report.offlineSuite.passedCount -eq $report.offlineSuite.expectedCount) {
            $report.offlineSuite.status = 'passed'
        }
    }
    Write-Report
} finally {
    Remove-Item -LiteralPath $temp -Recurse -Force
}
