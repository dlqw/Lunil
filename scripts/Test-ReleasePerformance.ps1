[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(?:-[0-9A-Za-z.-]+)?$')]
    [string] $Version,

    [ValidatePattern('^v\d+\.\d+\.\d+$')]
    [string] $BaselineTag = 'v0.12.1',

    [ValidateRange(2, 20)]
    [int] $Rounds = 6,

    [ValidateRange(1, 1000000)]
    [int] $Operations = 24,

    [ValidateRange(1, 1000000)]
    [int] $Warmup = 160,

    [ValidateRange(3, 21)]
    [int] $Samples = 5,

    [string] $BaselineRoot = ''
)

$ErrorActionPreference = 'Stop'
if (($Rounds % 2) -ne 0) {
    throw 'Rounds must be even so process and workload order can be balanced.'
}
if (($Samples % 2) -ne 1) {
    throw 'Samples must be odd so every process has an exact median.'
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot `
    'benchmarks/Lunil.ReleaseGate.Benchmarks/Lunil.ReleaseGate.Benchmarks.csproj'
$stamp = [DateTime]::UtcNow.ToString(
    'yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
$outputDirectory = Join-Path $repositoryRoot "artifacts/release-performance/$Version/$stamp"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

function Invoke-Git([string[]] $Arguments) {
    $output = & git -C $repositoryRoot @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "git $($Arguments -join ' ') failed."
    }
    return $output
}

function Get-Sha256Hex([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($stream)
        return ([BitConverter]::ToString($hash) -replace '-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

$baselineCommit = (Invoke-Git @('rev-parse', "$BaselineTag^{}") | Select-Object -First 1).Trim()
if ([string]::IsNullOrWhiteSpace($BaselineRoot)) {
    $safeTag = $BaselineTag -replace '[^0-9A-Za-z.-]', '-'
    $BaselineRoot = Join-Path ([IO.Path]::GetTempPath()) `
        "Lunil-release-performance-baselines/$safeTag"
}
$BaselineRoot = [IO.Path]::GetFullPath($BaselineRoot)
if (-not (Test-Path -LiteralPath $BaselineRoot)) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $BaselineRoot) -Force | Out-Null
    Invoke-Git @('worktree', 'add', '--detach', $BaselineRoot, $baselineCommit) | Out-Null
}
$actualBaselineCommit = (& git -C $BaselineRoot rev-parse HEAD)
if ($LASTEXITCODE -ne 0 -or $actualBaselineCommit.Trim() -ne $baselineCommit) {
    throw "Baseline root must be a worktree at $BaselineTag ($baselineCommit)."
}

$baselineFixture = Join-Path $outputDirectory 'baseline-fixture'
New-Item -ItemType Directory -Path $baselineFixture -Force | Out-Null
Copy-Item -LiteralPath $project -Destination (Join-Path $baselineFixture `
    'Lunil.ReleaseGate.Benchmarks.csproj')
Copy-Item -LiteralPath (Join-Path (Split-Path -Parent $project) 'Program.cs') `
    -Destination (Join-Path $baselineFixture 'Program.cs')
$currentProgramHash = Get-Sha256Hex `
    (Join-Path (Split-Path -Parent $project) 'Program.cs')
$baselineProgramHash = Get-Sha256Hex (Join-Path $baselineFixture 'Program.cs')
if ($currentProgramHash -ne $baselineProgramHash) {
    throw 'The current and published-baseline runners do not use identical source.'
}

Push-Location $repositoryRoot
try {
    & dotnet build $project --configuration Release
    if ($LASTEXITCODE -ne 0) { throw 'Building the current multi-target performance fixture failed.' }

    $baselineProject = Join-Path $baselineFixture 'Lunil.ReleaseGate.Benchmarks.csproj'
    & dotnet build $baselineProject --configuration Release --framework net10.0 `
        "-p:LunilReleaseBaseline=true" "-p:LunilSourceRoot=$(Join-Path $BaselineRoot 'src')"
    if ($LASTEXITCODE -ne 0) { throw 'Building the published-baseline performance fixture failed.' }
}
finally {
    Pop-Location
}

$currentNet8 = Join-Path (Split-Path -Parent $project) `
    'bin/Release/net8.0/Lunil.ReleaseGate.Benchmarks.dll'
$currentNet10 = Join-Path (Split-Path -Parent $project) `
    'bin/Release/net10.0/Lunil.ReleaseGate.Benchmarks.dll'
