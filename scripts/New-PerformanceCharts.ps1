[CmdletBinding()]
param(
    [string] $DataPath = 'benchmarks/results/0.8.0-cross-runtime.json',
    [string] $OutputDirectory = 'assets/performance',
    [switch] $Verify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$dataPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $DataPath))
$outputDirectory = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
$data = Get-Content -Raw -LiteralPath $dataPath | ConvertFrom-Json

function Escape-Xml([string] $Value) {
    return [Security.SecurityElement]::Escape($Value)
}

function Format-Number([double] $Value, [string] $Format = '0.000') {
    return $Value.ToString($Format, [Globalization.CultureInfo]::InvariantCulture)
}

function Get-EngineColor([string] $Kind, [string] $Engine) {
    if ($Kind -eq 'lunil-primary') { return '#6d28d9' }
    if ($Kind -eq 'lunil') { return '#a78bfa' }
    if ($Engine -eq 'Native Lua 5.4') { return '#0f766e' }
    if ($Engine -eq 'LuaJIT') { return '#0e7490' }
    return '#64748b'
}

function New-EngineOverviewSvg($Report) {
    $width = 1120
    $height = 620
    $plotLeft = 260
    $plotRight = 1040
    $plotWidth = $plotRight - $plotLeft
    $minimum = 0.03
    $maximum = 16.0
    $ticks = @(0.03, 0.1, 0.3, 1.0, 3.0, 10.0)
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('<svg xmlns="http://www.w3.org/2000/svg" width="1120" height="620" viewBox="0 0 1120 620" role="img" aria-labelledby="title desc">')
    $lines.Add("  <title id=`"title`">Lunil $($Report.release) runtime comparison</title>")
    $lines.Add('  <desc id="desc">Geometric mean speedup across eight workloads and six release platforms, normalized to native Lua 5.4.</desc>')
    $lines.Add('  <rect width="1120" height="620" rx="20" fill="#f8fafc"/>')
    $lines.Add('  <text x="56" y="64" font-family="Inter,Segoe UI,sans-serif" font-size="28" font-weight="700" fill="#0f172a">Runtime comparison</text>')
    $lines.Add("  <text x=`"56`" y=`"94`" font-family=`"Inter,Segoe UI,sans-serif`" font-size=`"15`" fill=`"#475569`">Lunil $($Report.release) · geometric mean across 8 workloads × 6 release RIDs</text>")
    foreach ($tick in $ticks) {
        $position = $plotLeft + (([Math]::Log($tick) - [Math]::Log($minimum)) / ([Math]::Log($maximum) - [Math]::Log($minimum))) * $plotWidth
        $x = Format-Number $position '0.0'
        $stroke = if ($tick -eq 1.0) { '#0f766e' } else { '#cbd5e1' }
        $dash = if ($tick -eq 1.0) { '' } else { ' stroke-dasharray="3 5"' }
        $lines.Add("  <line x1=`"$x`" y1=`"126`" x2=`"$x`" y2=`"532`" stroke=`"$stroke`" stroke-width=`"1`"$dash/>")
        $lines.Add("  <text x=`"$x`" y=`"552`" text-anchor=`"middle`" font-family=`"Inter,Segoe UI,sans-serif`" font-size=`"12`" fill=`"#64748b`">$(Format-Number $tick '0.##')×</text>")
    }
    $row = 0
    foreach ($engine in $Report.overall) {
        $y = 142 + ($row * 48)
        $barY = $y - 18
        $end = $plotLeft + (([Math]::Log([double]$engine.speedupVsNativeLua) - [Math]::Log($minimum)) / ([Math]::Log($maximum) - [Math]::Log($minimum))) * $plotWidth
        $barWidth = [Math]::Max(4, $end - $plotLeft)
        $color = Get-EngineColor $engine.kind $engine.engine
        $lines.Add("  <text x=`"236`" y=`"$y`" text-anchor=`"end`" font-family=`"Inter,Segoe UI,sans-serif`" font-size=`"15`" font-weight=`"600`" fill=`"#1e293b`">$(Escape-Xml $engine.engine)</text>")
        $lines.Add("  <rect x=`"$plotLeft`" y=`"$barY`" width=`"$(Format-Number $barWidth '0.0')`" height=`"26`" rx=`"6`" fill=`"$color`"/>")
        $labelX = [Math]::Min($plotRight - 2, $end + 10)
        $anchor = if ($end + 76 -gt $plotRight) { 'end' } else { 'start' }
        $lines.Add("  <text x=`"$(Format-Number $labelX '0.0')`" y=`"$y`" text-anchor=`"$anchor`" font-family=`"Inter,Segoe UI,sans-serif`" font-size=`"14`" font-weight=`"700`" fill=`"#0f172a`">$(Format-Number $engine.speedupVsNativeLua)×</text>")
        $row++
    }
    $lines.Add('  <text x="56" y="590" font-family="Inter,Segoe UI,sans-serif" font-size="13" fill="#64748b">Higher is faster · logarithmic scale · native Lua 5.4 = 1.000×</text>')
    $lines.Add('</svg>')
    return ($lines -join "`n") + "`n"
}

function New-WorkloadSvg($Report) {
    $width = 1120
    $height = 620
    $plotLeft = 250
    $plotRight = 1030
    $plotWidth = $plotRight - $plotLeft
    $observedMaximum = ($Report.autoWorkloads |
        Measure-Object -Property speedupVsNativeLua -Maximum).Maximum
    $maximum = if ([double]$observedMaximum -le 3.2) {
        3.2
    }
    else {
        [Math]::Ceiling([double]$observedMaximum)
    }
    $ticks = if ($maximum -eq 3.2) {
        @(0.0, 0.5, 1.0, 1.5, 2.0, 2.5, 3.0)
    }
    else {
        @(0..([int]$maximum))
    }
    $displayNames = @{
        arithmetic = 'Arithmetic'
        fib_iter = 'Iterative Fibonacci'
        mandelbrot = 'Mandelbrot'
        control_flow = 'Control flow'
        function_calls = 'Function calls'
        table_access = 'Table access'
        sieve = 'Prime sieve'
        string_build = 'String build'
    }
    $lines = [Collections.Generic.List[string]]::new()
    $lines.Add('<svg xmlns="http://www.w3.org/2000/svg" width="1120" height="620" viewBox="0 0 1120 620" role="img" aria-labelledby="title desc">')
    $lines.Add("  <title id=`"title`">Lunil $($Report.release) Auto JIT by workload</title>")
    $lines.Add('  <desc id="desc">Auto JIT speedup per workload across six release platforms, normalized to native Lua 5.4.</desc>')
    $lines.Add('  <rect width="1120" height="620" rx="20" fill="#f8fafc"/>')
    $lines.Add('  <text x="56" y="64" font-family="Inter,Segoe UI,sans-serif" font-size="28" font-weight="700" fill="#0f172a">Auto JIT by workload</text>')
    $lines.Add("  <text x=`"56`" y=`"94`" font-family=`"Inter,Segoe UI,sans-serif`" font-size=`"15`" fill=`"#475569`">Lunil $($Report.release) · six-RID geometric mean · native Lua 5.4 = 1.000×</text>")
    foreach ($tick in $ticks) {
        $position = $plotLeft + ($tick / $maximum) * $plotWidth
        $stroke = if ($tick -eq 1.0) { '#0f766e' } else { '#cbd5e1' }
        $dash = if ($tick -eq 1.0) { '' } else { ' stroke-dasharray="3 5"' }
        $lines.Add("  <line x1=`"$(Format-Number $position '0.0')`" y1=`"126`" x2=`"$(Format-Number $position '0.0')`" y2=`"532`" stroke=`"$stroke`" stroke-width=`"1`"$dash/>")
        $lines.Add("  <text x=`"$(Format-Number $position '0.0')`" y=`"552`" text-anchor=`"middle`" font-family=`"Inter,Segoe UI,sans-serif`" font-size=`"12`" fill=`"#64748b`">$(Format-Number $tick '0.0')×</text>")
    }
    $row = 0
    foreach ($workload in $Report.autoWorkloads) {
        $y = 142 + ($row * 48)
        $barY = $y - 18
        $barWidth = ([double]$workload.speedupVsNativeLua / $maximum) * $plotWidth
        $color = if ([double]$workload.speedupVsNativeLua -ge 1.0) { '#6d28d9' } else { '#a78bfa' }
        $name = $displayNames[$workload.workload]
        $lines.Add("  <text x=`"226`" y=`"$y`" text-anchor=`"end`" font-family=`"Inter,Segoe UI,sans-serif`" font-size=`"15`" font-weight=`"600`" fill=`"#1e293b`">$(Escape-Xml $name)</text>")
        $lines.Add("  <rect x=`"$plotLeft`" y=`"$barY`" width=`"$(Format-Number ([Math]::Max(3, $barWidth)) '0.0')`" height=`"26`" rx=`"6`" fill=`"$color`"/>")
        $lines.Add("  <text x=`"$(Format-Number ($plotLeft + $barWidth + 10) '0.0')`" y=`"$y`" font-family=`"Inter,Segoe UI,sans-serif`" font-size=`"14`" font-weight=`"700`" fill=`"#0f172a`">$(Format-Number $workload.speedupVsNativeLua)×</text>")
        $row++
    }
    $lines.Add('  <text x="56" y="590" font-family="Inter,Segoe UI,sans-serif" font-size="13" fill="#64748b">Purple bars above the green 1.000× line are faster than native Lua.</text>')
    $lines.Add('</svg>')
    return ($lines -join "`n") + "`n"
}

$outputs = @{
    "$($data.release)-runtime-overview.svg" = New-EngineOverviewSvg $data
    "$($data.release)-auto-workloads.svg" = New-WorkloadSvg $data
}

if (-not $Verify) {
    New-Item -ItemType Directory -Force -Path $outputDirectory | Out-Null
}

$encoding = [Text.UTF8Encoding]::new($false)
foreach ($entry in $outputs.GetEnumerator()) {
    $path = Join-Path $outputDirectory $entry.Key
    if ($Verify) {
        if (-not (Test-Path -LiteralPath $path)) {
            throw "Generated performance chart is missing: $path"
        }
        $actual = [IO.File]::ReadAllText($path).Replace("`r`n", "`n")
        if ($actual -ne $entry.Value) {
            throw "Generated performance chart is stale: $path"
        }
        continue
    }
    [IO.File]::WriteAllText($path, $entry.Value, $encoding)
    Write-Host "Generated $path"
}

if ($Verify) {
    Write-Host 'Performance charts are up to date.'
}
