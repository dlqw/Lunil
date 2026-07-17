[CmdletBinding()]
param(
    [string] $Configuration = 'Release',

    [string] $RuntimeIdentifier = '',

    [switch] $NoBuild,

    [switch] $NoRestore
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$version = (& (Join-Path $PSScriptRoot 'Get-LunilVersion.ps1')).Trim()
if ([string]::IsNullOrWhiteSpace($version)) {
    throw 'Could not resolve the active Lunil release version.'
}
$releaseGate = "$version-conformance"
$detectedRid = [System.Runtime.InteropServices.RuntimeInformation]::RuntimeIdentifier.Trim().ToLowerInvariant()
$effectiveRid = if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $detectedRid
}
else {
    $RuntimeIdentifier.Trim().ToLowerInvariant()
}
if ($effectiveRid -ne $detectedRid) {
    throw "Requested evidence RID $effectiveRid does not match the executing runtime RID $detectedRid."
}

$stamp = [DateTime]::UtcNow.ToString(
    'yyyyMMdd-HHmmss',
    [Globalization.CultureInfo]::InvariantCulture)
$outputDirectory = Join-Path $repositoryRoot "artifacts/conformance/$effectiveRid/$stamp"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

$testProjects = @(
    [pscustomobject]@{
        Name = 'official-lua-5.4.8-user-mode'
        Project = 'tests/Lunil.Conformance.Tests/Lunil.Conformance.Tests.csproj'
        ExpectedTests = 2
    },
    [pscustomobject]@{
        Name = 'five-backend-differential'
        Project = 'tests/Lunil.BackendDifferential.Tests/Lunil.BackendDifferential.Tests.csproj'
        ExpectedTests = 16
    },
    [pscustomobject]@{
        Name = 'deterministic-fuzz-and-soak'
        Project = 'tests/Lunil.Stability.Tests/Lunil.Stability.Tests.csproj'
        ExpectedTests = 5
    }
)

function Get-TrxCounters([string] $Path) {
    [xml] $document = Get-Content -LiteralPath $Path -Raw
    $counters = $document.TestRun.ResultSummary.Counters
    if ($null -eq $counters) {
        throw "TRX result does not contain counters: $Path"
    }

    return [pscustomobject]@{
        Total = [int] $counters.total
        Executed = [int] $counters.executed
        Passed = [int] $counters.passed
        Failed = [int] $counters.failed
        Error = [int] $counters.error
        Timeout = [int] $counters.timeout
        Aborted = [int] $counters.aborted
        Inconclusive = [int] $counters.inconclusive
        NotExecuted = [int] $counters.notExecuted
    }
}

$testRuns = [Collections.Generic.List[object]]::new()
Push-Location $repositoryRoot
try {
    foreach ($testProject in $testProjects) {
        $trxName = "$($testProject.Name).trx"
        $arguments = @(
            'test', $testProject.Project,
            '--configuration', $Configuration,
            '--nologo',
            '--logger', "trx;LogFileName=$trxName",
            '--results-directory', $outputDirectory
        )
        if ($NoBuild) {
            $arguments += '--no-build'
        }
        if ($NoRestore) {
            $arguments += '--no-restore'
        }

        $stopwatch = [Diagnostics.Stopwatch]::StartNew()
        & dotnet @arguments
        $exitCode = $LASTEXITCODE
        $stopwatch.Stop()
        if ($exitCode -ne 0) {
            throw "Conformance evidence test '$($testProject.Name)' failed with exit code $exitCode."
        }

        $trxPath = Join-Path $outputDirectory $trxName
        $counters = Get-TrxCounters $trxPath
        if ($counters.Total -ne $testProject.ExpectedTests -or
            $counters.Executed -ne $testProject.ExpectedTests -or
            $counters.Passed -ne $testProject.ExpectedTests -or
            $counters.Failed -ne 0 -or
            $counters.Error -ne 0 -or
            $counters.Timeout -ne 0 -or
            $counters.Aborted -ne 0 -or
            $counters.Inconclusive -ne 0 -or
            $counters.NotExecuted -ne 0) {
            throw "Unexpected TRX counters for '$($testProject.Name)': $($counters | ConvertTo-Json -Compress)"
        }

        $testRuns.Add([pscustomobject]@{
            Name = $testProject.Name
            Project = $testProject.Project
            ExpectedTests = $testProject.ExpectedTests
            Total = $counters.Total
            Executed = $counters.Executed
            Passed = $counters.Passed
            Failed = $counters.Failed
            DurationMilliseconds = $stopwatch.ElapsedMilliseconds
            TrxSha256 = (Get-FileHash -LiteralPath $trxPath -Algorithm SHA256).Hash
        })
    }
}
finally {
    Pop-Location
}