$baselineNet10 = Join-Path $baselineFixture `
    'bin/Release/net10.0/Lunil.ReleaseGate.Benchmarks.dll'

# Run both current assets and the published baseline on the same CLR.  The portable
# runner targets net8.0 only so that it resolves the netstandard2.1 Lunil assets; if
# it were launched normally, the host would also select the .NET 8 CLR and the gate
# would mix target-asset overhead with two generations of JIT/runtime changes.
$commonRuntime = & dotnet --list-runtimes | ForEach-Object {
    if ($_ -match '^Microsoft\.NETCore\.App\s+(\d+\.\d+\.\d+)\s+\[') {
        $candidate = [Version]$Matches[1]
        if ($candidate.Major -eq 10) { $candidate }
    }
} | Sort-Object -Descending | Select-Object -First 1
if ($null -eq $commonRuntime) {
    throw 'The release performance gate requires an installed .NET 10 runtime.'
}
$commonRuntimeText = $commonRuntime.ToString(3)

$records = [Collections.Generic.List[object]]::new()
$environments = [Collections.Generic.List[object]]::new()

function ConvertTo-KeyValues([string] $Payload) {
    $values = @{}
    foreach ($part in $Payload.Split(',')) {
        $pair = $part.Trim().Split('=', 2)
        if ($pair.Count -ne 2) { throw "Malformed performance field: $part" }
        $values[$pair[0]] = $pair[1]
    }
    return $values
}

function Invoke-Runner(
    [string] $Assembly,
    [string] $Kind,
    [string] $Mode,
    [int] $Round,
    [bool] $Reverse) {
    $arguments = @(
        $Assembly,
        "--mode=$Mode",
        "--operations=$Operations",
        "--warmup=$Warmup",
        "--samples=$Samples"
    )
    if ($Reverse) { $arguments += '--reverse-order' }
    $lines = & dotnet exec --fx-version $commonRuntimeText @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Kind performance runner failed in round $Round."
    }
    $lines | Set-Content -LiteralPath `
        (Join-Path $outputDirectory ("{0}-round-{1:D2}.txt" -f $Kind, $Round)) `
        -Encoding utf8
    foreach ($line in $lines) {
        if ($line -like 'release_perf_environment *') {
            $values = ConvertTo-KeyValues $line.Substring('release_perf_environment '.Length)
            $environments.Add([pscustomobject]@{
                Kind = $Kind
                Round = $Round
                Mode = $values.mode
                Framework = $values.framework
                Runtime = $values.runtime
            })
        }
        elseif ($line -like 'release_perf *') {
            $values = ConvertTo-KeyValues $line.Substring('release_perf '.Length)
            $records.Add([pscustomobject]@{
                Kind = $Kind
                Round = $Round
                Workload = $values.workload
                Nanoseconds = [double]::Parse(
                    $values.ns_op, [Globalization.CultureInfo]::InvariantCulture)
                Checksum = [long]$values.checksum
                SelectedBackend = $values.selected_backend
            })
        }
        elseif ($line -like 'adapter_perf *') {
            $values = ConvertTo-KeyValues $line.Substring('adapter_perf '.Length)
            $records.Add([pscustomobject]@{
                Kind = 'adapter-' + $values.name
                Round = $Round
                Workload = 'frame-scheduling'
                Nanoseconds = [double]::Parse(
                    $values.ns_frame, [Globalization.CultureInfo]::InvariantCulture)
                Checksum = [long]$values.checksum
                SelectedBackend = 'none'
            })
        }
    }
}

for ($round = 1; $round -le $Rounds; $round++) {
    $reverse = ($round % 2) -eq 0
    Write-Host "Release performance round $round/$Rounds"
    if ($reverse) {
        Invoke-Runner $currentNet10 'net10-interpreter' 'interpreter' $round $true
        Invoke-Runner $currentNet8 'portable-interpreter' 'interpreter' $round $true
        Invoke-Runner $currentNet10 'current-auto' 'auto' $round $true
        Invoke-Runner $baselineNet10 'baseline-auto' 'auto' $round $true
    }
    else {
        Invoke-Runner $currentNet8 'portable-interpreter' 'interpreter' $round $false
        Invoke-Runner $currentNet10 'net10-interpreter' 'interpreter' $round $false
        Invoke-Runner $baselineNet10 'baseline-auto' 'auto' $round $false
        Invoke-Runner $currentNet10 'current-auto' 'auto' $round $false
    }
    Invoke-Runner $currentNet10 'adapters' 'adapter' $round $reverse
}

$expectedWorkloads = 4
foreach ($kind in @(
    'portable-interpreter', 'net10-interpreter', 'baseline-auto', 'current-auto')) {
    $count = @($records | Where-Object Kind -eq $kind).Count
    if ($count -ne $Rounds * $expectedWorkloads) {
        throw "Expected $($Rounds * $expectedWorkloads) $kind records, found $count."
    }
}
foreach ($kind in @('adapter-neutral', 'adapter-unity', 'adapter-godot')) {
    $count = @($records | Where-Object Kind -eq $kind).Count
    if ($count -ne $Rounds) {
        throw "Expected $Rounds $kind records, found $count."
    }
}

foreach ($kind in @('portable-interpreter', 'net10-interpreter')) {
    $unexpected = @($records | Where-Object {
        $_.Kind -eq $kind -and $_.SelectedBackend -ne 'interpreter'
    })
    if ($unexpected.Count -ne 0) {
        throw "$kind did not consistently select the interpreter backend."
    }
}
foreach ($kind in @('baseline-auto', 'current-auto')) {
    $unexpected = @($records | Where-Object {
        $_.Kind -eq $kind -and $_.SelectedBackend -ne 'jit'
    })
    if ($unexpected.Count -ne 0) {
        throw "$kind did not consistently select the JIT backend."
    }
}

$portableFrameworks = @($environments | Where-Object Kind -eq 'portable-interpreter' |
    Select-Object -ExpandProperty Framework -Unique)
$net10Frameworks = @($environments | Where-Object Kind -eq 'net10-interpreter' |
    Select-Object -ExpandProperty Framework -Unique)
if ($portableFrameworks.Count -ne 1 -or
    $portableFrameworks[0] -ne '.NETStandard;Version=v2.1') {
    throw "Portable measurements did not consume the netstandard2.1 asset: $portableFrameworks"
}
if ($net10Frameworks.Count -ne 1 -or
    $net10Frameworks[0] -ne '.NETCoreApp;Version=v10.0') {
    throw "net10 measurements did not consume the net10.0 asset: $net10Frameworks"
}
$measuredRuntimes = @($environments | Select-Object -ExpandProperty Runtime -Unique)
if ($measuredRuntimes.Count -ne 1 -or $measuredRuntimes[0] -ne $commonRuntimeText) {
    throw "Performance measurements did not use the common CLR $commonRuntimeText`: $measuredRuntimes"
}

