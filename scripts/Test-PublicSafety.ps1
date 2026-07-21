[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$publicRoots = @(
    '.gitattributes',
    '.github',
    '.gitignore',
    'api',
    'assets',
    'benchmarks',
    'changelogs',
    'Directory.Build.props',
    'docs',
    'dotnet-tools.json',
    'global.json',
    'LICENSE',
    'Lunil.sln',
    'README.md',
    'README.zh-CN.md',
    'scripts',
    'src',
    'tests'
)
$credentialPatterns = [ordered]@{
    'private key' = '-----BEGIN (?:OPENSSH |RSA |EC |DSA )?PRIVATE KEY-----'
    'GitHub token' = '\b(?:gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{40,})\b'
    'AWS access key' = '\b(?:AKIA|ASIA)[A-Z0-9]{16}\b'
    'connection-string password' = '(?i)(?:^|[;\s])Password\s*=\s*[^;\s$][^;\r\n]*'
}

$trackedPaths = @(& git -C $repositoryRoot ls-files)
if ($LASTEXITCODE -ne 0) {
    throw 'Unable to enumerate tracked repository files.'
}

$violations = [Collections.Generic.List[string]]::new()
foreach ($path in $trackedPaths) {
    $normalized = $path.Replace('\', '/')
    $root = $normalized.Split('/')[0]
    if ($root -notin $publicRoots) {
        $violations.Add("Unexpected repository root: $normalized")
        continue
    }

    $fullPath = Join-Path $repositoryRoot $path
    if (-not [IO.File]::Exists($fullPath)) {
        continue
    }

    $fileInfo = [IO.FileInfo]::new($fullPath)
    if ($fileInfo.Length -gt 2MB) {
        continue
    }

    $bytes = [IO.File]::ReadAllBytes($fullPath)
    if ($bytes -contains 0) {
        continue
    }

    $text = [Text.Encoding]::UTF8.GetString($bytes)
    foreach ($pattern in $credentialPatterns.GetEnumerator()) {
        if ($text -match $pattern.Value) {
            $violations.Add("Potential $($pattern.Key): $normalized")
        }
    }
}

if ($violations.Count -ne 0) {
    throw "Public safety validation failed:`n$($violations -join "`n")"
}

Write-Output "Verified $($trackedPaths.Count) tracked files for public repository safety."
