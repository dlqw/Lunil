[CmdletBinding()]
param(
    [ValidateRange(1, 1000)]
    [int] $Rounds = 5,

    [string] $Configuration = 'Release',

    [switch] $NoBuild
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$stamp = [DateTime]::UtcNow.ToString(
    'yyyyMMdd-HHmmss',
    [Globalization.CultureInfo]::InvariantCulture)
$resultsRoot = Join-Path $repositoryRoot "artifacts/backend-release-soak/$stamp"

$testGroups = @(
    @{
        Name = 'backend-differential'
        Project = 'tests/Lunil.BackendDifferential.Tests/Lunil.BackendDifferential.Tests.csproj'
        Filter = 'FullyQualifiedName~Lunil.BackendDifferential.Tests'
    },
    @{
        Name = 'deterministic-fuzz-gc-coroutine'
        Project = 'tests/Lunil.Stability.Tests/Lunil.Stability.Tests.csproj'
        Filter = 'FullyQualifiedName~Lunil.Stability.Tests'
    },
    @{
        Name = 'jit-tier-profile-osr'
        Project = 'tests/Lunil.CodeGen.Cil.Tests/Lunil.CodeGen.Cil.Tests.csproj'
        Filter = 'FullyQualifiedName~LuaJitExecutorTests'
    },
    @{
        Name = 'cil-plan-emission'
        Project = 'tests/Lunil.CodeGen.Cil.Tests/Lunil.CodeGen.Cil.Tests.csproj'
        Filter = 'FullyQualifiedName~LuaCilCodeGeneratorTests'
    }
)

New-Item -ItemType Directory -Path $resultsRoot -Force | Out-Null
Push-Location $repositoryRoot
try {
    for ($round = 1; $round -le $Rounds; $round++) {
        Write-Host "Backend release soak round $round/$Rounds"
        foreach ($group in $testGroups) {
            $project = Join-Path $repositoryRoot $group.Project
            $resultDirectory = Join-Path $resultsRoot ("round-{0:D3}" -f $round)
            New-Item -ItemType Directory -Path $resultDirectory -Force | Out-Null
            $arguments = @(
                'test', $project,
                '--configuration', $Configuration,
                '--filter', $group.Filter,
                '--logger', "trx;LogFileName=$($group.Name).trx",
                '--results-directory', $resultDirectory,
                '--no-restore'
            )
            if ($NoBuild) {
                $arguments += '--no-build'
            }

            & dotnet @arguments
            if ($LASTEXITCODE -ne 0) {
                throw "Backend release soak failed in round $round group $($group.Name)."
            }
        }
    }
}
finally {
    Pop-Location
}

Write-Host "Backend release soak completed: $Rounds round(s), results: $resultsRoot"
