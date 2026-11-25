param(
    [string]$CMake = '',
    [string]$Ninja = '',
    [string]$Gcc = '',
    [string]$Gxx = '',
    [string]$WorkDirectory = ''
)
$ErrorActionPreference = 'Stop'

function Find-Tool([string]$chosen, [string]$name, [string[]]$fallbacks) {
    if ($chosen) {
        if (-not (Test-Path -LiteralPath $chosen -PathType Leaf)) { throw "找不到指定的 $name：$chosen" }
        return (Resolve-Path -LiteralPath $chosen).Path
    }
    $command = Get-Command $name -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    foreach ($candidate in $fallbacks) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate } }
    throw "找不到 $name。请先安装 CMake、Ninja 和同一套 MinGW-w64 GCC/G++，或通过脚本参数指定路径。"
}
# 通过 vswhere 通用发现 Visual Studio 自带的 CMake/Ninja（不硬编码任何个人安装路径）
function Find-VsTool([string]$relative) {
    try {
        $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
        if (-not (Test-Path -LiteralPath $vswhere -PathType Leaf)) { return $null }
        $vsRoot = (& $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath 2>$null | Select-Object -First 1)
        if (-not $vsRoot) { $vsRoot = (& $vswhere -latest -products * -property installationPath 2>$null | Select-Object -First 1) }
        if (-not $vsRoot) { return $null }
        $candidate = Join-Path $vsRoot $relative
        if (Test-Path -LiteralPath $candidate -PathType Leaf) { return $candidate }
    } catch { }
    return $null
}
function Invoke-BuildTool([string]$tool, [string[]]$toolArgs) {
    & $tool @toolArgs
    if ($LASTEXITCODE -ne 0) { throw "构建命令失败，退出码 $LASTEXITCODE。请保留构建目录检查输出。" }
}

$CMake = Find-Tool $CMake 'cmake.exe' @(
    'C:\Program Files\CMake\bin\cmake.exe', 'C:\Program Files (x86)\CMake\bin\cmake.exe',
    (Find-VsTool 'Common7\IDE\CommonExtensions\Microsoft\CMake\CMake\bin\cmake.exe'))
$Ninja = Find-Tool $Ninja 'ninja.exe' @(
    'C:\msys64\ucrt64\bin\ninja.exe', 'C:\Program Files\CMake\bin\ninja.exe',
    (Find-VsTool 'Common7\IDE\CommonExtensions\Microsoft\CMake\Ninja\ninja.exe'))
$Gcc = Find-Tool $Gcc 'gcc.exe' @('C:\msys64\ucrt64\bin\gcc.exe')
$Gxx = Find-Tool $Gxx 'g++.exe' @('C:\msys64\ucrt64\bin\g++.exe')
# 固定优先使用 Windows 自带的 bsdtar (System32): 从 Git Bash/MSYS 环境启动时,
# PATH 里排在前的 GNU tar 会把 "D:\..." 盘符当成远程主机而失败
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tar -PathType Leaf)) { $tar = Find-Tool '' 'tar.exe' @() }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '找不到 .NET Framework C# 编译器。' }

