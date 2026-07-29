[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$actualVersion = (& (Join-Path $PSScriptRoot 'Get-LunilVersion.ps1')).Trim()
if ($actualVersion -ne $Version) {
    throw "Requested Godot addon $Version, but the repository is $actualVersion."
}

$sourceAddon = Join-Path $repositoryRoot 'integrations/godot/addons/lunil'
$pluginConfig = Get-Content -Raw (Join-Path $sourceAddon 'plugin.cfg')
if ($pluginConfig -notmatch ('(?m)^version="' + [Regex]::Escape($Version) + '"$')) {
    throw "Godot addon plugin.cfg does not identify version $Version."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/godot/$Version"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $OutputDirectory.StartsWith(
    $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Godot addon output escaped the artifacts directory: $OutputDirectory"
}

$stageRoot = Join-Path $OutputDirectory 'stage'
$stageAddon = Join-Path $stageRoot 'addons/lunil'
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Path $stageAddon -Force | Out-Null
Copy-Item -Path (Join-Path $sourceAddon '*') -Destination $stageAddon -Recurse -Force

$archive = Join-Path $OutputDirectory "Lunil.Godot.addon-$Version.zip"
Compress-Archive -Path (Join-Path $stageRoot 'addons') -DestinationPath $archive `
    -CompressionLevel Optimal
$stream = [IO.File]::OpenRead($archive)
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $hashBytes = $sha256.ComputeHash($stream)
    $hash = ([BitConverter]::ToString($hashBytes) -replace '-', '').ToLowerInvariant()
}
finally {
    $sha256.Dispose()
    $stream.Dispose()
}
[pscustomobject]@{
    Path = $archive
    Sha256 = $hash
    Version = $Version
    AddonPath = 'addons/lunil'
    RequiredNuGetPackage = "Lunil.Godot@$Version"
}
