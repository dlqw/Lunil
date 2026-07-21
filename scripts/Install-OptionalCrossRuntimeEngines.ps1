[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $RuntimeIdentifier,
    [switch] $Force,
    [switch] $SkipGo,
    [switch] $SkipWasmoon
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $platform = if ($IsWindows) { "win" } elseif ($IsMacOS) { "osx" } else { "linux" }
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $RuntimeIdentifier = "$platform-$architecture"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/cross-runtime-tools/$RuntimeIdentifier/optional"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null

function Get-VerifiedArchive(
    [string] $Uri,
    [string] $Destination,
    [string] $ExpectedSha256
) {
    if (-not (Test-Path -LiteralPath $Destination) -or $Force) {
        Write-Host "Downloading $Uri"
        Invoke-WebRequest -Uri $Uri -OutFile $Destination
    }
    $actual = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if ($actual -ne $ExpectedSha256) {
        throw "Archive integrity check failed for $Uri. Expected $ExpectedSha256, got $actual."
    }
    return $actual
}

# Pins (source identity for public datasets)
$luauVersion = "0.623"
$luauUri = "https://github.com/luau-lang/luau/releases/download/$luauVersion/luau-windows.zip"
$luauArchiveSha256 = "41C7F03EE530C4EB7DDB40F7E4048B3425ABB2D8EEBC0C2B4151D81AFCD259AA"
$gopherVersion = "1.1.1"
$gopherUri = "https://github.com/yuin/gopher-lua/archive/refs/tags/v$gopherVersion.zip"
$gopherArchiveSha256 = "8F4CEB4AB0F820B045B7335B84D841102933D72E74ABD39AC023B7A424E27D2A"
$uniCommit = "194eb311191111bfdbc77070de67c100235dc618"
$uniUri = "https://github.com/xebecnan/UniLua/archive/$uniCommit.zip"
$uniArchiveSha256 = "C00F5CF42D9DFE1CF6C00AFD90A3F58EC05D8B82939858B3954F07F3619049D7"
$wasmoonVersion = "1.16.0"
$neoLuaVersion = "1.3.19"
$goVersion = "1.22.12"
$goArchiveSha256 = "2CEDA04074EAC51F4B0B85A9FCCA38BCD49DAEE24BED9EA1F29958A8E22673A6"

$luauZip = Join-Path $OutputDirectory "luau-windows-$luauVersion.zip"
$luauSha = Get-VerifiedArchive $luauUri $luauZip $luauArchiveSha256
$luauDir = Join-Path $OutputDirectory "luau-$luauVersion"
if ($Force -or -not (Test-Path (Join-Path $luauDir "luau.exe"))) {
    if (Test-Path $luauDir) { Remove-Item $luauDir -Recurse -Force }
    Expand-Archive -LiteralPath $luauZip -DestinationPath $luauDir -Force
}
$luauExe = (Get-ChildItem -Path $luauDir -Recurse -Filter "luau.exe" | Select-Object -First 1).FullName

$glZip = Join-Path $OutputDirectory "gopher-lua-$gopherVersion.zip"
$glSha = Get-VerifiedArchive $gopherUri $glZip $gopherArchiveSha256
$glDir = Join-Path $OutputDirectory "gopher-lua-$gopherVersion"
if ($Force -or -not (Test-Path $glDir)) {
    if (Test-Path $glDir) { Remove-Item $glDir -Recurse -Force }
    Expand-Archive -LiteralPath $glZip -DestinationPath $OutputDirectory -Force
    $extracted = Get-ChildItem $OutputDirectory -Directory | Where-Object { $_.Name -like "gopher-lua-*" -and $_.Name -ne "gopher-lua-$gopherVersion" } | Select-Object -First 1
    if ($extracted) {
        Rename-Item $extracted.FullName "gopher-lua-$gopherVersion"
    }
}

$uniZip = Join-Path $OutputDirectory "unilua-$($uniCommit.Substring(0,8)).zip"
$uniSha = Get-VerifiedArchive $uniUri $uniZip $uniArchiveSha256
$uniDir = Join-Path $OutputDirectory "UniLua-$($uniCommit.Substring(0,8))"
if ($Force -or -not (Test-Path $uniDir)) {
    Expand-Archive -LiteralPath $uniZip -DestinationPath $OutputDirectory -Force
    $extracted = Get-ChildItem $OutputDirectory -Directory | Where-Object { $_.Name -like "UniLua-$($uniCommit.Substring(0,8))*" -or $_.Name -eq "UniLua-$uniCommit" } | Select-Object -First 1
    if ($extracted -and $extracted.FullName -ne $uniDir) {
        if (Test-Path $uniDir) { Remove-Item $uniDir -Recurse -Force }
        Rename-Item $extracted.FullName (Split-Path $uniDir -Leaf)
    }
}

if (-not $SkipWasmoon) {
    $wasmoonDir = Join-Path $OutputDirectory "wasmoon-$wasmoonVersion"
    New-Item -ItemType Directory -Force -Path $wasmoonDir | Out-Null
    Push-Location $wasmoonDir
    try {
        if (-not (Test-Path "package.json")) { npm init -y | Out-Null }
        if ($Force -or -not (Test-Path "node_modules/wasmoon")) {
            npm install "wasmoon@$wasmoonVersion" --no-fund --no-audit
        }
    } finally {
        Pop-Location
    }
}

$hostsRoot = Join-Path $repositoryRoot "benchmarks/Lunil.OptionalEngineHosts"
$gopherHost = Join-Path $hostsRoot "gopherlua-host.exe"
if (-not $SkipGo -and -not (Test-Path $gopherHost)) {
    $goUri = "https://go.dev/dl/go$goVersion.windows-amd64.zip"
    $goZip = Join-Path $OutputDirectory "go$goVersion.windows-amd64.zip"
    $null = Get-VerifiedArchive $goUri $goZip $goArchiveSha256
    $goRoot = Join-Path $OutputDirectory "go"
    if (-not (Test-Path (Join-Path $goRoot "bin/go.exe"))) {
        Expand-Archive -LiteralPath $goZip -DestinationPath $OutputDirectory -Force
    }
    $go = Join-Path $goRoot "bin/go.exe"
    Push-Location (Join-Path $hostsRoot "gopherlua")
    try {
        & $go mod tidy
        & $go build -o $gopherHost .
    } finally {
        Pop-Location
    }
}

dotnet build (Join-Path $repositoryRoot "benchmarks/Lunil.NeoLua.Harness/Lunil.NeoLua.Harness.csproj") -c Release | Out-Host
dotnet build (Join-Path $repositoryRoot "benchmarks/Lunil.UniLua.Harness/Lunil.UniLua.Harness.csproj") -c Release | Out-Host

$manifest = [ordered]@{
    schemaVersion = 1
    rid = $RuntimeIdentifier
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString("O")
    engines = [ordered]@{
        neolua = [ordered]@{ version = $neoLuaVersion; source = "NuGet NeoLua"; note = "net8 out-of-process harness" }
        luau = [ordered]@{ version = $luauVersion; sourceUri = $luauUri; archiveSha256 = $luauSha; executable = $luauExe }
        gopherlua = [ordered]@{ version = $gopherVersion; sourceUri = $gopherUri; archiveSha256 = $glSha; hostExecutable = $gopherHost }
        wasmoon = [ordered]@{ version = $wasmoonVersion; source = "npm wasmoon@$wasmoonVersion" }
        unilua = [ordered]@{ version = $uniCommit; sourceUri = $uniUri; archiveSha256 = $uniSha; sourceDirectory = $uniDir }
    }
}
$manifestPath = Join-Path $OutputDirectory "optional-engines.json"
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Optional engines provisioned at $OutputDirectory"
Get-Content -Raw -LiteralPath $manifestPath
