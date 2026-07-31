[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [switch] $SkipNodeInstall,
    [switch] $SkipExtensionCheck
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$extensionRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'editors/vscode'))
$serverRoot = [IO.Path]::GetFullPath((Join-Path $extensionRoot 'server'))
$project = Join-Path $repositoryRoot 'src/Lunil.LanguageServer/Lunil.LanguageServer.csproj'
$version = (& (Join-Path $repositoryRoot 'scripts/Get-LunilVersion.ps1')).Trim()
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/vscode/$version"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)

$package = Get-Content -Raw (Join-Path $extensionRoot 'package.json') | ConvertFrom-Json
if ($package.version -ne $version) {
    throw "VS Code extension version $($package.version) does not match repository version $version."
}
if (-not (Test-Path -LiteralPath (Join-Path $extensionRoot 'LICENSE') -PathType Leaf)) {
    throw 'The VS Code package license is missing.'
}

$targets = @(
    [pscustomobject]@{ Rid = 'win-x64'; Target = 'win32-x64'; Executable = 'lunil-language-server.exe' },
    [pscustomobject]@{ Rid = 'win-arm64'; Target = 'win32-arm64'; Executable = 'lunil-language-server.exe' },
    [pscustomobject]@{ Rid = 'linux-x64'; Target = 'linux-x64'; Executable = 'lunil-language-server' },
    [pscustomobject]@{ Rid = 'linux-arm64'; Target = 'linux-arm64'; Executable = 'lunil-language-server' },
    [pscustomobject]@{ Rid = 'osx-x64'; Target = 'darwin-x64'; Executable = 'lunil-language-server' },
    [pscustomobject]@{ Rid = 'osx-arm64'; Target = 'darwin-arm64'; Executable = 'lunil-language-server' }
)

function Remove-ServerStaging {
    if (-not (Test-Path -LiteralPath $serverRoot)) { return }
    $resolved = [IO.Path]::GetFullPath($serverRoot)
    $expectedPrefix = $extensionRoot.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $resolved.StartsWith($expectedPrefix, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to remove server staging outside the extension root: $resolved"
    }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}

function Get-Sha256Hex([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $sha256 = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($sha256.ComputeHash($stream)) -replace '-', '').ToLowerInvariant()
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
Push-Location $extensionRoot
try {
    if (-not $SkipNodeInstall) {
        & npm ci
        if ($LASTEXITCODE -ne 0) { throw 'npm ci failed.' }
    }
    if (-not $SkipExtensionCheck) {
        & npm run check
        if ($LASTEXITCODE -ne 0) { throw 'VS Code extension check failed.' }
    }

    foreach ($item in $targets) {
        Remove-ServerStaging
        $publishDirectory = Join-Path $serverRoot $item.Rid
        & dotnet restore $project -r $item.Rid
        if ($LASTEXITCODE -ne 0) { throw "Restore failed for $($item.Rid)." }
        & dotnet publish $project -c Release -r $item.Rid --self-contained true --no-restore `
            -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
            -p:DebugType=None -p:DebugSymbols=false -o $publishDirectory
        if ($LASTEXITCODE -ne 0) { throw "Publish failed for $($item.Rid)." }

        $executable = Join-Path $publishDirectory $item.Executable
        if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
            throw "Published server executable is missing for $($item.Rid)."
        }
        $hash = Get-Sha256Hex $executable
        [IO.File]::WriteAllText(
            (Join-Path $publishDirectory 'server.sha256'),
            "$hash  $($item.Executable)`n",
            [Text.UTF8Encoding]::new($false))

        $vsix = Join-Path $OutputDirectory "lunil-lua-$version-$($item.Target).vsix"
        & npx vsce package --target $item.Target --out $vsix --no-dependencies --allow-missing-repository
        if ($LASTEXITCODE -ne 0) { throw "VSIX packaging failed for $($item.Target)." }

        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $archive = [IO.Compression.ZipFile]::OpenRead($vsix)
        try {
            $entries = $archive.Entries.FullName
            foreach ($required in @(
                'extension/package.json',
                'extension/LICENSE.txt',
                "extension/server/$($item.Rid)/$($item.Executable)",
                "extension/server/$($item.Rid)/server.sha256"
            )) {
                if ($entries -notcontains $required) {
                    throw "VSIX $($item.Target) is missing $required."
                }
            }
            $foreignServers = $entries | Where-Object {
                $_ -like 'extension/server/*/lunil-language-server*' -and
                $_ -notlike "extension/server/$($item.Rid)/*"
            }
            if ($foreignServers) {
                throw "VSIX $($item.Target) contains a foreign platform server."
            }
        }
        finally {
            $archive.Dispose()
        }

        $vsixHash = Get-Sha256Hex $vsix
        [IO.File]::WriteAllText(
            "$vsix.sha256",
            "$vsixHash  $([IO.Path]::GetFileName($vsix))`n",
            [Text.UTF8Encoding]::new($false))
    }
}
finally {
    Remove-ServerStaging
    Pop-Location
}

Get-ChildItem -LiteralPath $OutputDirectory -Filter '*.vsix' | Sort-Object Name | Select-Object -ExpandProperty FullName
