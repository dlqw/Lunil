[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [ValidateRange(0.0, 1.0)][double] $MinimumCoreLineRate = 0.85,
    [ValidateRange(0.0, 1.0)][double] $MinimumCoreBranchRate = 0.75,
    [ValidateRange(0.0, 1.0)][double] $MinimumAdapterLineRate = 0.80
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
$resultRoot = Join-Path $repositoryRoot "artifacts/coverage/$Version/$stamp"
$coreRoot = Join-Path $resultRoot 'core'
$adapterRoot = Join-Path $resultRoot 'adapters'
New-Item -ItemType Directory -Path $coreRoot,$adapterRoot -Force | Out-Null

function Invoke-CoverageTest(
    [string] $Project,
    [string] $ResultsDirectory,
    [bool] $IncludeTestAssembly) {
    $arguments = @(
        'test', $Project,
        '--configuration', 'Release',
        '--collect', 'XPlat Code Coverage',
        '--results-directory', $ResultsDirectory,
        '--nologo',
        '--',
        "DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.IncludeTestAssembly=$($IncludeTestAssembly.ToString().ToLowerInvariant())"
    )
    & dotnet @arguments | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "Coverage test project failed: $Project"
    }

    $reports = @(Get-ChildItem -LiteralPath $ResultsDirectory -Recurse -Filter coverage.cobertura.xml)
    if ($reports.Count -ne 1) {
        throw "Expected one Cobertura report for $Project, found $($reports.Count)."
    }

    return $reports[0].FullName
}

function Get-FileCoverage([xml] $Report, [string[]] $Suffixes) {
    $classes = @($Report.coverage.packages.package.classes.class)
    $files = [Collections.Generic.List[object]]::new()
    foreach ($suffix in $Suffixes) {
        $normalizedSuffix = $suffix.Replace('\', '/')
        $matching = @($classes | Where-Object {
            ([string]$_.filename).Replace('\', '/').EndsWith(
                $normalizedSuffix,
                [StringComparison]::OrdinalIgnoreCase)
        })
        if ($matching.Count -eq 0) {
            throw "Coverage report is missing required source file '$suffix'."
        }

        $lines = @($matching | ForEach-Object { $_.lines.line } |
            Where-Object { $null -ne $_.number } |
            Group-Object number | ForEach-Object { $_.Group | Select-Object -First 1 })
        $coveredLines = @($lines | Where-Object { [int]$_.hits -gt 0 }).Count
        $branchCount = 0
        $coveredBranches = 0
        foreach ($line in $lines | Where-Object { $_.branch -eq 'true' }) {
            if ([string]$line.'condition-coverage' -match '\((\d+)/(\d+)\)') {
                $coveredBranches += [int]$Matches[1]
                $branchCount += [int]$Matches[2]
            }
        }

        $files.Add([pscustomobject]@{
            file = $suffix
            lines = $lines.Count
            coveredLines = $coveredLines
            branches = $branchCount
            coveredBranches = $coveredBranches
        })
    }

    $lineCount = ($files.lines | Measure-Object -Sum).Sum
    $coveredLineCount = ($files.coveredLines | Measure-Object -Sum).Sum
    $branchCount = ($files.branches | Measure-Object -Sum).Sum
    $coveredBranchCount = ($files.coveredBranches | Measure-Object -Sum).Sum
    return [pscustomobject]@{
        lineRate = if ($lineCount -eq 0) { 0.0 } else { $coveredLineCount / $lineCount }
        branchRate = if ($branchCount -eq 0) { 1.0 } else { $coveredBranchCount / $branchCount }
        lines = $lineCount
        coveredLines = $coveredLineCount
        branches = $branchCount
        coveredBranches = $coveredBranchCount
        files = $files
    }
}

$coreReportPath = Invoke-CoverageTest `
    (Join-Path $repositoryRoot 'tests/Lunil.Hosting.Tests/Lunil.Hosting.Tests.csproj') `
    $coreRoot $false
$adapterReportPath = Invoke-CoverageTest `
    (Join-Path $repositoryRoot 'tests/Lunil.EngineAdapters.Tests/Lunil.EngineAdapters.Tests.csproj') `
    $adapterRoot $true

[xml]$coreReport = Get-Content -LiteralPath $coreReportPath -Raw
[xml]$adapterReport = Get-Content -LiteralPath $adapterReportPath -Raw
$core = Get-FileCoverage $coreReport @(
    'Lunil.Hosting/LuaGameLoopContracts.cs',
    'Lunil.Hosting/LuaGameLoopHost.cs'
)
$unity = Get-FileCoverage $adapterReport @(
    'integrations/unity/com.dlqw.lunil/Runtime/LuaScriptAsset.cs',
    'integrations/unity/com.dlqw.lunil/Runtime/LuaUnityServices.cs'
)
$godot = Get-FileCoverage $adapterReport @(
    'src/Lunil.Godot/LuaGodotScriptResource.cs',
    'src/Lunil.Godot/LuaGodotServices.cs'
)

if ($core.lineRate -lt $MinimumCoreLineRate) {
    throw "Engine-neutral core line coverage $($core.lineRate) is below $MinimumCoreLineRate."
}
if ($core.branchRate -lt $MinimumCoreBranchRate) {
    throw "Engine-neutral core branch coverage $($core.branchRate) is below $MinimumCoreBranchRate."
}
if ($unity.lineRate -lt $MinimumAdapterLineRate) {
    throw "Unity testable adapter line coverage $($unity.lineRate) is below $MinimumAdapterLineRate."
}
if ($godot.lineRate -lt $MinimumAdapterLineRate) {
    throw "Godot testable adapter line coverage $($godot.lineRate) is below $MinimumAdapterLineRate."
}

$result = [pscustomobject]@{
    version = $Version
    generatedAtUtc = [DateTime]::UtcNow.ToString('O', [Globalization.CultureInfo]::InvariantCulture)
    thresholds = [pscustomobject]@{
        coreLineRate = $MinimumCoreLineRate
        coreBranchRate = $MinimumCoreBranchRate
        adapterLineRate = $MinimumAdapterLineRate
    }
    core = $core
    unity = $unity
    godot = $godot
    status = 'passed'
}
$resultPath = Join-Path $resultRoot 'results.json'
[IO.File]::WriteAllText(
    $resultPath,
    ($result | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))

Write-Host ('LUNIL_RELEASE_COVERAGE_RESULT ' +
    "core_line=$($core.lineRate.ToString('F6', [Globalization.CultureInfo]::InvariantCulture)) " +
    "core_branch=$($core.branchRate.ToString('F6', [Globalization.CultureInfo]::InvariantCulture)) " +
    "unity_line=$($unity.lineRate.ToString('F6', [Globalization.CultureInfo]::InvariantCulture)) " +
    "godot_line=$($godot.lineRate.ToString('F6', [Globalization.CultureInfo]::InvariantCulture)) " +
    "result=$resultPath")
