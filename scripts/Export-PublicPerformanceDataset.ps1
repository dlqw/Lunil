[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ReportPath,
    [string] $Release = "0.10.0",
    [string] $OutputPath = "benchmarks/results/0.10.0-performance.json",
    [string[]] $Rids = @("win-x64")
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$ReportPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $ReportPath))
$OutputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputPath))

$report = Get-Content -Raw -LiteralPath $ReportPath | ConvertFrom-Json
if ($report.schemaVersion -notin @(2, 3)) {
    throw "Unsupported report schema $($report.schemaVersion)"
}

function Get-DisplayName([string] $Id, [string] $Fallback) {
    switch ($Id) {
        "lua54" { return "Native Lua 5.4" }
        "luajit" { return "LuaJIT" }
        "moonsharp" { return "MoonSharp" }
        "lunil_auto" { return "Lunil Auto JIT" }
        "neolua" { return "NeoLua" }
        "luau" { return "Luau" }
        "gopherlua" { return "GopherLua" }
        "wasmoon" { return "Wasmoon" }
        "unilua" { return "UniLua" }
        default { return $Fallback }
    }
}

function Get-Kind([string] $Id) {
    if ($Id -eq "lunil_auto") { return "lunil-primary" }
    if ($Id -like "lunil_*") { return "lunil" }
    return "reference"
}

$engineMeta = @{}
foreach ($engine in $report.engines) {
    $engineMeta[$engine.id] = $engine
}

$publicIds = @(
    "luajit", "lua54", "lunil_auto", "moonsharp",
    "neolua", "luau", "gopherlua", "wasmoon", "unilua"
)

$overall = @()
foreach ($row in $report.overall) {
    if ($row.engine -notin $publicIds) { continue }
    $display = Get-DisplayName $row.engine $row.engine
    $overall += [ordered]@{
        engine = $display
        engineId = $row.engine
        speedupVsNativeLua = [double]$row.geometricMeanSpeedupVsNativeLua
        speedupVsMoonSharp = [double]$row.geometricMeanSpeedupVsComparison
        kind = Get-Kind $row.engine
        semanticGroup = switch ($row.engine) {
            "lua54" { "lua54" }
            "wasmoon" { "lua54" }
            "luajit" { "lua51-dialect" }
            "luau" { "lua51-dialect" }
            "gopherlua" { "lua51-dialect" }
            "unilua" { "lua52-managed" }
            default { "managed-dotnet" }
        }
    }
}

$autoWorkloads = @()
$autoRows = $report.results | Where-Object { $_.engine -eq "lunil_auto" -and ($_.role -eq "Release" -or -not $_.role) }
foreach ($row in $autoRows) {
    $autoWorkloads += [ordered]@{
        workload = $row.workload
        speedupVsNativeLua = [double]$row.speedupVsNativeLua
        speedupVsMoonSharp = [double]$row.speedupVsComparison
    }
}

$optionalMeasured = @($report.engines | Where-Object { $_.id -in @("neolua","luau","gopherlua","wasmoon","unilua") } | ForEach-Object { $_.id })

$dataset = [ordered]@{
    schemaVersion = 3
    release = $Release
    referenceRuntimes = [ordered]@{
        pucLua = [ordered]@{
            name = "PUC Lua"
            version = "5.4.8"
            sourceSha256 = "4f18ddae154e793e46eeab727c59ef1c0c0c2b744e7b94219710d76f530629ae"
        }
        luaJit = [ordered]@{
            name = "LuaJIT"
            version = "2.1"
            commit = "3c4f9fe2052b8d08a917ac0d5f38563f0297b5a3"
        }
        moonSharp = [ordered]@{
            name = "MoonSharp"
            version = "2.0.0"
            package = "MoonSharp"
        }
        neoLua = [ordered]@{
            name = "NeoLua"
            version = "1.3.19"
            package = "NeoLua"
            note = "net8 out-of-process harness"
        }
        luau = [ordered]@{
            name = "Luau"
            version = "0.623"
            source = "https://github.com/luau-lang/luau/releases/tag/0.623"
        }
        gopherLua = [ordered]@{
            name = "GopherLua"
            version = "1.1.1"
            source = "https://github.com/yuin/gopher-lua/releases/tag/v1.1.1"
        }
        wasmoon = [ordered]@{
            name = "Wasmoon"
            version = "1.16.0"
            source = "https://github.com/ceifa/wasmoon/releases/tag/v1.16.0"
        }
        uniLua = [ordered]@{
            name = "UniLua"
            version = "194eb311191111bfdbc77070de67c100235dc618"
            source = "https://github.com/xebecnan/UniLua"
        }
    }
    source = [ordered]@{
        rids = @($Rids)
        workloads = @($autoWorkloads).Count
        rounds = [int]$report.settings.rounds
        targetMilliseconds = [double]$report.settings.targetCpuMilliseconds
        baselineEngine = "lua54"
        comparisonEngine = "moonsharp"
        optionalEnginesMeasured = $optionalMeasured
        comparisonPolicy = "compareOnlyWithinSemanticGroup; no single merged score across LuaJIT/dialects"
        reportPath = [IO.Path]::GetRelativePath($repositoryRoot, $ReportPath).Replace("\", "/")
    }
    overall = $overall
    autoWorkloads = $autoWorkloads
}

$dataset | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $OutputPath -Encoding utf8
Write-Host "Wrote public dataset $OutputPath"
