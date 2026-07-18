[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$allowedExtensions = @('.cs', '.md', '.ps1', '.props', '.targets', '.yml', '.yaml')
$allTextRules = [ordered]@{
    'agent/tool identity' = '(?i)(?<![A-Za-z0-9_.])(?:Codex|Claude|aiproc)\b|(?:^|[/\\])(?:AGENTS|CLAUDE)\.md'
}
$markdownRules = [ordered]@{
    'private task tracking' = '(?i)\btasklist[/\\]|执行状态|长期执行记录|当前长期\s+goal'
    'internal milestone number' = '(?<![A-Za-z0-9_])M\d{1,3}(?:-\d+)?(?![A-Za-z0-9_])'
    'review pipeline transcript' = '(?i)\bexact(?:[- ]product)?[- ](?:commit|head)\b|\bPR\s+#\d+\b|\b(?:CI|workflow).{0,40}\brun\s+`?\d{6,}'
    'private evidence transcript' = '(?i)artifacts/(?:backend-performance|cross-runtime-performance|performance)/|\b(?:final\s+)?local\b.{0,40}\b(?:record|report|evidence)\b|\bprotected\s+(?:CI|workflow|run)\b|\bbefore merge\b'
}

Push-Location $repositoryRoot
try {
    $paths = @(& git ls-files --cached --others --exclude-standard | Sort-Object -Unique)
    if ($LASTEXITCODE -ne 0) { throw 'Unable to enumerate tracked files.' }
    $violations = [Collections.Generic.List[string]]::new()
    foreach ($path in $paths) {
        if ($path -eq 'scripts/Test-CommunityDocumentation.ps1') { continue }
        if ($allowedExtensions -notcontains [IO.Path]::GetExtension($path)) { continue }
        $fullPath = Join-Path $repositoryRoot $path
        if (-not (Test-Path -LiteralPath $fullPath)) { continue }
        $rules = [ordered]@{}
        foreach ($rule in $allTextRules.GetEnumerator()) { $rules[$rule.Key] = $rule.Value }
        if ([IO.Path]::GetExtension($path) -eq '.md') {
            foreach ($rule in $markdownRules.GetEnumerator()) { $rules[$rule.Key] = $rule.Value }
        }
        $lines = [IO.File]::ReadAllLines($fullPath)
        for ($index = 0; $index -lt $lines.Length; $index++) {
            foreach ($rule in $rules.GetEnumerator()) {
                if ($lines[$index] -match $rule.Value) {
                    $violations.Add("${path}:$($index + 1): $($rule.Key): $($lines[$index].Trim())")
                }
            }
        }
    }
    if ($violations.Count -gt 0) {
        throw "Community documentation boundary check failed:`n$($violations -join "`n")"
    }
    Write-Host 'Community documentation boundary check passed.'
} finally {
    Pop-Location
}
