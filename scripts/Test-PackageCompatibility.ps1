[CmdletBinding()]
param(
    [string] $Version = '',

    [string] $PackageDirectory = '',

    [switch] $NoPack,

    [switch] $Update
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compatibilityLine = (& (Join-Path $PSScriptRoot 'Get-LunilCompatibilityLine.ps1')).Trim()
$baselinePath = Join-Path $repositoryRoot "api/$compatibilityLine/packages.json"
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (& (Join-Path $PSScriptRoot 'Get-LunilVersion.ps1')).Trim()
}
$escapedCompatibilityLine = [Regex]::Escape($compatibilityLine)
if ($Version -notmatch "^$escapedCompatibilityLine(?:-[0-9A-Za-z.-]+)?$") {
    throw "Package compatibility validation only accepts the active $compatibilityLine line: $Version"
}
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot 'artifacts/package-compatibility/packages'
}
$packageRoot = [System.IO.Path]::GetFullPath($PackageDirectory)

function ConvertTo-NormalizedText([string] $Text) {
    $normalized = $Text.Replace("`r`n", "`n").Replace("`r", "`n")
    if (-not $normalized.EndsWith("`n", [StringComparison]::Ordinal)) {
        $normalized += "`n"
    }

    return $normalized
}

function Write-NormalizedText([string] $Path, [string] $Text) {
    New-Item -ItemType Directory -Path ([System.IO.Path]::GetDirectoryName($Path)) -Force |
        Out-Null
    [System.IO.File]::WriteAllText(
        $Path,
        (ConvertTo-NormalizedText $Text),
        [System.Text.UTF8Encoding]::new($false))
}

function Get-NormalizedEntryPath([string] $Path) {
    if ($Path -match '^package/services/metadata/core-properties/[^/]+\.psmdcp$') {
        return 'package/services/metadata/core-properties/<generated>.psmdcp'
    }

    return $Path
}

