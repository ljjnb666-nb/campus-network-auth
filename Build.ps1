$ErrorActionPreference = 'Stop'
$core = Join-Path $PSScriptRoot '认证核心.exe'
$expected = 'A691A52D6536E4CE190279D1A676F9C128E453B30D1286BAB63F59B99A77DDB3'
if ((Get-FileHash -LiteralPath $core -Algorithm SHA256).Hash -ne $expected) { throw '认证核心哈希不符，未构建。' }
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$refs = @('/r:System.dll','/r:System.Core.dll','/r:System.Drawing.dll','/r:System.Windows.Forms.dll','/r:System.Web.Extensions.dll','/r:System.ServiceProcess.dll','/r:System.Management.dll','/r:System.Security.dll')
$arguments = @('/nologo','/target:winexe','/optimize+','/platform:anycpu',('/win32icon:' + (Join-Path $PSScriptRoot 'chezhi.ico')),('/out:' + (Join-Path $PSScriptRoot '澈知科技.exe')),('/resource:' + $core + ',Chezhi.AuthCore'),('/resource:' + (Join-Path $PSScriptRoot 'Licenses.txt') + ',Chezhi.Licenses'))
& $compiler $arguments $refs (Join-Path $PSScriptRoot 'Program.cs') (Join-Path $PSScriptRoot 'CredentialStore.cs')
if ($LASTEXITCODE -ne 0) { throw '编译失败。' }
Write-Host '已生成 澈知科技.exe。'
