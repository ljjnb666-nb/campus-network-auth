# Observe Git checkout state without logging file names or contents.
# This is not an immutable source snapshot or a signed provenance statement.
param([Parameter(Mandatory=$true)][string]$Root)
$ErrorActionPreference = 'Stop'
$result = [ordered]@{ commit = 'unknown'; state = 'unknown' }
try {
    $git = Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1
    $head = @(& $git.Source -C $Root rev-parse --verify HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0] -cnotmatch '^[0-9a-f]{40}$') { return $result }
    $result.commit = [string]$head[0]
    $status = @(& $git.Source -C $Root status --porcelain=v1 --untracked-files=normal --ignore-submodules=none 2>$null)
    if ($LASTEXITCODE -ne 0) { return $result }
    $after = @(& $git.Source -C $Root rev-parse --verify HEAD 2>$null)
    if ($LASTEXITCODE -ne 0 -or $after.Count -ne 1 -or $after[0] -cne $result.commit) {
        $result.commit = 'unknown'
        return $result
    }
    $result.state = if ($status.Count -eq 0) { 'clean' } else { 'dirty' }
} catch {
    # Fail closed for provenance; do not disclose command output or paths.
    $result.state = 'unknown'
}
return $result
