[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props')
$prefix = [string]$props.Project.PropertyGroup.VersionPrefix
$suffix = [string]$props.Project.PropertyGroup.VersionSuffix
$version = if ([string]::IsNullOrWhiteSpace($suffix)) { $prefix } else { "$prefix-$suffix" }

$semVerPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$'
if ($version -notmatch $semVerPattern) {
    throw "Directory.Build.props does not contain a supported release SemVer: $version"
}

Write-Output $version
