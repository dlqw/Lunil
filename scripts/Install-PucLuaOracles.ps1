[CmdletBinding()]
param(
    [string] $OutputDirectory = 'artifacts/puc-oracles'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($IsWindows) {
    throw 'Pinned PUC Lua oracle installation currently supports Unix CI runners only.'
}

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$outputRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
New-Item -ItemType Directory -Force $outputRoot | Out-Null

$specifications = @(
    [pscustomobject]@{
        Label = '51'; Version = '5.1.5'
        Sha256 = '2640FC56A795F29D28EF15E13C34A47E223960B0240E8CB0A82D9B0738695333'
    },
    [pscustomobject]@{
        Label = '52'; Version = '5.2.4'
        Sha256 = 'B9E2E4AAD6789B3B63A056D442F7B39F0ECFCA3AE0F1FC0AE4E9614401B69F4B'
    },
    [pscustomobject]@{
        Label = '53'; Version = '5.3.6'
        Sha256 = 'FC5FD69BB8736323F026672B1B7235DA613D7177E72558893A0BDCD320466D60'
    },
    [pscustomobject]@{
        Label = '54'; Version = '5.4.8'
        Sha256 = '4F18DDAE154E793E46EEAB727C59EF1C0C0C2B744E7B94219710D76F530629AE'
    },
    [pscustomobject]@{
        Label = '55'; Version = '5.5.0'
        Sha256 = '57CCC32BBBD005CAB75BCC52444052535AF691789DBA2B9016D5C50640D68B3D'
    }
)

foreach ($tool in 'tar', 'make', 'cc') {
    if ($null -eq (Get-Command $tool -ErrorAction SilentlyContinue)) {
        throw "Required PUC Lua build tool '$tool' is unavailable."
    }
}

$installed = [ordered]@{}
foreach ($specification in $specifications) {
    $archiveName = "lua-$($specification.Version).tar.gz"
    $archivePath = Join-Path $outputRoot $archiveName
    $sourceDirectory = Join-Path $outputRoot "lua-$($specification.Version)"
    $luaPath = Join-Path $sourceDirectory 'src/lua'
    $luacPath = Join-Path $sourceDirectory 'src/luac'

    if (-not (Test-Path -LiteralPath $archivePath)) {
        Invoke-WebRequest `
            -Uri "https://www.lua.org/ftp/$archiveName" `
            -OutFile $archivePath
    }
    $actualSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
    if ($actualSha256 -ne $specification.Sha256) {
        throw "SHA-256 mismatch for ${archiveName}: expected $($specification.Sha256), got $actualSha256."
    }

    if (-not (Test-Path -LiteralPath $luaPath) -or
        -not (Test-Path -LiteralPath $luacPath)) {
        if (Test-Path -LiteralPath $sourceDirectory) {
            Remove-Item -LiteralPath $sourceDirectory -Recurse -Force
        }
        & tar -xzf $archivePath -C $outputRoot
        if ($LASTEXITCODE -ne 0) {
            throw "Could not extract $archiveName."
        }
        & make -C $sourceDirectory -j2 generic
        if ($LASTEXITCODE -ne 0) {
            throw "Could not build PUC Lua $($specification.Version)."
        }
    }

    $versionText = (& $luaPath -v 2>&1 | Out-String).Trim()
    if ($LASTEXITCODE -ne 0 -or
        $versionText -notmatch [Regex]::Escape("Lua $($specification.Version)")) {
        throw "PUC Lua oracle failed its version check: $luaPath ($versionText)."
    }

    $luaVariable = "LUNIL_PUC_LUA$($specification.Label)"
    $luacVariable = "LUNIL_PUC_LUAC$($specification.Label)"
    $installed[$luaVariable] = [System.IO.Path]::GetFullPath($luaPath)
    $installed[$luacVariable] = [System.IO.Path]::GetFullPath($luacPath)
}

if (-not [string]::IsNullOrWhiteSpace($env:GITHUB_ENV)) {
    foreach ($entry in $installed.GetEnumerator()) {
        "$($entry.Key)=$($entry.Value)" >> $env:GITHUB_ENV
    }
}

[pscustomobject]$installed
