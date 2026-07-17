[CmdletBinding()]
param(
    [string] $InputPath = '',

    [string] $OutputPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = (& (Join-Path $PSScriptRoot 'Get-LunilVersion.ps1')).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Could not resolve the active Lunil release version.'
}
$releaseGate = "$version-conformance"
if ([string]::IsNullOrWhiteSpace($InputPath)) {
    $InputPath = Join-Path $repositoryRoot 'artifacts/conformance'
}
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $repositoryRoot `
        'artifacts/conformance/conformance-six-rid-evidence.json'
}
$inputRoot = [System.IO.Path]::GetFullPath($InputPath)
$outputFile = [System.IO.Path]::GetFullPath($OutputPath)

$requiredRids = @(
    'linux-x64',
    'linux-arm64',
    'win-x64',
    'win-arm64',
    'osx-x64',
    'osx-arm64'
)
$requiredBackends = @(
    'interpreter',
    'executor-auto',
    'coreclr-tier1-jit',
    'coreclr-tier2-jit',
    'experimental-loop-osr'
)
$candidates = Get-ChildItem -LiteralPath $inputRoot -Recurse -Filter 'evidence.json' |
    ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName
            LastWriteTimeUtc = $_.LastWriteTimeUtc
            Evidence = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        }
    }

$selected = [Collections.Generic.List[object]]::new()
foreach ($rid in $requiredRids) {
    $match = $candidates | Where-Object { $_.Evidence.Rid -eq $rid } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $match) {
        throw "Missing conformance evidence for RID $rid under $inputRoot."
    }

    $evidence = $match.Evidence
    if ($evidence.SchemaVersion -ne 1 -or
        $evidence.ReleaseGate -ne $releaseGate -or
        -not $evidence.AllPassed) {
        throw "RID $rid has invalid or failing conformance evidence: $($match.Path)"
    }
    if (@($evidence.TestRuns).Count -ne 3 -or
        @($evidence.TestRuns | Where-Object {
            $_.Total -ne $_.ExpectedTests -or
            $_.Executed -ne $_.ExpectedTests -or
            $_.Passed -ne $_.ExpectedTests -or
            $_.Failed -ne 0
        }).Count -ne 0) {
        throw "RID $rid does not have three complete passing test runs."
    }
    if ((@($evidence.ObservableGoldens.Backends) -join ',') -ne
            ($requiredBackends -join ',') -or
        $evidence.ObservableGoldens.CaseCount -ne 8 -or
        $evidence.OfficialSuite.FinalMarker -ne 'final OK !!!') {
        throw "RID $rid does not satisfy the fixed conformance corpus contract."
    }

    $selected.Add($evidence)
}

$first = $selected[0]
$contractFields = @(
    'GitCommit',
    'OfficialSuite.ArchiveSha256',
    'OfficialSuite.ManifestSha256',
    'ObservableGoldens.DocumentSha256',
    'ObservableGoldens.OracleExecutableSha256'
)
foreach ($field in $contractFields) {
    $segments = $field.Split('.')
    $expected = $first
    foreach ($segment in $segments) { $expected = $expected.$segment }
    foreach ($evidence in $selected) {
        $actual = $evidence
        foreach ($segment in $segments) { $actual = $actual.$segment }
        if ($actual -ne $expected) {
            throw "Six-RID conformance evidence disagrees on $field."
        }
    }
}

$backendContract = @($first.ObservableGoldens.Backends) -join ','
foreach ($evidence in $selected) {
    if ((@($evidence.ObservableGoldens.Backends) -join ',') -ne $backendContract) {
        throw "RID $($evidence.Rid) has a different backend catalog."
    }
}

$result = [ordered]@{
    SchemaVersion = 1
    ReleaseGate = "$releaseGate-six-rid"
    GeneratedAtUtc = [DateTime]::UtcNow.ToString('O')
    RequiredRids = $requiredRids
    GitCommit = $first.GitCommit
    OfficialSuite = $first.OfficialSuite
    ObservableGoldens = $first.ObservableGoldens
    Stability = $first.Stability
    Evidence = $selected.ToArray()
    AllRidsPassed = $true
}

New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($outputFile)) `
    -Force | Out-Null
$result | ConvertTo-Json -Depth 12 |
    Set-Content -LiteralPath $outputFile -Encoding utf8
$result | ConvertTo-Json -Depth 4
Write-Host "Six-RID conformance evidence written to $outputFile"
