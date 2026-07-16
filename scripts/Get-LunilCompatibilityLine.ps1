[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$version = (& (Join-Path $PSScriptRoot 'Get-LunilVersion.ps1')).Trim()
if ($version -notmatch '^(?<major>0|[1-9]\d*)\.(?<minor>0|[1-9]\d*)\.(?<patch>0|[1-9]\d*)(?:-|$)') {
    throw "Could not derive a compatibility line from the active Lunil version: $version"
}

Write-Output "$($Matches.major).$($Matches.minor).0"