function Get-PackageDocument([string] $Path) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        $nuspecEntries = @($archive.Entries | Where-Object FullName -Like '*.nuspec')
        if ($nuspecEntries.Count -ne 1) {
            throw "Expected exactly one nuspec in $Path, found $($nuspecEntries.Count)."
        }
        $reader = [System.IO.StreamReader]::new($nuspecEntries[0].Open())
        try {
            [xml] $nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }

        $metadata = $nuspec.package.metadata
        $id = [string] $metadata.id
        $license = if ($metadata.license -is [System.Xml.XmlNode]) {
            $metadata.license.InnerText
        }
        else {
            [string] $metadata.license
        }
        $actualVersion = [string] $metadata.version
        if (-not [string]::Equals($actualVersion, $Version, [StringComparison]::Ordinal)) {
            throw "Package $id has version $actualVersion; expected $Version."
        }
        if ([string]$metadata.authors -ne 'rdququ' -or
            $license -ne 'MIT' -or
            [string]$metadata.readme -ne 'README.md' -or
            [string]$metadata.projectUrl -ne 'https://github.com/dlqw/Lunil') {
            throw "Package $id does not satisfy the frozen author/license/readme/project metadata."
        }
        if ([string]$metadata.repository.url -ne 'https://github.com/dlqw/Lunil' -or
            [string]$metadata.repository.type -ne 'git') {
            throw "Package $id does not identify the canonical Git repository."
        }

        $dependencyGroups = [Collections.Generic.List[object]]::new()
        foreach ($group in @($metadata.SelectNodes(
            "*[local-name()='dependencies']/*[local-name()='group']"))) {
            $dependencies = [Collections.Generic.List[object]]::new()
            foreach ($dependency in @($group.SelectNodes("*[local-name()='dependency']") |
                Sort-Object id)) {
                if ([string]$dependency.version -ne $Version) {
                    throw "Package $id dependency $($dependency.id) is $($dependency.version); expected $Version."
                }
                $dependencies.Add([ordered]@{
                    Id = [string] $dependency.id
                    Version = '{version}'
                    Exclude = [string] $dependency.exclude
                })
            }
            $dependencyGroups.Add([ordered]@{
                TargetFramework = [string] $group.targetFramework
                Dependencies = $dependencies.ToArray()
            })
        }

        $packageTypes = @($metadata.SelectNodes(
            "*[local-name()='packageTypes']/*[local-name()='packageType']") |
            ForEach-Object { [string] $_.name } | Sort-Object)
        $assets = @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
            ForEach-Object { Get-NormalizedEntryPath $_.FullName } |
            Sort-Object -Unique)
        return [ordered]@{
            Id = $id
            Authors = [string] $metadata.authors
            License = $license
            Readme = [string] $metadata.readme
            ProjectUrl = [string] $metadata.projectUrl
            Description = [string] $metadata.description
            Tags = [string] $metadata.tags
            Repository = 'https://github.com/dlqw/Lunil'
            PackageTypes = $packageTypes
            DependencyGroups = $dependencyGroups.ToArray()
            Assets = $assets
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Get-SymbolAssets([string] $Path) {
    $archive = [System.IO.Compression.ZipFile]::OpenRead($Path)
    try {
        return @($archive.Entries | Where-Object { -not [string]::IsNullOrEmpty($_.Name) } |
            ForEach-Object { Get-NormalizedEntryPath $_.FullName } |
            Sort-Object -Unique)
    }
    finally {
        $archive.Dispose()
    }
}

function Invoke-ConsumerSmoke([object[]] $Packages) {
    $consumerRoot = [System.IO.Path]::GetFullPath(
        (Join-Path $repositoryRoot 'artifacts/package-compatibility/consumer'))
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
    if (-not $consumerRoot.StartsWith(
        $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Package consumer directory escaped the artifacts root: $consumerRoot"
    }
    if (Test-Path -LiteralPath $consumerRoot) {
        Remove-Item -LiteralPath $consumerRoot -Recurse -Force
    }
    New-Item -ItemType Directory -Path $consumerRoot -Force | Out-Null

    $references = @($Packages | Where-Object Id -NE 'Lunil.Cli' | ForEach-Object {
        "    <PackageReference Include=`"$($_.Id)`" Version=`"$Version`" />"
    }) -join "`n"
    $project = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
    <NuGetAudit>false</NuGetAudit>
  </PropertyGroup>
  <ItemGroup>
$references
  </ItemGroup>
</Project>
"@
    Write-NormalizedText (Join-Path $consumerRoot 'PackageConsumer.csproj') $project
    Write-NormalizedText (Join-Path $consumerRoot 'Program.cs') @'
using Lunil.Hosting;

using var host = new LuaHost(LuaHostOptions.Deterministic);
var result = host.RunUtf8("return 40 + 2", "=package-consumer");
return result.Succeeded ? 0 : 1;
'@
    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-lunil" value="$($packageRoot.Replace('&', '&amp;'))" />
  </packageSources>
</configuration>
"@
    Write-NormalizedText (Join-Path $consumerRoot 'NuGet.Config') $nugetConfig

    & dotnet restore (Join-Path $consumerRoot 'PackageConsumer.csproj') `
        --configfile (Join-Path $consumerRoot 'NuGet.Config')
    if ($LASTEXITCODE -ne 0) { throw 'Restoring the all-packages consumer failed.' }
    & dotnet run --project (Join-Path $consumerRoot 'PackageConsumer.csproj') `
        --configuration Release --no-restore
    if ($LASTEXITCODE -ne 0) { throw 'Running the all-packages consumer failed.' }

    $toolPath = Join-Path $consumerRoot 'tools'
    & dotnet tool install Lunil.Cli --tool-path $toolPath --version $Version `
        --add-source $packageRoot --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Installing the frozen Lunil.Cli package failed.' }
    $toolExecutable = if ($IsWindows) {
        Join-Path $toolPath 'lunil.exe'
    }
    else {
        Join-Path $toolPath 'lunil'
    }
    $reportedVersion = (& $toolExecutable --version).Trim()
    if ($LASTEXITCODE -ne 0 -or $reportedVersion -ne $Version) {
        throw "Installed CLI reported '$reportedVersion'; expected '$Version'."
    }
    $sample = Join-Path $consumerRoot 'sample.lua'
    Write-NormalizedText $sample "print('package-compat-ok')"
    $output = (& $toolExecutable run $sample --deterministic).Trim()
    if ($LASTEXITCODE -ne 0 -or $output -ne 'package-compat-ok') {
        throw "Installed CLI package smoke failed: $output"
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
if (-not $NoPack) {
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
    if (-not $packageRoot.StartsWith(
        $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean a package directory outside artifacts: $packageRoot"
    }
    if (Test-Path -LiteralPath $packageRoot) {
        Remove-Item -LiteralPath $packageRoot -Recurse -Force
    }
    & (Join-Path $PSScriptRoot 'New-NuGetPackages.ps1') `
        -Version $Version -OutputDirectory $packageRoot
    if ($LASTEXITCODE -ne 0) { throw 'Building packages for compatibility validation failed.' }
}

$nupkgs = @(Get-ChildItem -LiteralPath $packageRoot -File -Filter "*.$Version.nupkg" |
    Sort-Object Name)
$snupkgs = @(Get-ChildItem -LiteralPath $packageRoot -File -Filter "*.$Version.snupkg" |
    Sort-Object Name)
if ($nupkgs.Count -ne 13 -or $snupkgs.Count -ne 13) {
    throw "Expected 13 NuGet and 13 symbol packages for $Version; found $($nupkgs.Count) and $($snupkgs.Count)."
}

$packages = [Collections.Generic.List[object]]::new()
foreach ($nupkg in $nupkgs) {
    $package = Get-PackageDocument $nupkg.FullName
    $symbolPath = Join-Path $packageRoot "$($package.Id).$Version.snupkg"
    if (-not (Test-Path -LiteralPath $symbolPath -PathType Leaf)) {
        throw "Missing symbol package for $($package.Id)."
    }
    $package['SymbolAssets'] = Get-SymbolAssets $symbolPath
    $packages.Add($package)
}

$manifest = [ordered]@{
    SchemaVersion = 1
    CompatibilityLine = $compatibilityLine
    PackageCount = $packages.Count
    VersionToken = '{version}'
    Packages = $packages.ToArray()
}
$manifestText = ConvertTo-NormalizedText ($manifest | ConvertTo-Json -Depth 10)
if ($Update) {
    Write-NormalizedText $baselinePath $manifestText
    Write-Host "Updated the reviewed 13-package baseline at $baselinePath."
}
else {
    if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
        throw "Missing package baseline $baselinePath. Run scripts/Update-PackageBaseline.ps1."
    }
    $expected = ConvertTo-NormalizedText ([System.IO.File]::ReadAllText($baselinePath))
    if (-not [string]::Equals($expected, $manifestText, [StringComparison]::Ordinal)) {
        throw "NuGet package metadata, dependencies, or assets differ from api/$compatibilityLine/packages.json."
    }
    Write-Host "Verified the reviewed 13-package metadata, dependency, and asset baseline for $compatibilityLine."
}

Invoke-ConsumerSmoke $packages.ToArray()
Write-Host 'All library packages and the Lunil.Cli tool passed local-package consumer smoke tests.'
