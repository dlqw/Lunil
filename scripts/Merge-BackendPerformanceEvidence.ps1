[CmdletBinding()]
param(
    [string] $InputRoot = 'artifacts/backend-performance',

    [string] $Output = 'artifacts/backend-performance/tier1-six-rid-decision.json',

    [string] $Tier2Output = 'artifacts/backend-performance/tier2-six-rid-decision.json'
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
$tier2OutputPath = if ([System.IO.Path]::IsPathRooted($Tier2Output)) {
    $Tier2Output
}
else {
    Join-Path $repositoryRoot $Tier2Output
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

$tier2Decisions = Get-ChildItem -LiteralPath $inputPath -Recurse -Filter 'tier2-decision.json' |
    ForEach-Object {
        $decision = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
        [pscustomobject]@{
            Path = $_.FullName
            LastWriteTimeUtc = $_.LastWriteTimeUtc
            Decision = $decision
        }
    }
$tier2Selected = [Collections.Generic.List[object]]::new()
foreach ($rid in $requiredRids) {
    $match = $tier2Decisions | Where-Object { $_.Decision.Rid -eq $rid } |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1
    if ($null -eq $match) {
        throw "Missing Tier 2 performance decision for RID $rid under $inputPath."
    }

    $tier2Selected.Add($match.Decision)
}

$tier2Result = [pscustomobject]@{
    GeneratedAtUtc = [DateTime]::UtcNow.ToString('O')
    RequiredRids = $requiredRids
    Decisions = $tier2Selected.ToArray()
    MinimumArithmeticSpeedupCi95Lower = (
        $tier2Selected | Measure-Object ArithmeticSpeedupCi95Lower -Minimum).Minimum
    MaximumTier2CompilationP95Ms = (
        $tier2Selected | Measure-Object Tier2CompilationP95Ms -Maximum).Maximum
    MinimumTier2LivenessCacheHitRate = (
        $tier2Selected | Measure-Object Tier2LivenessCacheHitRate -Minimum).Minimum
    MaximumTier2CompileAllocatedP95Bytes = (
        $tier2Selected | Measure-Object Tier2CompileAllocatedP95Bytes -Maximum).Maximum
    MaximumAbsoluteAllocationSlopeBytesIteration = (
        $tier2Selected | ForEach-Object {
            [Math]::Abs($_.ArithmeticAllocationSlopeBytesIteration)
        } | Measure-Object -Maximum).Maximum
    AllRidsUseExactNumericSpecializedCil = @($tier2Selected | Where-Object {
        $_.Tier2CodeKind -ne 'ExactNumericSpecializedCil'
    }).Count -eq 0
    AllRidsQualify = @($tier2Selected | Where-Object {
        -not $_.QualifiesThisRid
    }).Count -eq 0
}

New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($tier2OutputPath)) `
    -Force | Out-Null
$tier2Result | ConvertTo-Json -Depth 8 |
    Set-Content -LiteralPath $tier2OutputPath -Encoding utf8
$tier2Result | Format-List
Write-Host "Six-RID Tier 2 decision written to $tier2OutputPath"
