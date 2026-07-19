[CmdletBinding()]
param(
    [string] $InputDirectory,
    [string] $OutputDirectory,
    [string] $TargetPolicyPath,
    [switch] $EnforceRoadmapTargets,
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
if ([string]::IsNullOrWhiteSpace($TargetPolicyPath)) {
    $TargetPolicyPath = Join-Path $repositoryRoot 'benchmarks/cross-runtime/targets/0.9.0.json'
}
$TargetPolicyPath = [IO.Path]::GetFullPath($TargetPolicyPath)
if (-not (Test-Path -LiteralPath $TargetPolicyPath)) {
    throw "Cross-runtime target policy was not found: $TargetPolicyPath"
}
$targetPolicy = Get-Content -Raw -LiteralPath $TargetPolicyPath | ConvertFrom-Json
if ($targetPolicy.schemaVersion -ne 1 -or
    [string]::IsNullOrWhiteSpace($targetPolicy.release) -or
    $null -eq $targetPolicy.crossRuntime) {
    throw "Cross-runtime target policy is invalid: $TargetPolicyPath"
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
    if ($report.schemaVersion -notin @(2, 3)) {
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

$policyRids = @($targetPolicy.requiredRids)
if (@(Compare-Object $ExpectedRids $policyRids).Count -ne 0) {
    throw "Expected RIDs do not match target policy $($targetPolicy.release)."
}
$releaseWorkloadNames = @($reports[0].workloads | Where-Object {
    $role = $_.PSObject.Properties['role']
    $null -eq $role -or [string]::Equals(
        [string]$role.Value,
        'Release',
        [StringComparison]::OrdinalIgnoreCase)
} | ForEach-Object name)
if ($releaseWorkloadNames.Count -eq 0) {
    throw 'No release workloads were found in the cross-runtime reports.'
}
$targetWorkloadNames = @(
    $targetPolicy.crossRuntime.workloadMinimumSpeedupVsNativeLua.PSObject.Properties.Name)
if (@(Compare-Object $releaseWorkloadNames $targetWorkloadNames).Count -ne 0) {
    throw "Release workloads do not match target policy $($targetPolicy.release)."
}

function Get-GeometricMean([double[]] $Values) {
    if ($Values.Count -eq 0) { return 0.0 }
    return [Math]::Exp(($Values | ForEach-Object { [Math]::Log($_) } | Measure-Object -Average).Average)
}

$engineIds = @($reports[0].engines | ForEach-Object id)
$aggregateEngines = foreach ($engineId in $engineIds) {
    $values = @($reports | ForEach-Object {
        $_.results | Where-Object {
            $_.engine -eq $engineId -and $_.workload -in $releaseWorkloadNames
        } | ForEach-Object speedupVsNativeLua
    })
    $comparisonValues = @($reports | ForEach-Object {
        $_.results | Where-Object {
            $_.engine -eq $engineId -and $_.workload -in $releaseWorkloadNames
        } | ForEach-Object speedupVsComparison
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
$perWorkload = foreach ($workload in $reports[0].workloads | Where-Object {
    $_.name -in $releaseWorkloadNames
}) {
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

$autoEngineId = [string]$targetPolicy.crossRuntime.autoEngine
$autoAggregate = $aggregateEngines | Where-Object Engine -eq $autoEngineId |
    Select-Object -First 1
$autoPerRid = @($perRid | Where-Object Engine -eq $autoEngineId)
$targetMeasurements = [Collections.Generic.List[object]]::new()
function Add-MinimumTargetMeasurement(
    [string] $Name,
    [string] $Scope,
    [Nullable[double]] $Actual,
    [double] $RequiredMinimum) {
    $present = $null -ne $Actual -and [double]::IsFinite([double]$Actual)
    $targetMeasurements.Add([pscustomobject]@{
        Name = $Name
        Scope = $Scope
        Actual = if ($present) { [double]$Actual } else { $null }
        RequiredMinimum = $RequiredMinimum
        Passed = $present -and [double]$Actual -ge $RequiredMinimum
    })
}

Add-MinimumTargetMeasurement `
    'auto-geomean-vs-native-lua' `
    'all release workloads and RIDs' `
    $autoAggregate.GeometricMeanSpeedupVsNativeLua `
    ([double]$targetPolicy.crossRuntime.minimumGeometricMeanSpeedupVsNativeLua)
Add-MinimumTargetMeasurement `
    'auto-geomean-vs-moonsharp' `
    'all release workloads and RIDs' `
    $autoAggregate.GeometricMeanSpeedupVsMoonSharp `
    ([double]$targetPolicy.crossRuntime.minimumGeometricMeanSpeedupVsMoonSharp)
$minimumPerRidAuto = if ($autoPerRid.Count -eq $ExpectedRids.Count) {
    ($autoPerRid | Measure-Object GeometricMeanSpeedupVsNativeLua -Minimum).Minimum
}
else {
    $null
}
Add-MinimumTargetMeasurement `
    'auto-minimum-per-rid-geomean-vs-native-lua' `
    'release workloads' `
    $minimumPerRidAuto `
    ([double]$targetPolicy.crossRuntime.minimumPerRidGeometricMeanSpeedupVsNativeLua)
foreach ($property in $targetPolicy.crossRuntime.workloadMinimumSpeedupVsNativeLua.PSObject.Properties) {
    $row = $perWorkload | Where-Object {
        $_.Workload -eq $property.Name -and $_.Engine -eq $autoEngineId
    } | Select-Object -First 1
    Add-MinimumTargetMeasurement `
        "auto-$($property.Name)-vs-native-lua" `
        'six-RID workload geomean' `
        $row.GeometricMeanSpeedupVsNativeLua `
        ([double]$property.Value)
}
$roadmapTargetGatePassed = $targetMeasurements.Count -gt 0 -and
    @($targetMeasurements | Where-Object { -not $_.Passed }).Count -eq 0

$merged = [ordered]@{
    schemaVersion = 2
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    expectedRids = $ExpectedRids
    actualRids = $actualRids | Sort-Object
    baselineEngine = 'lua54'
    comparisonEngine = 'moonsharp'
    complete = $missingRids.Count -eq 0
    stabilityGatePassed = $true
    roadmapTargetGate = [ordered]@{
        policyRelease = [string]$targetPolicy.release
        policyPath = [IO.Path]::GetRelativePath($repositoryRoot, $TargetPolicyPath).Replace('\', '/')
        passed = $roadmapTargetGatePassed
        measurements = $targetMeasurements.ToArray()
    }
    performanceGatePassed = $roadmapTargetGatePassed
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
$lines.Add('')
$lines.Add("## $($targetPolicy.release) roadmap target gate")
$lines.Add('')
$lines.Add('| Target | Scope | Actual | Required | Result |')
$lines.Add('|---|---|---:|---:|---|')
foreach ($measurement in $targetMeasurements) {
    $actual = if ($null -eq $measurement.Actual) { 'missing' } else { '{0:F3}x' -f $measurement.Actual }
    $lines.Add(('| {0} | {1} | {2} | ≥ {3:F3}x | {4} |' -f `
        $measurement.Name, $measurement.Scope, $actual,
        $measurement.RequiredMinimum, $(if ($measurement.Passed) { 'PASS' } else { 'FAIL' })))
}
$lines.Add('')
$lines.Add("Roadmap target gate: **$(if ($roadmapTargetGatePassed) { 'PASS' } else { 'FAIL' })**.")
$markdownPath = Join-Path $OutputDirectory 'cross-runtime-six-rid-report.md'
$lines | Set-Content -LiteralPath $markdownPath -Encoding utf8
Write-Host "Merged cross-runtime report written to $markdownPath"
if ($EnforceRoadmapTargets -and -not $roadmapTargetGatePassed) {
    $failures = @($targetMeasurements | Where-Object { -not $_.Passed } |
        ForEach-Object { "$($_.Name): actual=$($_.Actual), required>=$($_.RequiredMinimum)" })
    throw "$($targetPolicy.release) roadmap target gate failed: $($failures -join '; ')."
}
