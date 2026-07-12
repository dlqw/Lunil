[CmdletBinding()]
param(
    [ValidateRange(3, 101)]
    [int] $Rounds = 3,

    [ValidateRange(1, 1000000000)]
    [int] $Iterations = 1000000,

    [string] $Configuration = 'Release',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot `
    'benchmarks/Lunil.Runtime.Benchmarks/Lunil.Runtime.Benchmarks.csproj'
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss', [Globalization.CultureInfo]::InvariantCulture)
$outputDirectory = Join-Path $repositoryRoot "artifacts/backend-performance/$stamp"
New-Item -ItemType Directory -Path $outputDirectory -Force | Out-Null

function Get-Median([double[]] $Values) {
    if ($Values.Count -eq 0) { return 0.0 }
    $ordered = @($Values | Sort-Object)
    $middle = [Math]::Floor($ordered.Count / 2)
    if (($ordered.Count % 2) -eq 0) {
        return ($ordered[$middle - 1] + $ordered[$middle]) / 2.0
    }

    return $ordered[$middle]
}

function ConvertTo-BackendRecord([string] $Line, [int] $Round) {
    $values = @{}
    foreach ($part in $Line.Substring('backend_evidence '.Length).Split(',')) {
        $pair = $part.Trim().Split('=', 2)
        if ($pair.Count -ne 2) { throw "Malformed backend evidence field: $part" }
        $values[$pair[0]] = $pair[1]
    }

    return [pscustomobject]@{
        Round = $Round
        Name = $values.name
        Operations = [int]$values.operations
        StartupMedianMs = [double]::Parse($values.startup_median_ms, [Globalization.CultureInfo]::InvariantCulture)
        StartupP95Ms = [double]::Parse($values.startup_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        WarmNsOp = [double]::Parse($values.warm_ns_op, [Globalization.CultureInfo]::InvariantCulture)
        AllocatedOp = [double]::Parse($values.allocated_op, [Globalization.CultureInfo]::InvariantCulture)
        CompilationP95Ms = [double]::Parse($values.compilation_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        Tier1P95Ms = [double]::Parse($values.tier1_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        Tier2P95Ms = [double]::Parse($values.tier2_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        LoopOsrP95Ms = [double]::Parse($values.loop_osr_p95_ms, [Globalization.CultureInfo]::InvariantCulture)
        RssPeakDeltaBytes = [long]$values.rss_peak_delta_bytes
        EstimatedCodeBytes = [long]$values.estimated_code_bytes
    }
}

$records = [Collections.Generic.List[object]]::new()
Push-Location $repositoryRoot
try {
    for ($round = 1; $round -le $Rounds; $round++) {
        Write-Host "Backend performance evidence round $round/$Rounds"
        $arguments = @(
            'run', '--project', $project,
            '--configuration', $Configuration
        )
        if ($NoBuild) {
            $arguments += '--no-build'
        }
        $arguments += @('--', $Iterations)
        $lines = & dotnet @arguments
        if ($LASTEXITCODE -ne 0) {
            throw "Backend performance runner failed in round $round."
        }

        $runFile = Join-Path $outputDirectory ("round-{0:D3}.txt" -f $round)
        $lines | Set-Content -LiteralPath $runFile -Encoding utf8
        foreach ($line in $lines | Where-Object { $_ -like 'backend_evidence *' }) {
            $records.Add((ConvertTo-BackendRecord $line $round))
        }
    }
}
finally {
    Pop-Location
}

if ($records.Count -ne ($Rounds * 4)) {
    throw "Expected $($Rounds * 4) backend evidence records, found $($records.Count)."
}

$records | Export-Csv -LiteralPath (Join-Path $outputDirectory 'runs.csv') `
    -NoTypeInformation -Encoding utf8
$summary = foreach ($group in $records | Group-Object Name | Sort-Object Name) {
    [pscustomobject]@{
        Name = $group.Name
        Rounds = $group.Count
        StartupMedianMs = Get-Median @($group.Group.StartupMedianMs)
        StartupP95Ms = Get-Median @($group.Group.StartupP95Ms)
        WarmNsOp = Get-Median @($group.Group.WarmNsOp)
        AllocatedOp = Get-Median @($group.Group.AllocatedOp)
        CompilationP95Ms = Get-Median @($group.Group.CompilationP95Ms)
        RssPeakDeltaBytes = Get-Median @($group.Group.RssPeakDeltaBytes)
        EstimatedCodeBytes = Get-Median @($group.Group.EstimatedCodeBytes)
    }
}
$summary | ConvertTo-Json | Set-Content `
    -LiteralPath (Join-Path $outputDirectory 'summary.json') -Encoding utf8
$summary | Format-Table -AutoSize
Write-Host "Backend performance evidence written to $outputDirectory"
