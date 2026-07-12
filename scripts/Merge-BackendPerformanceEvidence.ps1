[CmdletBinding()]
param(
    [string] $InputRoot = 'artifacts/backend-performance',

    [string] $Output = 'artifacts/backend-performance/tier1-six-rid-decision.json'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$inputPath = if ([System.IO.Path]::IsPathRooted($InputRoot)) {
    $InputRoot
}
else {
    Join-Path $repositoryRoot $InputRoot
}
$outputPath = if ([System.IO.Path]::IsPathRooted($Output)) {
    $Output
}
else {
    Join-Path $repositoryRoot $Output
}
$requiredRids = @(
    'win-x64',
    'win-arm64',
    'linux-x64',
    'linux-arm64',
    'osx-x64',
    'osx-arm64'
)

$decisions = Get-ChildItem -LiteralPath $inputPath -Recurse -Filter 'tier1-decision.json' |
    ForEach-Object {
        $decision = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        [pscustomobject]@{
            Path = $_.FullName
            LastWriteTimeUtc = $_.LastWriteTimeUtc
            Decision = $decision
        }
    }
$selected = [Collections.Generic.List[object]]::new()
foreach ($rid in $requiredRids) {
    $match = $decisions | Where-Object { $_.Decision.Rid -eq $rid } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $match) {
        throw "Missing backend performance decision for RID $rid under $inputPath."
    }

    $selected.Add($match.Decision)
}

$result = [pscustomobject]@{
    GeneratedAtUtc = [DateTime]::UtcNow.ToString('O')
    RequiredRids = $requiredRids
    Decisions = $selected.ToArray()
    MinimumArithmeticSpeedupCi95Lower = (
        $selected | Measure-Object ArithmeticSpeedupCi95Lower -Minimum).Minimum
    MaximumTier1CompilationP95Ms = (
        $selected | Measure-Object Tier1CompilationP95Ms -Maximum).Maximum
    MaximumAbsoluteAllocationSlopeBytesIteration = (
        $selected | ForEach-Object {
            [Math]::Abs($_.ArithmeticAllocationSlopeBytesIteration)
        } | Measure-Object -Maximum).Maximum
    AllRidsQualify = @($selected | Where-Object { -not $_.QualifiesThisRid }).Count -eq 0
}

New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($outputPath)) `
    -Force | Out-Null
$result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $outputPath -Encoding utf8
$result | Format-List
Write-Host "Six-RID Tier 1 decision written to $outputPath"
