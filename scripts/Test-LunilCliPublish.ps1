[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('win-x64', 'win-arm64', 'linux-x64', 'linux-arm64', 'osx-x64', 'osx-arm64')]
    [string] $RuntimeIdentifier,

    [Parameter(Mandatory)]
    [ValidateSet('NativeAot', 'SingleFileTrimmed', 'ReadyToRun')]
    [string[]] $Modes,

    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repositoryRoot 'src/Lunil.Cli/Lunil.Cli.csproj'
$executableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)) {
    'lunil.exe'
}
else {
    'lunil'
}

foreach ($mode in $Modes) {
    & dotnet build-server shutdown | Out-Null
    $modeName = $mode.ToLowerInvariant()
    $outputDirectory = Join-Path $repositoryRoot "artifacts/cli-publish/$RuntimeIdentifier/$modeName"
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }

    $properties = switch ($mode) {
        'NativeAot' { @('-p:PublishAot=true', '-p:PublishTrimmed=true') }
        'SingleFileTrimmed' { @('-p:PublishAot=false', '-p:PublishTrimmed=true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true') }
        'ReadyToRun' { @('-p:PublishAot=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=true') }
    }
    $arguments = @(
        'publish', $project,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '-p:ContinuousIntegrationBuild=true',
        '-p:TreatWarningsAsErrors=true',
        '--output', $outputDirectory
    ) + $properties
    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) { throw "$mode CLI publish failed for $RuntimeIdentifier." }

    $executable = Join-Path $outputDirectory $executableName
    if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
        throw "$mode CLI executable was not produced: $executable"
    }

    Set-Content -LiteralPath (Join-Path $outputDirectory 'app.lua') -Encoding utf8NoBOM -Value "print(os.time()); print('LUNIL_CLI_PUBLISH_OK')"
    Set-Content -LiteralPath (Join-Path $outputDirectory 'warning.lua') -Encoding utf8NoBOM -Value "return 'text' + 1"
    Set-Content -LiteralPath (Join-Path $outputDirectory 'lunil.json') -Encoding utf8NoBOM -Value '{ "profile": "deterministic", "diagnosticFormat": "json" }'
    Set-Content -LiteralPath (Join-Path $outputDirectory 'run.rsp') -Encoding utf8NoBOM -Value 'run "app.lua"'

    Push-Location $outputDirectory
    try {
        $versionOutput = (& $executable --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $versionOutput -notmatch '^0\.7\.0(?:-|$)') {
            throw "$mode CLI version smoke failed: $versionOutput"
        }

        $runOutput = ((& $executable '@run.rsp' | Out-String).Trim() -replace "`r`n", "`n")
        if ($LASTEXITCODE -ne 0 -or $runOutput -ne "0`nLUNIL_CLI_PUBLISH_OK") {
            throw "$mode CLI response/config/run smoke failed: $runOutput"
        }

        $diagnostics = (& $executable check warning.lua --warnings-as-errors 2>&1 | Out-String)
        if ($LASTEXITCODE -ne 1 -or $diagnostics -notmatch '"schema"\s*:\s*"lunil.diagnostics.v1"') {
            throw "$mode CLI JSON diagnostics smoke failed: $diagnostics"
        }

        & $executable build app.lua --output app.luac | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath 'app.luac' -PathType Leaf)) {
            throw "$mode CLI build smoke failed."
        }
    }
    finally {
        Pop-Location
    }

    Write-Output "LUNIL_CLI_PUBLISH_OK $mode $RuntimeIdentifier"
}
