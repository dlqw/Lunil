[CmdletBinding()]
param(
    [ValidateRange(1, 100)]
    [int] $Rounds = 6,
    [ValidateRange(1, 60000)]
    [double] $TargetMilliseconds = 250,
    [ValidateRange(1, 100)]
    [int] $WarmupCalls = 4,
    [string] $RuntimeIdentifier,
    [string] $ToolsDirectory,
    [string] $OutputDirectory,
    [string] $LuaPath,
    [string] $LuaJitPath,
    [string[]] $Workloads,
    [string[]] $Engines,
    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',
    [switch] $NoProvision,
    [switch] $NoBuild,
    [switch] $SkipReference,
    [switch] $Quick
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$Workloads = @($Workloads)
$Engines = @($Engines)
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $platform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $RuntimeIdentifier = "$platform-$architecture"
}
if ($Quick) {
    $Rounds = 2
    $TargetMilliseconds = 50
    $WarmupCalls = 3
}
if ([string]::IsNullOrWhiteSpace($ToolsDirectory)) {
    $ToolsDirectory = Join-Path $repositoryRoot "artifacts/cross-runtime-tools/$RuntimeIdentifier"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $timestamp = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss')
    $OutputDirectory = Join-Path $repositoryRoot `
        "artifacts/cross-runtime-performance/$RuntimeIdentifier/$timestamp"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

if ([string]::IsNullOrWhiteSpace($LuaPath) -or [string]::IsNullOrWhiteSpace($LuaJitPath)) {
    if (-not $NoProvision) {
        & (Join-Path $PSScriptRoot 'Install-CrossRuntimeBenchmarkTools.ps1') `
            -OutputDirectory $ToolsDirectory -RuntimeIdentifier $RuntimeIdentifier | Out-Host
    }
    $toolsManifestPath = Join-Path $ToolsDirectory 'tools.json'
    if (-not (Test-Path -LiteralPath $toolsManifestPath)) {
        throw "Cross-runtime tool manifest was not found: $toolsManifestPath"
    }
    $tools = Get-Content -Raw -LiteralPath $toolsManifestPath | ConvertFrom-Json
    if ([string]::IsNullOrWhiteSpace($LuaPath)) { $LuaPath = $tools.lua.executable }
    if ([string]::IsNullOrWhiteSpace($LuaJitPath)) { $LuaJitPath = $tools.luaJit.executable }
    Copy-Item -LiteralPath $toolsManifestPath -Destination (Join-Path $OutputDirectory 'tools.json')
}
if (-not (Test-Path -LiteralPath $LuaPath)) { throw "Native Lua was not found: $LuaPath" }
if (-not (Test-Path -LiteralPath $LuaJitPath)) { throw "LuaJIT was not found: $LuaJitPath" }

$project = Join-Path $repositoryRoot `
    'benchmarks/Lunil.Runtime.CrossRuntimeBenchmarks/Lunil.Runtime.CrossRuntimeBenchmarks.csproj'
Push-Location $repositoryRoot
try {
    if (-not $NoBuild) {
        & dotnet build $project --configuration $Configuration
        if ($LASTEXITCODE -ne 0) { throw 'Cross-runtime benchmark build failed.' }
    }

    & dotnet run --project $project --configuration $Configuration --no-build -- --self-test
    if ($LASTEXITCODE -ne 0) { throw 'Cross-runtime report self-test failed.' }

    $commit = (& git rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to read the Git commit.' }
    $runnerArguments = @(
        'run', '--project', $project, '--configuration', $Configuration, '--no-build', '--',
        "--lua=$([IO.Path]::GetFullPath($LuaPath))",
        "--luajit=$([IO.Path]::GetFullPath($LuaJitPath))",
        "--suite-root=$([IO.Path]::GetFullPath((Join-Path $repositoryRoot 'benchmarks/cross-runtime')))",
        "--output=$OutputDirectory",
        "--rid=$RuntimeIdentifier",
        "--commit=$commit",
        "--rounds=$Rounds",
        "--target-ms=$TargetMilliseconds",
        "--warmup-calls=$WarmupCalls"
    )
    if ($Workloads.Count -gt 0) {
        $runnerArguments += "--workloads=$($Workloads -join ',')"
    }
    if ($Engines.Count -gt 0) {
        $runnerArguments += "--engines=$($Engines -join ',')"
    }
    if ($SkipReference) {
        $runnerArguments += '--skip-reference'
    }

    & dotnet @runnerArguments 2>&1 | Tee-Object -FilePath (Join-Path $OutputDirectory 'runner.log')
    if ($LASTEXITCODE -ne 0) { throw 'Cross-runtime performance runner failed.' }
} finally {
    Pop-Location
}

$reportPath = Join-Path $OutputDirectory 'report.json'
if (-not (Test-Path -LiteralPath $reportPath)) {
    throw "Cross-runtime report was not produced: $reportPath"
}
$report = Get-Content -Raw -LiteralPath $reportPath | ConvertFrom-Json
if ($Workloads.Count -eq 0 -and $Engines.Count -eq 0 -and -not $report.completeness.complete) {
    throw 'The full cross-runtime performance report is incomplete.'
}
Write-Host "Cross-runtime performance evidence written to $OutputDirectory"
Get-Content -Raw -LiteralPath (Join-Path $OutputDirectory 'report.md')
