[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,

    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot 'tests/Lunil.NativeAot.Fixture/Lunil.NativeAot.Fixture.csproj'
$executableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)) {
    'Lunil.NativeAot.Fixture.exe'
}
else {
    'Lunil.NativeAot.Fixture'
}

$modes = @(
    @{
        Name = 'single-file-trimmed'
        Properties = @('-p:PublishAot=false', '-p:PublishTrimmed=true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true')
    },
    @{
        Name = 'ready-to-run'
        Properties = @('-p:PublishAot=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=true')
    }
)

foreach ($mode in $modes) {
    & dotnet build-server shutdown | Out-Null
    $outputDirectory = Join-Path $repositoryRoot "artifacts/publish/$RuntimeIdentifier/$($mode.Name)"
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }

    $arguments = @(
        'publish', $project,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '-p:ContinuousIntegrationBuild=true',
        '-p:TreatWarningsAsErrors=true',
        '--output', $outputDirectory
    ) + $mode.Properties
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$($mode.Name) publish failed for $RuntimeIdentifier." }

    $executable = Join-Path $outputDirectory $executableName
    Push-Location $outputDirectory
    try {
        $output = & $executable
        if ($LASTEXITCODE -ne 0) {
            throw "$($mode.Name) fixture exited with $LASTEXITCODE for $RuntimeIdentifier."
        }
    }
    finally {
        Pop-Location
    }

    if ($output -notcontains 'LUNIL_NATIVEAOT_OK') {
        throw "$($mode.Name) fixture did not report conformance success for $RuntimeIdentifier."
    }
}
