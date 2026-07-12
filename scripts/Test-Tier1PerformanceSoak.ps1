[CmdletBinding()]
param(
    [ValidateRange(1, 1000)]
    [int] $Rounds = 20,

    [string] $Configuration = 'Release',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stamp = [DateTime]::UtcNow.ToString(
    'yyyyMMdd-HHmmss',
    [Globalization.CultureInfo]::InvariantCulture)
$resultsDirectory = Join-Path $repositoryRoot "artifacts/tier1-soak/$stamp"
New-Item -ItemType Directory -Path $resultsDirectory -Force | Out-Null

$projects = @(
    'tests/Lunil.Runtime.Tests/Lunil.Runtime.Tests.csproj',
    'tests/Lunil.CodeGen.Cil.Tests/Lunil.CodeGen.Cil.Tests.csproj',
    'tests/Lunil.BackendDifferential.Tests/Lunil.BackendDifferential.Tests.csproj'
)

Push-Location $repositoryRoot
try {
    if (-not $NoBuild) {
        & dotnet build Lunil.sln --configuration $Configuration
        if ($LASTEXITCODE -ne 0) {
            throw 'Tier 1 soak build failed.'
        }
    }

    for ($round = 1; $round -le $Rounds; $round++) {
        Write-Host "Tier 1 semantic/fault soak round $round/$Rounds"
        foreach ($project in $projects) {
            $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project)
            $trxName = '{0}-round-{1:D3}.trx' -f $projectName, $round
            $arguments = @(
                'test', $project,
                '--configuration', $Configuration,
                '--no-build',
                '--results-directory', $resultsDirectory,
                '--logger', "trx;LogFileName=$trxName"
            )
            & dotnet @arguments
            if ($LASTEXITCODE -ne 0) {
                throw "Tier 1 soak failed in round $round for $project."
            }
        }
    }
}
finally {
    Pop-Location
}

Write-Host "Tier 1 soak passed $Rounds rounds. TRX files: $resultsDirectory"
