[CmdletBinding()]
param(
    [string] $InputDirectory,
    [string] $OutputDirectory,
    [string[]] $ExpectedRids = @(
        'win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($InputDirectory)) {
    $InputDirectory = Join-Path $repositoryRoot 'artifacts/cross-runtime-performance'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts/cross-runtime-performance'
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

$reports = @(Get-ChildItem -LiteralPath $InputDirectory -Recurse -Filter report.json |
    ForEach-Object { Get-Content -Raw -LiteralPath $_.FullName | ConvertFrom-Json })
if ($reports.Count -eq 0) { throw "No cross-runtime report.json files were found under $InputDirectory." }

$duplicateRids = @($reports | Group-Object { $_.environment.rid } | Where-Object Count -ne 1)
if ($duplicateRids.Count -gt 0) {
    throw "Expected one report per RID; duplicates: $($duplicateRids.Name -join ', ')."
}
$actualRids = @($reports | ForEach-Object { $_.environment.rid })
$missingRids = @($ExpectedRids | Where-Object { $_ -notin $actualRids })
if ($missingRids.Count -gt 0) { throw "Missing cross-runtime reports: $($missingRids -join ', ')." }
foreach ($report in $reports) {
    if ($report.schemaVersion -ne 2) {
        throw "Cross-runtime report for $($report.environment.rid) has unsupported schema $($report.schemaVersion)."
    }
    if (-not $report.completeness.complete) {
        throw "Cross-runtime report for $($report.environment.rid) is incomplete."
    }
    if (-not $report.performanceGate.complete -or -not $report.performanceGate.passed) {
        $failures = @($report.performanceGate.measurements | Where-Object { -not $_.passed } |
            ForEach-Object { "$($_.workload)/$($_.candidateEngine): $($_.failure)" })
        throw "Lunil did not stably beat MoonSharp on $($report.environment.rid): $($failures -join '; ')."
    }
}

function Get-GeometricMean([double[]] $Values) {
    if ($Values.Count -eq 0) { return 0.0 }
    return [Math]::Exp(($Values | ForEach-Object { [Math]::Log($_) } | Measure-Object -Average).Average)
}

$engineIds = @($reports[0].engines | ForEach-Object id)
$aggregateEngines = foreach ($engineId in $engineIds) {
    $values = @($reports | ForEach-Object {
        $_.results | Where-Object engine -eq $engineId | ForEach-Object speedupVsNativeLua
    })
    $comparisonValues = @($reports | ForEach-Object {
        $_.results | Where-Object engine -eq $engineId | ForEach-Object speedupVsComparison
    } | Where-Object { $null -ne $_ })
    [pscustomobject]@{
        Engine = $engineId
        Measurements = $values.Count
        GeometricMeanSpeedupVsNativeLua = Get-GeometricMean $values
        MinimumSpeedupVsNativeLua = ($values | Measure-Object -Minimum).Minimum
        MaximumSpeedupVsNativeLua = ($values | Measure-Object -Maximum).Maximum
        GeometricMeanSpeedupVsMoonSharp = Get-GeometricMean $comparisonValues
        MinimumSpeedupVsMoonSharp = ($comparisonValues | Measure-Object -Minimum).Minimum
        MaximumSpeedupVsMoonSharp = ($comparisonValues | Measure-Object -Maximum).Maximum
    }
}
$perRid = foreach ($report in $reports | Sort-Object { $_.environment.rid }) {
    foreach ($overall in $report.overall) {
        [pscustomobject]@{
            Rid = $report.environment.rid
            Engine = $overall.engine
            Workloads = $overall.workloads
            GeometricMeanSpeedupVsNativeLua = $overall.geometricMeanSpeedupVsNativeLua
            GeometricMeanSpeedupVsMoonSharp = $overall.geometricMeanSpeedupVsComparison
        }
    }
}
$perWorkload = foreach ($workload in $reports[0].workloads) {
    foreach ($engineId in $engineIds) {
        $values = @($reports | ForEach-Object {
            $_.results | Where-Object {
                $_.workload -eq $workload.name -and $_.engine -eq $engineId
            } | ForEach-Object speedupVsNativeLua
        })
        $comparisonValues = @($reports | ForEach-Object {
            $_.results | Where-Object {
                $_.workload -eq $workload.name -and $_.engine -eq $engineId
            } | ForEach-Object speedupVsComparison
        } | Where-Object { $null -ne $_ })
        [pscustomobject]@{
            Workload = $workload.name
            Engine = $engineId
            Rids = $values.Count
            GeometricMeanSpeedupVsNativeLua = Get-GeometricMean $values
            MinimumSpeedupVsNativeLua = ($values | Measure-Object -Minimum).Minimum
            MaximumSpeedupVsNativeLua = ($values | Measure-Object -Maximum).Maximum
            GeometricMeanSpeedupVsMoonSharp = Get-GeometricMean $comparisonValues
            MinimumSpeedupVsMoonSharp = ($comparisonValues | Measure-Object -Minimum).Minimum
            MaximumSpeedupVsMoonSharp = ($comparisonValues | Measure-Object -Maximum).Maximum
        }
    }
}

$merged = [ordered]@{
    schemaVersion = 1
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    expectedRids = $ExpectedRids
    actualRids = $actualRids | Sort-Object
    baselineEngine = 'lua54'
    comparisonEngine = 'moonsharp'
    complete = $missingRids.Count -eq 0
    performanceGatePassed = $true
    engines = $aggregateEngines
    perRid = $perRid
    perWorkload = $perWorkload
}
$jsonPath = Join-Path $OutputDirectory 'cross-runtime-six-rid-report.json'
$merged | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $jsonPath -Encoding utf8

$engineNames = @{}
foreach ($engine in $reports[0].engines) { $engineNames[$engine.id] = $engine.displayName }
$lines = [Collections.Generic.List[string]]::new()
$lines.Add('# Cross-runtime Lua performance report — six RID aggregate')
$lines.Add('')
$lines.Add("Native PUC Lua is the per-RID baseline (`1.000x`). Values above `1.000x` are faster.")
$lines.Add('')
$lines.Add('## Overall across workloads and RIDs')
$lines.Add('')
$lines.Add('| Engine | Measurements | Geomean vs native Lua | Native range | Geomean vs MoonSharp |')
$lines.Add('|---|---:|---:|---:|---:|')
foreach ($engine in $aggregateEngines | Sort-Object GeometricMeanSpeedupVsNativeLua -Descending) {
    $lines.Add(('| {0} | {1} | {2:F3}x | {3:F3}x–{4:F3}x | {5:F3}x |' -f `
        $engineNames[$engine.Engine], $engine.Measurements,
        $engine.GeometricMeanSpeedupVsNativeLua,
        $engine.MinimumSpeedupVsNativeLua, $engine.MaximumSpeedupVsNativeLua,
        $engine.GeometricMeanSpeedupVsMoonSharp))
}
$lines.Add('')
$lines.Add('## Per RID geometric mean')
$lines.Add('')
$lines.Add('| RID | Engine | Workloads | Geomean vs native Lua | Geomean vs MoonSharp |')
$lines.Add('|---|---|---:|---:|---:|')
foreach ($row in $perRid) {
    $lines.Add(('| {0} | {1} | {2} | {3:F3}x | {4:F3}x |' -f `
        $row.Rid, $engineNames[$row.Engine], $row.Workloads,
        $row.GeometricMeanSpeedupVsNativeLua, $row.GeometricMeanSpeedupVsMoonSharp))
}
$lines.Add('')
$lines.Add('## Per workload across RIDs')
$lines.Add('')
$lines.Add('| Workload | Engine | RIDs | Geomean vs native Lua | Native range | Geomean vs MoonSharp |')
$lines.Add('|---|---|---:|---:|---:|---:|')
foreach ($row in $perWorkload) {
    $lines.Add(('| {0} | {1} | {2} | {3:F3}x | {4:F3}x–{5:F3}x | {6:F3}x |' -f `
        $row.Workload, $engineNames[$row.Engine], $row.Rids,
        $row.GeometricMeanSpeedupVsNativeLua,
        $row.MinimumSpeedupVsNativeLua, $row.MaximumSpeedupVsNativeLua,
        $row.GeometricMeanSpeedupVsMoonSharp))
}
$lines.Add('')
$lines.Add('Lunil Auto and Tier 2 passed the per-workload MoonSharp median/CI/route/telemetry gate on all six RIDs.')
$markdownPath = Join-Path $OutputDirectory 'cross-runtime-six-rid-report.md'
$lines | Set-Content -LiteralPath $markdownPath -Encoding utf8
Write-Host "Merged cross-runtime report written to $markdownPath"
