[CmdletBinding()]
param(
    [ValidateRange(1, [int]::MaxValue)]
    [int]$Iterations = 1000000,

    [int]$Seed = 319299622,

    [switch]$AllowReducedIterations
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot 'tests/Lunil.Fuzz.Fixture/Lunil.Fuzz.Fixture.csproj'

if (-not $AllowReducedIterations -and $Iterations -lt 1000000) {
    throw 'The release fuzz gate requires at least 1,000,000 cumulative bounded inputs. Use -AllowReducedIterations only for local calibration.'
}

$output = & dotnet run --project $project -c Release -- `
    "--iterations=$Iterations" "--seed=$Seed" 2>&1
if ($LASTEXITCODE -ne 0) {
    $output | ForEach-Object { Write-Host $_ }
    throw "Release fuzz fixture failed with exit code $LASTEXITCODE."
}

$output | ForEach-Object { Write-Host $_ }
$marker = $output | Where-Object { $_ -match '^LUNIL_RELEASE_FUZZ_RESULT ' } | Select-Object -Last 1
if (-not $marker) {
    throw 'Release fuzz fixture did not emit its result marker.'
}

$fields = @{}
foreach ($part in ($marker -split ' ' | Select-Object -Skip 1)) {
    $pair = $part -split '=', 2
    if ($pair.Count -eq 2) {
        $fields[$pair[0]] = $pair[1]
    }
}

$reported = 0
if (-not [int]::TryParse($fields['iterations'], [ref]$reported) -or $reported -ne $Iterations) {
    throw "Release fuzz fixture reported '$($fields['iterations'])' iterations; expected $Iterations."
}

$corpusTotal = 0L
foreach ($corpus in @('source', 'chunk', 'binding', 'patch')) {
    $count = 0
    $accepted = 0
    $rejected = 0
    if (-not [int]::TryParse($fields[$corpus], [ref]$count) -or $count -le 0) {
        throw "Release fuzz corpus '$corpus' did not report a positive count."
    }
    if (-not [int]::TryParse($fields["${corpus}_accept"], [ref]$accepted) -or
        -not [int]::TryParse($fields["${corpus}_reject"], [ref]$rejected) -or
        $accepted + $rejected -ne $count) {
        throw "Release fuzz corpus '$corpus' has invalid acceptance accounting."
    }
    $corpusTotal += $count
}

if ($corpusTotal -ne $Iterations) {
    throw "Release fuzz corpus total is $corpusTotal; expected $Iterations."
}

Write-Host "Release fuzz gate passed: $Iterations cumulative bounded inputs across four corpora."
