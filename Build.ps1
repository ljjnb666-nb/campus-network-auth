param(
    # 认证核心 exe 路径。缺省依次尝试仓库根目录 认证核心.exe、build-core\认证核心-源码重建.exe
    [string]$CorePath = ''
)
$ErrorActionPreference = 'Stop'

$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler -PathType Leaf)) { throw '找不到 .NET Framework C# 编译器（csc.exe）。' }

$programPath = Join-Path $PSScriptRoot 'Program.cs'
foreach ($path in @($programPath, (Join-Path $PSScriptRoot 'CredentialStore.cs'), (Join-Path $PSScriptRoot 'ConnectionStates.cs'), (Join-Path $PSScriptRoot 'ServiceIdentity.cs'), (Join-Path $PSScriptRoot 'ServiceJournal.cs'), (Join-Path $PSScriptRoot 'chezhi.ico'), (Join-Path $PSScriptRoot 'Licenses.txt'), (Join-Path $PSScriptRoot 'app.manifest'), (Join-Path $PSScriptRoot 'app.config'))) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "缺少构建文件：$path" }
}

# 核心校验值以 Program.cs 为唯一来源（运行时 VerifyCore 用同一常量），不再双处维护
$programSource = Get-Content -LiteralPath $programPath -Raw -Encoding UTF8
$coreHashMatch = [regex]::Match($programSource, 'private const string CoreHash = "([0-9A-Fa-f]{64})";')
if (-not $coreHashMatch.Success) { throw '无法从 Program.cs 读取核心校验值（CoreHash）。' }
$expected = $coreHashMatch.Groups[1].Value

# 定位认证核心：显式参数 > 根目录 > 最近一次源码重建产物
if (-not $CorePath) {
    $candidates = @((Join-Path $PSScriptRoot '认证核心.exe'), (Join-Path $PSScriptRoot 'build-core\认证核心-源码重建.exe'))
    foreach ($candidate in $candidates) { if (Test-Path -LiteralPath $candidate -PathType Leaf) { $CorePath = $candidate; break } }
}
if (-not $CorePath -or -not (Test-Path -LiteralPath $CorePath -PathType Leaf)) {
    throw "找不到认证核心 exe。请先运行 Build-Core.ps1 从源码构建，或用 -CorePath 指定核心文件。"
}
$actual = (Get-FileHash -LiteralPath $CorePath -Algorithm SHA256).Hash
if ($actual -ne $expected) { throw "认证核心哈希不符：$CorePath（实际 $actual，期望 $expected）。请重新运行 Build-Core.ps1 并按提示同步 CoreHash。" }

$out = Join-Path $PSScriptRoot '澈知科技.exe'
$tmpOut = Join-Path $PSScriptRoot 'build-core\澈知科技-new.exe'
[void](New-Item -ItemType Directory -Path (Join-Path $PSScriptRoot 'build-core') -Force)
if (Test-Path -LiteralPath $tmpOut) { Remove-Item -LiteralPath $tmpOut -Force }

$refs = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.ServiceProcess.dll','/r:System.Management.dll','/r:System.Security.dll')
$arguments = @('/nologo','/target:winexe','/optimize+','/platform:anycpu',('/win32icon:' + (Join-Path $PSScriptRoot 'chezhi.ico')),('/win32manifest:' + (Join-Path $PSScriptRoot 'app.manifest')),('/out:' + $tmpOut),('/resource:' + $CorePath + ',Chezhi.AuthCore'),('/resource:' + (Join-Path $PSScriptRoot 'Licenses.txt') + ',Chezhi.Licenses'))
& $compiler $arguments $refs $programPath (Join-Path $PSScriptRoot 'CredentialStore.cs') (Join-Path $PSScriptRoot 'ConnectionStates.cs') (Join-Path $PSScriptRoot 'ServiceIdentity.cs') (Join-Path $PSScriptRoot 'ServiceJournal.cs')
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }

# 编译成功才替换正式产物；失败时旧 exe 原样保留
Move-Item -LiteralPath $tmpOut -Destination $out -Force
# 高 DPI 配置随产物分发（.NET Framework 4.7+ 读取；缺失时回退系统级缩放）
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'app.config') -Destination (Join-Path $PSScriptRoot '澈知科技.exe.config') -Force

# 构建清单
$gitSha = 'unknown'
try { $gitSha = (& git rev-parse HEAD 2>$null | Select-Object -First 1).Trim() } catch { }
$appVersion = 'unknown'
try {
    $m = [regex]::Match($programSource, 'AssemblyVersion\("([0-9.]+)"\)')
    if ($m.Success) { $appVersion = $m.Groups[1].Value }
} catch { }
$manifest = [ordered]@{
    appVersion = $appVersion
    gitCommit  = $gitSha
    builtAtUtc = (Get-Date).ToUniversalTime().ToString('o')
    compiler   = $compiler
    core       = [ordered]@{ path = $CorePath; sha256 = $actual }
    outputs    = @([ordered]@{ file = '澈知科技.exe'; sha256 = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash; bytes = (Get-Item -LiteralPath $out).Length })
}
$manifest | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $PSScriptRoot 'build-core\gui-build-manifest.json') -Encoding UTF8

Write-Host '已生成 澈知科技.exe。'
Write-Host "认证核心：$CorePath（SHA-256 校验通过）"