function Get-GeometricMean([double[]] $Values) {
    if ($Values.Count -eq 0 -or $Values.Where({ $_ -le 0 }).Count -ne 0) {
        throw 'Geometric means require at least one finite positive ratio.'
    }
    $sum = 0.0
    foreach ($value in $Values) { $sum += [Math]::Log($value) }
    return [Math]::Exp($sum / $Values.Count)
}

function Get-PairedRatios([string] $CandidateKind, [string] $ReferenceKind) {
    $ratios = [Collections.Generic.List[double]]::new()
    foreach ($candidate in $records | Where-Object Kind -eq $CandidateKind) {
        $reference = $records | Where-Object {
            $_.Kind -eq $ReferenceKind -and
            $_.Round -eq $candidate.Round -and
            $_.Workload -eq $candidate.Workload
        } | Select-Object -First 1
        if ($null -eq $reference) {
            throw "Missing $ReferenceKind pair for $CandidateKind round $($candidate.Round)."
        }
        if ($CandidateKind -notlike 'adapter-*' -and
            $candidate.Checksum -ne $reference.Checksum) {
            throw "Checksum mismatch for $($candidate.Workload) in round $($candidate.Round)."
        }
        $ratios.Add($candidate.Nanoseconds / $reference.Nanoseconds)
    }
    return $ratios.ToArray()
}

$portableRatio = Get-GeometricMean (Get-PairedRatios `
    'portable-interpreter' 'net10-interpreter')
$autoRatio = Get-GeometricMean (Get-PairedRatios 'current-auto' 'baseline-auto')
$unityRatio = Get-GeometricMean (Get-PairedRatios 'adapter-unity' 'adapter-neutral')
$godotRatio = Get-GeometricMean (Get-PairedRatios 'adapter-godot' 'adapter-neutral')

$result = [ordered]@{
    schemaVersion = 1
    version = $Version
    baselineTag = $BaselineTag
    baselineCommit = $baselineCommit
    runtime = $commonRuntimeText
    rounds = $Rounds
    operations = $Operations
    warmup = $Warmup
    samples = $Samples
    portableInterpreterRatioVsNet10 = $portableRatio
    portableInterpreterMaximumRatio = 1.15
    net10AutoRatioVsPublishedBaseline = $autoRatio
    net10AutoMaximumRatio = 1.05
    unityAdapterRatioVsNeutral = $unityRatio
    godotAdapterRatioVsNeutral = $godotRatio
    adapterMaximumRatio = 1.10
    passed = $portableRatio -le 1.15 -and $autoRatio -le 1.05 -and `
        $unityRatio -le 1.10 -and $godotRatio -le 1.10
}
$records | Export-Csv -LiteralPath (Join-Path $outputDirectory 'runs.csv') `
    -NoTypeInformation -Encoding utf8
$result | ConvertTo-Json | Set-Content -LiteralPath `
    (Join-Path $outputDirectory 'results.json') -Encoding utf8

Write-Host (
    'LUNIL_RELEASE_PERFORMANCE_RESULT ' +
    "portable_ratio=$($portableRatio.ToString('F6', [Globalization.CultureInfo]::InvariantCulture)) " +
    "auto_ratio=$($autoRatio.ToString('F6', [Globalization.CultureInfo]::InvariantCulture)) " +
    "unity_ratio=$($unityRatio.ToString('F6', [Globalization.CultureInfo]::InvariantCulture)) " +
    "godot_ratio=$($godotRatio.ToString('F6', [Globalization.CultureInfo]::InvariantCulture)) " +
    "result=$(Join-Path $outputDirectory 'results.json')")
if (-not $result.passed) {
    throw 'One or more release performance regression gates did not pass.'
}
