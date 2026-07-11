[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$')]
    [string] $Version,

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts/packages'
}
else {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}

$changelog = Join-Path $repositoryRoot "changelogs/$Version.md"
if (-not (Test-Path -LiteralPath $changelog -PathType Leaf)) {
    throw "Missing version changelog: $changelog"
}

New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$projects = Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') -Recurse -Filter 'Lunil.*.csproj' |
    Sort-Object FullName
foreach ($project in $projects) {
    & dotnet pack $project.FullName --configuration Release `
        -p:Version=$Version -p:ContinuousIntegrationBuild=true `
        --include-symbols -p:SymbolPackageFormat=snupkg --output $OutputDirectory
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack failed for $($project.Name)." }
}

Get-ChildItem -LiteralPath $OutputDirectory -File | Sort-Object Name | Select-Object -ExpandProperty FullName
