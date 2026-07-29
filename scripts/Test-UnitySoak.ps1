[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string[]] $UnityEditorPaths,
    [ValidateRange(0.001, 24.0)][double] $DurationHours = 6.0,
    [ValidateRange(0.0, 1440.0)][double] $WarmupMinutes = 30.0,
    [ValidateRange(0.001, 1440.0)][double] $SampleMinutes = 5.0,
    [switch] $SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$durationSeconds = $DurationHours * 3600.0
$warmupSeconds = $WarmupMinutes * 60.0
$sampleSeconds = $SampleMinutes * 60.0
if ($warmupSeconds -ge $durationSeconds) { throw 'Warmup must be shorter than duration.' }
if ($sampleSeconds -gt $durationSeconds - $warmupSeconds) {
    throw 'Sample interval must fit after warmup.'
}

if (-not $SkipBuild) {
    & (Join-Path $PSScriptRoot 'Test-UnityPackage.ps1') -Version $Version `
        -UnityEditorPaths $UnityEditorPaths | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Unity soak fixture preparation failed.' }
}

$resultRoot = Join-Path $repositoryRoot "artifacts/unity/$Version/soak"
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
foreach ($editorInput in $UnityEditorPaths) {
    $editor = [IO.Path]::GetFullPath($editorInput)
    $versionLabel = ([Diagnostics.FileVersionInfo]::GetVersionInfo($editor).ProductVersion -split '_')[0]
    $projectLabel = $versionLabel -replace '[^0-9A-Za-z.-]', '-'
    $player = Join-Path $repositoryRoot `
        "artifacts/unity/$Version/verification/project-$projectLabel/Player/LunilUnityFixture.exe"
    if (-not (Test-Path -LiteralPath $player -PathType Leaf)) {
        throw "Unity $versionLabel Mono player is missing: $player"
    }
    $log = Join-Path $resultRoot "unity-$projectLabel.log"
    $arguments = @(
        '-batchmode', '-nographics', '-logFile', $log,
        '-lunilSoakSeconds', $durationSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture),
        '-lunilSoakWarmupSeconds', $warmupSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture),
        '-lunilSoakSampleSeconds', $sampleSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture))
    $quoted = @($arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    })
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $player
    $start.Arguments = $quoted -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $timeout = [TimeSpan]::FromHours($DurationHours).Add([TimeSpan]::FromMinutes(30))
        if (-not $process.WaitForExit([int]$timeout.TotalMilliseconds)) {
            try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
            throw "Unity $versionLabel soak timed out."
        }
        if ($process.ExitCode -ne 0) {
            throw "Unity $versionLabel soak exited with $($process.ExitCode).`n$((Get-Content $log -Tail 160) -join "`n")"
        }
    }
    finally {
        $process.Dispose()
    }

    $line = Select-String -LiteralPath $log -Pattern `
        'LUNIL_ENGINE_SOAK_RESULT host=unity seconds=(?<seconds>[0-9.]+) ticks=(?<ticks>\d+) managed_growth=(?<managed>[0-9.]+) logical_growth=(?<logical>[0-9.]+) object_growth=(?<objects>[0-9.]+) max_growth=(?<maximum>[0-9.]+) samples=(?<samples>\d+) active=0 pending=0' |
        Select-Object -Last 1
    if ($null -eq $line) { throw "Unity $versionLabel soak result marker is missing." }
    $match = $line.Matches[0]
    $seconds = [double]::Parse($match.Groups['seconds'].Value, [Globalization.CultureInfo]::InvariantCulture)
    $maximum = [double]::Parse($match.Groups['maximum'].Value, [Globalization.CultureInfo]::InvariantCulture)
    if ($seconds + 0.01 -lt $durationSeconds -or $maximum -gt 0.05) {
        throw "Unity $versionLabel soak did not meet duration/growth gates."
    }
    $results.Add([pscustomobject]@{
        unityVersion = $versionLabel
        durationSeconds = $seconds
        ticks = [long]$match.Groups['ticks'].Value
        maximumGrowth = $maximum
        samples = [int]$match.Groups['samples'].Value
        status = 'passed'
    })
}

$resultPath = Join-Path $resultRoot 'results.json'
[IO.File]::WriteAllText($resultPath, ($results | ConvertTo-Json -Depth 6),
    [Text.UTF8Encoding]::new($false))
$results
