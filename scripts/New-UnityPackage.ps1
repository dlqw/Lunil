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
    throw "Requested Unity package $Version, but the repository is $actualVersion."
}

$sourcePackage = Join-Path $repositoryRoot 'integrations/unity/com.dlqw.lunil'
$sourceManifest = Join-Path $sourcePackage 'package.json'
$manifest = Get-Content -Raw $sourceManifest | ConvertFrom-Json
if ($manifest.name -ne 'com.dlqw.lunil' -or $manifest.version -ne $Version) {
    throw "Unity package manifest identity/version does not match com.dlqw.lunil@$Version."
}
if ($manifest.unity -ne '2022.3') {
    throw "Unity package minimum must remain 2022.3, found $($manifest.unity)."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/unity/$Version"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
if (-not $OutputDirectory.StartsWith(
    $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Unity package output escaped the artifacts directory: $OutputDirectory"
}

$stageRoot = Join-Path $OutputDirectory 'stage'
$stagePackage = Join-Path $stageRoot 'package'
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $stageRoot) {
    Remove-Item -LiteralPath $stageRoot -Recurse -Force
}
Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.tgz' -File |
    Remove-Item -Force
New-Item -ItemType Directory -Path $stagePackage -Force | Out-Null
Copy-Item -Path (Join-Path $sourcePackage '*') -Destination $stagePackage -Recurse -Force

$plugins = Join-Path $stagePackage 'Runtime/Plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
& dotnet publish (Join-Path $repositoryRoot 'src/Lunil.Hosting/Lunil.Hosting.csproj') `
    -c Release -f netstandard2.1 --no-restore -o $plugins
if ($LASTEXITCODE -ne 0) { throw 'Publishing portable Lunil assemblies for Unity failed.' }

Get-ChildItem -LiteralPath $plugins -File | Where-Object {
    $_.Extension -ne '.dll'
} | Remove-Item -Force

$requiredAssemblies = @(
    'Lunil.Core.dll', 'Lunil.Syntax.dll', 'Lunil.EmmyLua.dll',
    'Lunil.Semantics.dll', 'Lunil.Analysis.dll', 'Lunil.IR.dll',
    'Lunil.Compiler.dll', 'Lunil.Runtime.dll', 'Lunil.StandardLibrary.dll',
    'Lunil.Workspace.dll', 'Lunil.Hosting.dll', 'Microsoft.Bcl.TimeProvider.dll',
    'System.Collections.Immutable.dll', 'System.Diagnostics.DiagnosticSource.dll',
    'System.Text.Json.dll'
)
foreach ($assembly in $requiredAssemblies) {
    if (-not (Test-Path -LiteralPath (Join-Path $plugins $assembly) -PathType Leaf)) {
        throw "Portable Unity package is missing $assembly."
    }
}
if (Test-Path -LiteralPath (Join-Path $plugins 'Lunil.CodeGen.Cil.dll')) {
    throw 'The Unity package must not contain the dynamic-code JIT assembly.'
}

$generatorSource = Join-Path $repositoryRoot `
    'src/Lunil.Hosting.Generators/bin/Release/netstandard2.0/Lunil.Hosting.Generators.dll'
if (-not (Test-Path -LiteralPath $generatorSource -PathType Leaf)) {
    throw 'The CLR binding generator was not built for the Unity package.'
}
$editorTools = Join-Path $stagePackage 'Editor/Tools'
New-Item -ItemType Directory -Path $editorTools -Force | Out-Null
Copy-Item -LiteralPath $generatorSource `
    -Destination (Join-Path $editorTools 'Lunil.Hosting.Generators.dll.bytes') -Force

$linkXml = @'
<linker>
  <assembly fullname="Lunil.Unity" preserve="all" />
  <assembly fullname="Lunil.Unity.Unity6" preserve="all" />
</linker>
'@
[IO.File]::WriteAllText(
    (Join-Path $stagePackage 'Runtime/link.xml'),
    $linkXml,
    [Text.UTF8Encoding]::new($false))

function Get-UnityGuid([string] $RelativePath) {
    $normalized = $RelativePath -replace '\\', '/'
    $bytes = [Text.Encoding]::UTF8.GetBytes($normalized.ToLowerInvariant())
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try { $hash = $sha256.ComputeHash($bytes) } finally { $sha256.Dispose() }
    return ([BitConverter]::ToString($hash, 0, 16) -replace '-', '').ToLowerInvariant()
}

function Get-Sha256Hex([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha256.ComputeHash($stream)
        return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Get-StagedRelativePath([string] $AssetPath) {
    $root = [IO.Path]::GetFullPath($stagePackage).TrimEnd(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    $fullPath = [IO.Path]::GetFullPath($AssetPath)
    if (-not $fullPath.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unity package asset escaped the staging directory: $fullPath"
    }

    return $fullPath.Substring($root.Length) -replace '\\', '/'
}

function Write-UnityMeta([string] $AssetPath, [bool] $IsFolder) {
    $relative = Get-StagedRelativePath $AssetPath
    $guid = Get-UnityGuid $relative
    if ($IsFolder) {
        $body = "fileFormatVersion: 2`nguid: $guid`nfolderAsset: yes`nDefaultImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
    }
    elseif ([IO.Path]::GetExtension($AssetPath) -eq '.dll') {
        $body = @"
fileFormatVersion: 2
guid: $guid
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 1
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  userData:
  assetBundleName:
  assetBundleVariant:
"@
    }
    elseif ([IO.Path]::GetExtension($AssetPath) -eq '.cs') {
        $body = "fileFormatVersion: 2`nguid: $guid`nMonoImporter:`n  externalObjects: {}`n  serializedVersion: 2`n  defaultReferences: []`n  executionOrder: 0`n  icon: {instanceID: 0}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
    }
    elseif ([IO.Path]::GetExtension($AssetPath) -eq '.asmdef') {
        $body = "fileFormatVersion: 2`nguid: $guid`nAssemblyDefinitionImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
    }
    else {
        $body = "fileFormatVersion: 2`nguid: $guid`nDefaultImporter:`n  externalObjects: {}`n  userData: `n  assetBundleName: `n  assetBundleVariant: `n"
    }
    [IO.File]::WriteAllText($AssetPath + '.meta', $body, [Text.UTF8Encoding]::new($false))
}

