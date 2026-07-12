[CmdletBinding()]
param(
    [ValidateSet(
        'arithmetic',
        'control_flow',
        'lua_calls',
        'table_access',
        'metamethod',
        'coroutine_error_hook')]
    [string] $Workload = 'arithmetic',

    [ValidateRange(1, 1000000000)]
    [int] $Iterations = 100000,

    [ValidateRange(1, 101)]
    [int] $ColdSamples = 1,

    [string] $Configuration = 'Release',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot `
    'benchmarks/Lunil.Runtime.Benchmarks/Lunil.Runtime.Benchmarks.csproj'
$benchmarkDll = Join-Path $repositoryRoot `
    "benchmarks/Lunil.Runtime.Benchmarks/bin/$Configuration/net10.0/Lunil.Runtime.Benchmarks.dll"
$stamp = [DateTime]::UtcNow.ToString(
    'yyyyMMdd-HHmmss',
    [Globalization.CultureInfo]::InvariantCulture)
$outputDirectory = Join-Path $repositoryRoot "artifacts/tier1-traces/$stamp"
$trace = Join-Path $outputDirectory "tier1-$Workload.nettrace"
$top = Join-Path $outputDirectory 'top-inclusive.txt'

if (-not (Get-Command dotnet-trace -ErrorAction SilentlyContinue)) {
    throw 'dotnet-trace is required. Install it with: dotnet tool install --global dotnet-trace'
}

New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null
Push-Location $repositoryRoot
try {
    if (-not $NoBuild) {
        & dotnet build $project --configuration $Configuration --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw 'Tier 1 trace benchmark build failed.'
        }
    }

    if (-not (Test-Path -LiteralPath $benchmarkDll -PathType Leaf)) {
        throw "Benchmark assembly does not exist: $benchmarkDll"
    }

    & dotnet-trace collect `
        --profile gc-verbose `
        --output $trace `
        -- dotnet $benchmarkDll `
        --backend-only `
        --backend=tier1 `
        --workload=$Workload `
        --cold-samples=$ColdSamples `
        $Iterations
    if ($LASTEXITCODE -ne 0) {
        throw 'Tier 1 allocation trace collection failed.'
    }

    & dotnet-trace report $trace topN --inclusive --number 100 |
        Set-Content -LiteralPath $top -Encoding utf8
    if ($LASTEXITCODE -ne 0) {
        throw 'Tier 1 trace report generation failed.'
    }
}
finally {
    Pop-Location
}

Write-Host "Tier 1 allocation trace written to $trace"
Write-Host "Inclusive stack report written to $top"
