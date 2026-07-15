[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
[xml]$props = Get-Content -LiteralPath (Join-Path $repositoryRoot 'Directory.Build.props')
$prefixNode = $props.SelectSingleNode('/Project/PropertyGroup/VersionPrefix')
if ($null -eq $prefixNode) {
    throw 'Directory.Build.props does not contain VersionPrefix.'
}

$prefix = [string]$prefixNode.InnerText
$suffixNode = $props.SelectSingleNode('/Project/PropertyGroup/VersionSuffix')
$suffix = if ($null -eq $suffixNode) { '' } else { [string]$suffixNode.InnerText }
$version = if ([string]::IsNullOrWhiteSpace($suffix)) { $prefix } else { "$prefix-$suffix" }

$semVerPattern = '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$'
if ($version -notmatch $semVerPattern) {
    throw "Directory.Build.props does not contain a supported release SemVer: $version"
}

Write-Output $version