Get-ChildItem -LiteralPath $stagePackage -Directory -Recurse | ForEach-Object {
    Write-UnityMeta $_.FullName $true
}
Get-ChildItem -LiteralPath $stagePackage -File -Recurse | Where-Object {
    $_.Extension -ne '.meta'
} | ForEach-Object {
    Write-UnityMeta $_.FullName $false
}

Push-Location $stagePackage
try {
    $previousErrorActionPreference = $ErrorActionPreference
    try {
        # Windows PowerShell promotes native stderr (including npm's informational notices) to
        # ErrorRecord objects when Stop is active. Capture the complete stream and decide by the
        # native exit code instead.
        $ErrorActionPreference = 'Continue'
        $packOutput = & npm pack --pack-destination $OutputDirectory 2>&1
        $packExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previousErrorActionPreference
    }
    if ($packExitCode -ne 0) { throw "npm pack failed: $packOutput" }
}
finally {
    Pop-Location
}

$tarballs = @(Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.tgz' -File)
if ($tarballs.Count -ne 1) { throw "Expected one Unity package tarball, found $($tarballs.Count)." }
$tarball = $tarballs[0]
$entries = @(& tar -tzf $tarball.FullName)
if ($LASTEXITCODE -ne 0) { throw 'Unity package tarball could not be inspected.' }
if ($entries -notcontains 'package/package.json' -or
    -not ($entries -contains 'package/Runtime/Plugins/Lunil.Hosting.dll')) {
    throw 'Unity package tarball is missing its manifest or portable Hosting assembly.'
}

$hash = Get-Sha256Hex $tarball.FullName
[pscustomobject]@{
    Name = $manifest.name
    Version = $Version
    MinimumUnity = $manifest.unity
    Path = $tarball.FullName
    Sha256 = $hash
    AssemblyCount = @(Get-ChildItem -LiteralPath $plugins -Filter '*.dll' -File).Count
}
