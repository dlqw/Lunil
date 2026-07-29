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
$expectedVersion = (& (Join-Path $PSScriptRoot 'Get-LunilVersion.ps1')).Trim()
$project = Join-Path $repositoryRoot 'src/Lunil.Cli/Lunil.Cli.csproj'
$utf8NoBom = New-Object System.Text.UTF8Encoding($false)
$executableName = if ($RuntimeIdentifier.StartsWith('win-', [StringComparison]::Ordinal)) {
    'lunil.exe'
}
else {
    'lunil'
}

function ConvertTo-Pem {
    param(
        [Parameter(Mandatory)]
        [string] $Label,

        [Parameter(Mandatory)]
        [byte[]] $Der
    )

    $base64 = [Convert]::ToBase64String($Der)
    $lines = for ($offset = 0; $offset -lt $base64.Length; $offset += 64) {
        $length = [Math]::Min(64, $base64.Length - $offset)
        $base64.Substring($offset, $length)
    }
    "-----BEGIN $Label-----`n$($lines -join "`n")`n-----END $Label-----`n"
}

function New-PatchSigningKeyPair {
    param(
        [Parameter(Mandatory)]
        [string] $PrivateKeyPath,

        [Parameter(Mandatory)]
        [string] $PublicKeyPath
    )

    $key = [System.Security.Cryptography.ECDsa]::Create(
        [System.Security.Cryptography.ECCurve+NamedCurves]::nistP256)
    try {
        $parameters = $key.ExportParameters($true)
        if ($parameters.D.Length -ne 32 -or
            $parameters.Q.X.Length -ne 32 -or
            $parameters.Q.Y.Length -ne 32) {
            throw 'The generated P-256 signing key has unexpected parameter lengths.'
        }

        # RFC 5915 ECPrivateKey with named-curve and public-key fields.
        $privateDer = [byte[]](@(
            0x30, 0x77,
            0x02, 0x01, 0x01,
            0x04, 0x20
        ) + $parameters.D + @(
            0xA0, 0x0A,
            0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07,
            0xA1, 0x44,
            0x03, 0x42, 0x00, 0x04
        ) + $parameters.Q.X + $parameters.Q.Y)

        # RFC 5480 SubjectPublicKeyInfo for id-ecPublicKey / prime256v1.
        $publicDer = [byte[]](@(
            0x30, 0x59,
            0x30, 0x13,
            0x06, 0x07, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x02, 0x01,
            0x06, 0x08, 0x2A, 0x86, 0x48, 0xCE, 0x3D, 0x03, 0x01, 0x07,
            0x03, 0x42, 0x00, 0x04
        ) + $parameters.Q.X + $parameters.Q.Y)

        [System.IO.File]::WriteAllText(
            $PrivateKeyPath,
            (ConvertTo-Pem -Label 'EC PRIVATE KEY' -Der $privateDer),
            $utf8NoBom)
        [System.IO.File]::WriteAllText(
            $PublicKeyPath,
            (ConvertTo-Pem -Label 'PUBLIC KEY' -Der $publicDer),
            $utf8NoBom)
    }
    finally {
        $key.Dispose()
    }
}

$requiresNativeAotRestore = $Modes -contains 'NativeAot'
if ($requiresNativeAotRestore) {
    & dotnet restore $project --runtime $RuntimeIdentifier -p:LunilNativeAotPublish=true
    if ($LASTEXITCODE -ne 0) { throw "NativeAOT CLI restore failed for $RuntimeIdentifier." }
}
else {
    & dotnet restore $project --runtime $RuntimeIdentifier
    if ($LASTEXITCODE -ne 0) { throw "CLI publish-mode restore failed for $RuntimeIdentifier." }
}

foreach ($mode in $Modes) {
    & dotnet build-server shutdown | Out-Null
    $modeName = $mode.ToLowerInvariant()
    $outputDirectory = Join-Path $repositoryRoot "artifacts/cli-publish/$RuntimeIdentifier/$modeName"
    if (Test-Path -LiteralPath $outputDirectory) {
        Remove-Item -LiteralPath $outputDirectory -Recurse -Force
    }

    $properties = switch ($mode) {
        'NativeAot' { @('-p:LunilNativeAotPublish=true', '-p:PublishTrimmed=true') }
        'SingleFileTrimmed' { @('-p:PublishAot=false', '-p:PublishTrimmed=true', '-p:PublishSingleFile=true', '-p:EnableCompressionInSingleFile=true') }
        'ReadyToRun' { @('-p:PublishAot=false', '-p:PublishTrimmed=false', '-p:PublishReadyToRun=true') }
    }
    $arguments = @(
        'publish', $project,
        '--configuration', $Configuration,
        '--framework', 'net10.0',
        '--runtime', $RuntimeIdentifier,
        '--self-contained', 'true',
        '--no-restore',
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

    [System.IO.File]::WriteAllText(
        (Join-Path $outputDirectory 'app.lua'),
        "print(os.time()); print('LUNIL_CLI_PUBLISH_OK')",
        $utf8NoBom)
    [System.IO.File]::WriteAllText(
        (Join-Path $outputDirectory 'warning.lua'),
        "return 'text' + 1",
        $utf8NoBom)
    [System.IO.File]::WriteAllText(
        (Join-Path $outputDirectory 'lunil.json'),
        '{ "profile": "deterministic", "diagnosticFormat": "json" }',
        $utf8NoBom)
    [System.IO.File]::WriteAllText(
        (Join-Path $outputDirectory 'run.rsp'),
        'run "app.lua"',
        $utf8NoBom)

    Push-Location $outputDirectory
    try {
        $versionOutput = (& $executable --version | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or
            -not [string]::Equals(
                $versionOutput,
                $expectedVersion,
                [StringComparison]::Ordinal)) {
            throw "$mode CLI version smoke failed: $versionOutput"
        }

        $runOutput = ((& $executable '@run.rsp' | Out-String).Trim() -replace "`r`n", "`n")
        if ($LASTEXITCODE -ne 0 -or $runOutput -ne "0`nLUNIL_CLI_PUBLISH_OK") {
            throw "$mode CLI response/config/run smoke failed: $runOutput"
        }

        $previousErrorActionPreference = $ErrorActionPreference
        try {
            $ErrorActionPreference = 'Continue'
            $diagnostics = (& $executable check warning.lua --warnings-as-errors 2>&1 |
                ForEach-Object { $_.ToString() } |
                Out-String)
            $diagnosticsExitCode = $LASTEXITCODE
        }
        finally {
            $ErrorActionPreference = $previousErrorActionPreference
        }
        if ($diagnosticsExitCode -ne 1 -or $diagnostics -notmatch '"schema"\s*:\s*"lunil.diagnostics.v1"') {
            throw "$mode CLI JSON diagnostics smoke failed: $diagnostics"
        }

        & $executable build app.lua --output app.luac | Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath 'app.luac' -PathType Leaf)) {
            throw "$mode CLI build smoke failed."
        }

        New-Item -ItemType Directory -Path 'patch-payload' -Force | Out-Null
        [System.IO.File]::WriteAllText(
            (Join-Path $outputDirectory 'patch-payload/main.lua'),
            'return 42',
            $utf8NoBom)
        [System.IO.File]::WriteAllText(
            (Join-Path $outputDirectory 'patch-manifest.json'),
            @'
{
  "formatVersion": 1,
  "patchId": "publish-smoke",
  "channel": "ci",
  "targetBuild": "publish-smoke",
  "baseRevision": "1",
  "targetRevision": "2",
  "updateIntent": "Forward",
  "languageVersion": 84,
  "runtimeAbi": "lunil-0.12",
  "createdAt": "2026-01-01T00:00:00Z",
  "expiresAt": "2099-01-01T00:00:00Z",
  "nonce": "publish-smoke-1",
  "requiredCapabilities": [],
  "requiredTargetLabels": [],
  "entries": [
    {
      "name": "main.lua",
      "moduleName": "main",
      "kind": "Source",
      "contentHash": "0000000000000000000000000000000000000000000000000000000000000000",
      "length": 0,
      "dependencies": []
    }
  ]
}
'@,
            $utf8NoBom)
        New-PatchSigningKeyPair `
            -PrivateKeyPath (Join-Path $outputDirectory 'patch-private.pem') `
            -PublicKeyPath (Join-Path $outputDirectory 'patch-public.pem')
        [System.IO.File]::WriteAllText(
            (Join-Path $outputDirectory 'patch-trust.json'),
            @'
{
  "schema": "lunil.patch-trust.v1",
  "keys": [
    {
      "keyId": "publish-smoke",
      "publicKey": "patch-public.pem",
      "validFrom": "2020-01-01T00:00:00Z",
      "validUntil": "2099-01-01T00:00:00Z"
    }
  ]
}
'@,
            $utf8NoBom)
        & $executable patch pack patch-manifest.json patch-payload `
            --output patch.lpatch --private-key patch-private.pem --key-id publish-smoke |
            Out-Null
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath 'patch.lpatch' -PathType Leaf)) {
            throw "$mode CLI patch pack smoke failed."
        }

        $patchOutput = (& $executable patch verify patch.lpatch `
            --trust-store patch-trust.json | Out-String).Trim()
        if ($LASTEXITCODE -ne 0 -or $patchOutput -ne 'verified publish-smoke 2') {
            throw "$mode CLI trust-store verification smoke failed: $patchOutput"
        }
    }
    finally {
        Pop-Location
    }

    Write-Output "LUNIL_CLI_PUBLISH_OK $mode $RuntimeIdentifier"
}
