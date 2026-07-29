[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string[]] $GodotEditorPaths,
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
    & (Join-Path $PSScriptRoot 'Test-GodotPackage.ps1') -Version $Version `
        -GodotEditorPaths $GodotEditorPaths | Out-Host
    if ($LASTEXITCODE -ne 0) { throw 'Godot soak fixture preparation failed.' }
}

$resultRoot = Join-Path $repositoryRoot "artifacts/godot/$Version/soak"
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()
foreach ($editorInput in $GodotEditorPaths) {
    $editor = [IO.Path]::GetFullPath($editorInput)
    $versionOutput = & $editor --version
    if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch '^(4\.(?:4|6)\.\d+)') {
        throw "Could not determine Godot version from $editor"
    }
    $versionLabel = $Matches[1]
    $project = Join-Path $repositoryRoot "artifacts/godot/$Version/verification/project-$versionLabel"
    if (-not (Test-Path -LiteralPath $project -PathType Container)) {
        throw "Godot $versionLabel soak project is missing: $project"
    }
    $log = Join-Path $resultRoot "godot-$versionLabel.log"
    $arguments = @(
        '--headless', '--path', $project, '--log-file', $log, '--',
        "--lunil-soak-seconds=$($durationSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture))",
        "--lunil-soak-warmup-seconds=$($warmupSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture))",
        "--lunil-soak-sample-seconds=$($sampleSeconds.ToString('R', [Globalization.CultureInfo]::InvariantCulture))")
    $quoted = @($arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    })
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $editor
    $start.Arguments = $quoted -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $timeout = [TimeSpan]::FromHours($DurationHours).Add([TimeSpan]::FromMinutes(30))
        if (-not $process.WaitForExit([int]$timeout.TotalMilliseconds)) {
            try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
            throw "Godot $versionLabel soak timed out."
        }
        if ($process.ExitCode -ne 0) {
            throw "Godot $versionLabel soak exited with $($process.ExitCode).`n$((Get-Content $log -Tail 160) -join "`n")"
        }
    }
    finally {
        $process.Dispose()
    }

    $line = Select-String -LiteralPath $log -Pattern `
        'LUNIL_ENGINE_SOAK_RESULT host=godot seconds=(?<seconds>[0-9.]+) ticks=(?<ticks>\d+) managed_growth=(?<managed>[0-9.]+) logical_growth=(?<logical>[0-9.]+) object_growth=(?<objects>[0-9.]+) max_growth=(?<maximum>[0-9.]+) samples=(?<samples>\d+) active=0 pending=0' |
        Select-Object -Last 1
    if ($null -eq $line) { throw "Godot $versionLabel soak result marker is missing." }
    $match = $line.Matches[0]
    $seconds = [double]::Parse($match.Groups['seconds'].Value, [Globalization.CultureInfo]::InvariantCulture)
    $maximum = [double]::Parse($match.Groups['maximum'].Value, [Globalization.CultureInfo]::InvariantCulture)
    if ($seconds + 0.01 -lt $durationSeconds -or $maximum -gt 0.05) {
        throw "Godot $versionLabel soak did not meet duration/growth gates."
    }
    $results.Add([pscustomobject]@{
        godotVersion = $versionLabel
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
