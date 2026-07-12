[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,

    [string] $Configuration = 'Release',

    [string] $OutputDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot 'tests/Lunil.NativeAot.Fixture/Lunil.NativeAot.Fixture.csproj'
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/nativeaot/$RuntimeIdentifier"
}
else {
    $OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
}

& dotnet build-server shutdown | Out-Null
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}

& dotnet publish $project --configuration $Configuration `
    --runtime $RuntimeIdentifier --self-contained true `
    -p:PublishAot=true -p:PublishTrimmed=true `
    -p:ContinuousIntegrationBuild=true -p:TreatWarningsAsErrors=true `
    --output $OutputDirectory
if ($LASTEXITCODE -ne 0) { throw "NativeAOT publish failed for $RuntimeIdentifier." }

$executableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)) {
    'Lunil.NativeAot.Fixture.exe'
}
else {
    'Lunil.NativeAot.Fixture'
}
$executable = Join-Path $OutputDirectory $executableName
if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "NativeAOT executable was not produced: $executable"
}

Push-Location $OutputDirectory
try {
    $output = & $executable
    if ($LASTEXITCODE -ne 0) {
        throw "NativeAOT fixture exited with $LASTEXITCODE for $RuntimeIdentifier."
    }
}
finally {
    Pop-Location
}

if ($output -notcontains 'LUNIL_NATIVEAOT_OK') {
    throw "NativeAOT fixture did not report conformance success for $RuntimeIdentifier."
}

$output
