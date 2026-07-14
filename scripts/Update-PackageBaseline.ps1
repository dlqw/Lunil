[CmdletBinding()]
param(
    [string] $Version = '',

    [string] $PackageDirectory = '',

    [switch] $NoPack
)

$ErrorActionPreference = 'Stop'
& (Join-Path $PSScriptRoot 'Test-PackageCompatibility.ps1') `
    -Version $Version `
    -PackageDirectory $PackageDirectory `
    -NoPack:$NoPack `
    -Update
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