$manifestPath = Join-Path $repositoryRoot `
    'tests/Lunil.Conformance.Tests/Fixtures/lua-5.4.8-manifest.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$goldenPath = Join-Path $repositoryRoot `
    'tests/Lunil.BackendDifferential.Tests/PucLua54/goldens.json'
$golden = Get-Content -LiteralPath $goldenPath -Raw | ConvertFrom-Json
$gitCommit = (& git -C $repositoryRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $gitCommit -notmatch '^[0-9a-f]{40}$') {
    throw 'Could not resolve the evidence Git commit.'
}

$backendNames = @(
    'interpreter',
    'executor-auto',
    'coreclr-tier1-jit',
    'coreclr-tier2-jit',
    'experimental-loop-osr'
)
$evidence = [ordered]@{
    SchemaVersion = 1
    ReleaseGate = $releaseGate
    GeneratedAtUtc = [DateTime]::UtcNow.ToString('O')
    GitCommit = $gitCommit
    Rid = $effectiveRid
    Runtime = [ordered]@{
        FrameworkDescription = [System.Runtime.InteropServices.RuntimeInformation]::FrameworkDescription
        OsDescription = [System.Runtime.InteropServices.RuntimeInformation]::OSDescription
        OsArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString()
        ProcessArchitecture = [System.Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    }
    OfficialSuite = [ordered]@{
        Version = $manifest.upstreamVersion
        Mode = $manifest.mode
        ArchiveSha256 = $manifest.archiveSha256
        ManifestSha256 = (Get-FileHash -LiteralPath $manifestPath -Algorithm SHA256).Hash
        FixtureFileCount = @($manifest.files).Count
        ExecutedUserModeFileCount = @($manifest.files | Where-Object {
            $_.classification -eq 'executed-user-mode'
        }).Count
        FinalMarker = 'final OK !!!'
    }
    ObservableGoldens = [ordered]@{
        OracleImplementation = $golden.oracle.implementation
        OracleVersion = $golden.oracle.version
        LuaVersionGlobal = $golden.oracle.luaVersionGlobal
        OracleExecutableSha256 = $golden.oracle.executableSha256
        SourceArchiveSha256 = $golden.oracle.sourceArchiveSha256
        DocumentSha256 = (Get-FileHash -LiteralPath $goldenPath -Algorithm SHA256).Hash
        CaseCount = @($golden.cases).Count
        Backends = $backendNames
    }
    Stability = [ordered]@{
        Syntax = [ordered]@{ Seed = '0000000054070001'; Cases = 512 }
        Chunk = [ordered]@{ Seed = '0000000054070002'; Cases = 384 }
        Annotation = [ordered]@{ Seed = '0000000054070003'; Cases = 512 }
        TypeAnalysis = [ordered]@{ Seed = '0000000054070004'; Cases = 256 }
        GcCoroutine = [ordered]@{
            Seed = '0054F00D'
            Passes = 2
            Workers = 16
            Rounds = 32
            InstructionBudget = 5000000
        }
    }
    TestRuns = $testRuns.ToArray()
    AllPassed = $true
}

$evidencePath = Join-Path $outputDirectory 'evidence.json'
$evidence | ConvertTo-Json -Depth 10 |
    Set-Content -LiteralPath $evidencePath -Encoding utf8
$evidence | ConvertTo-Json -Depth 5
Write-Host "Conformance evidence written to $evidencePath"
