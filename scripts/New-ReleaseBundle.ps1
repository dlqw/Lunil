[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,

    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$')]
    [string] $Version,

    [ValidateSet('Debug', 'Release')]
    [string] $Configuration = 'Release',

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts'
}
else {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}

$changelog = Join-Path $repositoryRoot "changelogs/$Version.md"
if (-not (Test-Path -LiteralPath $changelog -PathType Leaf)) {
    throw "Missing version changelog: $changelog"
}

$solution = Join-Path $repositoryRoot 'Lunil.sln'
& dotnet restore $solution --runtime $RuntimeIdentifier
if ($LASTEXITCODE -ne 0) { throw 'dotnet restore failed.' }

$projects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -Filter 'Lunil.*.csproj' |
    Sort-Object FullName
foreach ($project in $projects) {
    & dotnet build $project.FullName --configuration $Configuration --runtime $RuntimeIdentifier --no-restore `
        -p:Version=$Version -p:ContinuousIntegrationBuild=true
    if ($LASTEXITCODE -ne 0) { throw "dotnet build failed for $($project.Name)." }
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$bundleName = "lunil-$Version-$RuntimeIdentifier"
$stagingRoot = Join-Path $OutputDirectory "staging-$bundleName-$([Guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $stagingRoot | Out-Null

foreach ($project in $projects) {
    $projectName = [System.IO.Path]::GetFileNameWithoutExtension($project.Name)
    $buildDirectory = Join-Path $project.Directory.FullName "bin/$Configuration/net10.0/$RuntimeIdentifier"
    foreach ($extension in @('dll', 'pdb', 'xml')) {
        $source = Join-Path $buildDirectory "$projectName.$extension"
        if (Test-Path -LiteralPath $source -PathType Leaf) {
            Copy-Item -LiteralPath $source -Destination $stagingRoot
        }
    }
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'README.md') -Destination $stagingRoot
Copy-Item -LiteralPath $changelog -Destination (Join-Path $stagingRoot 'CHANGELOG.md')

$archive = Join-Path $OutputDirectory "$bundleName.zip"
if (Test-Path -LiteralPath $archive) {
    throw "Refusing to overwrite existing archive: $archive"
}

Compress-Archive -Path (Join-Path $stagingRoot '*') -DestinationPath $archive -CompressionLevel Optimal
Write-Output $archive
