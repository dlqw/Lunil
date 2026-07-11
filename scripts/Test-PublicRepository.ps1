[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))

$trackedPaths = @(& git -C $repositoryRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate tracked repository files.'
}

$privatePathPatterns = @(
    '(^|/)tasklist(?:/|$)',
    '(^|/)\.ai_proc(?:/|$)',
    '(^|/)\.codex(?:/|$)',
    '(^|/)AGENTS\.md$'
)

$violations = @(
    $trackedPaths |
        Where-Object {
            $path = $_
            $privatePathPatterns.Where({ $path -match $_ }, 'First').Count -ne 0
        } |
        Sort-Object -Unique
)

if ($violations.Count -ne 0) {
    $formatted = $violations | ForEach-Object { "  - $_" }
    throw "Private planning or agent files are tracked and would be published:`n$($formatted -join "`n")"
}

Write-Output "Verified $($trackedPaths.Count) tracked files: no private planning or agent paths are public."
