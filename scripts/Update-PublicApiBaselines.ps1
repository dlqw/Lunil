[CmdletBinding()]
param(
    [string] $Configuration = 'Release',

    [switch] $NoBuild,

    [switch] $NoRestore
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-PublicApiBaselines.ps1') `
    -Configuration $Configuration `
    -NoBuild:$NoBuild `
    -NoRestore:$NoRestore `
    -Update
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