$project = Join-Path $PSScriptRoot '认证核心源码\esurfingclient\app'
foreach ($path in @((Join-Path $project 'CMakeLists.txt'), (Join-Path $PSScriptRoot 'Program.cs'), (Join-Path $PSScriptRoot 'CredentialStore.cs'), (Join-Path $PSScriptRoot 'ConnectionStates.cs'), (Join-Path $PSScriptRoot 'ServiceIdentity.cs'), (Join-Path $PSScriptRoot 'ServiceJournal.cs'), (Join-Path $PSScriptRoot 'chezhi.ico'), (Join-Path $PSScriptRoot 'Licenses.txt'), (Join-Path $PSScriptRoot 'app.manifest'), (Join-Path $PSScriptRoot 'app.config'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少构建文件：$path" }
}

# 依赖获取：固定版本/来源/SHA-256，缓存命中即离线可用；失败或哈希不符直接终止
$curlVersion = '8.23.0'
$curlArchive = (& (Join-Path $PSScriptRoot 'scripts\Fetch-Curl.ps1') -Version $curlVersion) | Select-Object -Last 1
if (-not (Test-Path -LiteralPath $curlArchive -PathType Leaf)) { throw "curl 源码包获取失败：$curlArchive" }

# 记录工具链版本（写入构建清单）
function Get-FirstLine([string]$tool, [string[]]$toolArgs) {
    try { $out = & $tool @toolArgs 2>$null | Select-Object -First 1; if ($out) { return ([string]$out).Trim() } } catch { }
    return 'unknown'
}
$toolVersions = @{
    cmake    = Get-FirstLine $CMake @('--version')
    ninja    = Get-FirstLine $Ninja @('--version')
    gcc      = Get-FirstLine $Gcc @('--version')
    gxx      = Get-FirstLine $Gxx @('--version')
    csc      = (Get-Item -LiteralPath $compiler).VersionInfo.FileVersion
    tar      = Get-FirstLine $tar @('--version')
}

$build = Join-Path $PSScriptRoot 'build-core'
if (Test-Path -LiteralPath $build) { throw "构建目录已存在：$build。为保护已有文件，请先自行选择其他目录保存旧结果。" }
$tempRoot = if ($WorkDirectory) { [IO.Path]::GetFullPath($WorkDirectory) } else { [IO.Path]::GetFullPath([IO.Path]::GetTempPath()) }
if (-not (Test-Path -LiteralPath $tempRoot -PathType Container)) { throw "临时目录不存在：$tempRoot" }
if ($tempRoot -match '[^\x20-\x7e]') { throw '临时目录含有非 ASCII 字符。请通过 -WorkDirectory 指定已有的英文路径。' }
$work = Join-Path $tempRoot ('CheZhiBuild-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $build)
[void](New-Item -ItemType Directory -Path $work)
$curlSource = Join-Path $work "curl-$curlVersion"
$curlBuild = Join-Path $work 'curl-build'
$prefix = Join-Path $work 'curl-prefix'
$coreBuild = Join-Path $work 'auth-build'
$env:PATH = (Split-Path -Parent $Gcc) + ';' + (Split-Path -Parent $Ninja) + ';' + $env:PATH

# Snapshot into our own build directory; re-check the bytes actually extracted.
$lockedCurlHash = 'E3DE77CFA6367083C776A26647FC9F633B538C06C3BAA73D931A252AC8F72B49'
$verifiedArchive = Join-Path $work "curl-$curlVersion.tar.gz"
[IO.File]::Copy($curlArchive, $verifiedArchive, $false)
$actualCurlHash = (Get-FileHash -LiteralPath $verifiedArchive -Algorithm SHA256).Hash
if ($actualCurlHash -cne $lockedCurlHash) { throw 'Build snapshot of curl failed SHA-256 verification.' }
Invoke-BuildTool $tar @('-xzf', $verifiedArchive, '-C', $work)
Invoke-BuildTool $CMake @('-S', $curlSource, '-B', $curlBuild, '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release', "-DCMAKE_MAKE_PROGRAM=$Ninja", "-DCMAKE_C_COMPILER=$Gcc", "-DCMAKE_CXX_COMPILER=$Gxx", "-DCMAKE_INSTALL_PREFIX=$prefix", '-DBUILD_SHARED_LIBS=OFF', '-DBUILD_STATIC_LIBS=ON', '-DBUILD_CURL_EXE=OFF', '-DBUILD_TESTING=OFF', '-DBUILD_EXAMPLES=OFF', '-DBUILD_LIBCURL_DOCS=OFF', '-DBUILD_MISC_DOCS=OFF', '-DENABLE_CURL_MANUAL=OFF', '-DCURL_USE_SCHANNEL=ON', '-DCURL_USE_OPENSSL=OFF', '-DCURL_USE_LIBPSL=OFF', '-DCURL_USE_LIBSSH2=OFF', '-DCURL_ZLIB=OFF', '-DCURL_BROTLI=OFF', '-DCURL_ZSTD=OFF')
Invoke-BuildTool $CMake @('--build', $curlBuild, '--parallel', '4')
Invoke-BuildTool $CMake @('--install', $curlBuild)
Invoke-BuildTool $CMake @('-S', $project, '-B', $coreBuild, '-G', 'Ninja', '-DCMAKE_BUILD_TYPE=Release', "-DCMAKE_MAKE_PROGRAM=$Ninja", "-DCMAKE_C_COMPILER=$Gcc", "-DCMAKE_CXX_COMPILER=$Gxx", "-DCMAKE_PREFIX_PATH=$prefix", '-DBUILD_SHARED_LIBS=OFF')
Invoke-BuildTool $CMake @('--build', $coreBuild, '--parallel', '4')

$core = Join-Path $coreBuild 'ESurfingClient.exe'
if (-not (Test-Path -LiteralPath $core -PathType Leaf)) { throw '未找到新生成的认证核心。' }
$coreHash = (Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash
$program = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Program.cs') -Raw -Encoding UTF8
$pattern = 'private const string CoreHash = "[0-9A-Fa-f]{64}";'
if ([regex]::Matches($program, $pattern).Count -ne 1) { throw '无法安全定位桌面程序的核心校验值。' }
$program = [regex]::Replace($program, $pattern, ('private const string CoreHash = "' + $coreHash + '";'))
$generatedProgram = Join-Path $work 'Program.generated.cs'
Set-Content -LiteralPath $generatedProgram -Value $program -Encoding UTF8
$result = Join-Path $build '澈知科技-源码重建.exe'
$refs = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.ServiceProcess.dll','/r:System.Management.dll','/r:System.Security.dll')
$arguments = @('/nologo','/target:winexe','/optimize+','/platform:anycpu',('/win32icon:' + (Join-Path $PSScriptRoot 'chezhi.ico')),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/out:' + $result),('/resource:' + $core + ',Chezhi.AuthCore'),('/resource:' + (Join-Path $PSScriptRoot 'Licenses.txt') + ',Chezhi.Licenses'))
& $compiler $arguments $refs $generatedProgram (Join-Path $PSScriptRoot 'CredentialStore.cs') (Join-Path $PSScriptRoot 'ConnectionStates.cs') (Join-Path $PSScriptRoot 'ServiceIdentity.cs') (Join-Path $PSScriptRoot 'ServiceJournal.cs')
if ($LASTEXITCODE -ne 0) { throw '桌面程序编译失败。' }
[System.IO.File]::Copy($core, (Join-Path $build '认证核心-源码重建.exe'), $false)
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination (Join-Path $build '澈知科技-源码重建.exe.config') -Force

# 构建清单：版本 / 提交 / 工具链 / 依赖 / 产物哈希（可复现性证据，随产物一起归档）
$gitSha = 'unknown'
try { $gitSha = (& git rev-parse HEAD 2>$null | Select-Object -First 1).Trim() } catch { }
$appVersion = 'unknown'
try {
    $m = [regex]::Match((Get-Content -LiteralPath (Join-Path $PSScriptRoot 'Program.cs') -Raw -Encoding UTF8), 'AssemblyVersion\("([0-9.]+)"\)')
    if ($m.Success) { $appVersion = $m.Groups[1].Value }
} catch { }
$guiOut = Join-Path $build '澈知科技-源码重建.exe'
$coreCopy = Join-Path $build '认证核心-源码重建.exe'
$configCopy = Join-Path $build '澈知科技-源码重建.exe.config'
$cjsonRoot = Join-Path $project 'third_party\cJSON'
$cjsonFiles = @('cJSON.c', 'cJSON.h') | ForEach-Object {
    [ordered]@{ file = $_; sha256 = (Get-FileHash -LiteralPath (Join-Path $cjsonRoot $_) -Algorithm SHA256).Hash }
}
$manifest = [ordered]@{
    schemaVersion = 2
    appVersion = $appVersion
    gitCommit  = $gitSha
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    toolchain  = $toolVersions
    cscPath    = $compiler
    dependencies = [ordered]@{
        curl = [ordered]@{
            version = $curlVersion
            sha256  = $actualCurlHash
            source  = "https://curl.se/download/curl-$curlVersion.tar.gz"
            archive = [IO.Path]::GetFileName($verifiedArchive)
        }
        cJSON = [ordered]@{
            version = '1.7.19'
            source = 'vendored source at gitCommit'
            files = @($cjsonFiles)
        }
    }
    outputs = @(
        [ordered]@{ file = '认证核心-源码重建.exe'; sha256 = $coreHash; bytes = (Get-Item -LiteralPath $coreCopy).Length },
        [ordered]@{ file = '澈知科技-源码重建.exe'; sha256 = (Get-FileHash -LiteralPath $guiOut -Algorithm SHA256).Hash; bytes = (Get-Item -LiteralPath $guiOut).Length },
        [ordered]@{ file = '澈知科技-源码重建.exe.config'; sha256 = (Get-FileHash -LiteralPath $configCopy -Algorithm SHA256).Hash; bytes = (Get-Item -LiteralPath $configCopy).Length }
    )
}
$manifest | ConvertTo-Json -Depth 7 | Set-Content -LiteralPath (Join-Path $build 'build-manifest.json') -Encoding UTF8

Write-Host "源码重建完成：$result"
Write-Host "认证核心 SHA-256：$coreHash"
Write-Host "临时构建文件：$work"
Write-Host '未覆盖原认证核心或桌面 EXE。请先验证后再选择是否使用新文件。'
