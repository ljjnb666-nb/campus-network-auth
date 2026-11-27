# Standalone local Git fixture; no network or project binaries.
$ErrorActionPreference = 'Stop'
$observer = Join-Path $PSScriptRoot 'Get-CheckoutEvidence.ps1'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('git-observe-' + [Guid]::NewGuid().ToString('N'))
[void](New-Item -ItemType Directory -Path $tmp)
function Invoke-FixtureGit([string[]]$arguments) {
    & git -C $tmp @arguments 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Synthetic Git fixture setup failed' }
}
function Assert-State([string]$expected) {
    $observed = & $observer -Root $tmp
    if ($observed.state -cne $expected) { throw "Expected $expected; observed $($observed.state)" }
    if ($expected -ne 'unknown' -and $observed.commit -cnotmatch '^[0-9a-f]{40}$') { throw 'Missing commit evidence' }
}
try {
    Assert-State 'unknown'
    Invoke-FixtureGit @('init','-q')
    Assert-State 'unknown'
    [IO.File]::WriteAllText((Join-Path $tmp 'sample.txt'), 'original')
    [IO.File]::WriteAllText((Join-Path $tmp '.gitignore'), ('ignored.tmp' + [char]10))
    Invoke-FixtureGit @('add','--','sample.txt','.gitignore')
    Invoke-FixtureGit @('-c','user.name=Fixture','-c','user.email=fixture@example.invalid','-c','commit.gpgsign=false','commit','-qm','synthetic fixture')
    Assert-State 'clean'
    [IO.File]::WriteAllText((Join-Path $tmp 'sample.txt'), 'modified')
    Assert-State 'dirty'
    Invoke-FixtureGit @('add','--','sample.txt')
    Assert-State 'dirty'
    Invoke-FixtureGit @('restore','--staged','--worktree','--','sample.txt')
    [IO.File]::WriteAllText((Join-Path $tmp 'extra.txt'), 'untracked')
    Assert-State 'dirty'
    Remove-Item -LiteralPath (Join-Path $tmp 'extra.txt')
    [IO.File]::WriteAllText((Join-Path $tmp 'ignored.tmp'), 'ignored')
    Assert-State 'clean'
    $child = Join-Path $tmp 'nested'
    [void](New-Item -ItemType Directory -Path $child)
    if ((& $observer -Root $child).state -cne 'unknown') { throw 'Nested directory falsely attributed to checkout root' }
    Write-Host 'CHECKOUT_EVIDENCE_SELFTEST_PASS cases=8'
} finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force
}
