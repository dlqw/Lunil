[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidatePattern('^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$')]
    [string] $Version,

    [string] $PackageDirectory
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot 'artifacts/packages'
}
else {
    $PackageDirectory = [System.IO.Path]::GetFullPath($PackageDirectory)
}

$package = Join-Path $PackageDirectory "Lunil.Cli.$Version.nupkg"
if (-not (Test-Path -LiteralPath $package -PathType Leaf)) {
    throw "CLI tool package was not produced: $package"
}

$smokeRoot = Join-Path $repositoryRoot 'artifacts/cli-package-smoke'
if (Test-Path -LiteralPath $smokeRoot) {
    Remove-Item -LiteralPath $smokeRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $smokeRoot | Out-Null

Push-Location $smokeRoot
try {
    & dotnet new tool-manifest --force | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'dotnet new tool-manifest failed.' }

    & dotnet tool install Lunil.Cli --version $Version --add-source $PackageDirectory --ignore-failed-sources
    if ($LASTEXITCODE -ne 0) { throw 'Installing the Lunil.Cli tool package failed.' }

    $reportedVersion = (& dotnet tool run lunil --version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $reportedVersion -ne $Version) {
        throw "Installed CLI reported '$reportedVersion' instead of '$Version'."
    }

    Set-Content -LiteralPath 'app.lua' -Encoding utf8NoBOM -Value "print('LUNIL_CLI_PACKAGE_OK')"
    $runOutput = (& dotnet tool run lunil run app.lua --deterministic | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $runOutput -ne 'LUNIL_CLI_PACKAGE_OK') {
        throw "Installed CLI source execution failed: $runOutput"
    }

    & dotnet tool run lunil build app.lua --output app.luac | Out-Null
    if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath 'app.luac' -PathType Leaf)) {
        throw 'Installed CLI chunk build failed.'
    }

    $chunkOutput = (& dotnet tool run lunil run app.luac | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or $chunkOutput -ne 'LUNIL_CLI_PACKAGE_OK') {
        throw "Installed CLI chunk execution failed: $chunkOutput"
    }

    $dump = (& dotnet tool run lunil dump app.lua --kind summary --format json | Out-String)
    if ($LASTEXITCODE -ne 0 -or $dump -notmatch '"schema"\s*:\s*"lunil.dump.v1"') {
        throw 'Installed CLI JSON dump failed.'
    }
}
finally {
    Pop-Location
}

Write-Output 'LUNIL_CLI_PACKAGE_OK'
