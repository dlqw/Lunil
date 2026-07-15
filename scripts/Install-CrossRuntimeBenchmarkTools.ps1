[CmdletBinding()]
param(
    [string] $OutputDirectory,
    [string] $RuntimeIdentifier,
    [switch] $Force
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($RuntimeIdentifier)) {
    $platform = if ($IsWindows) { 'win' } elseif ($IsMacOS) { 'osx' } else { 'linux' }
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    $RuntimeIdentifier = "$platform-$architecture"
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot "artifacts/cross-runtime-tools/$RuntimeIdentifier"
}
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$manifestPath = Join-Path $OutputDirectory 'tools.json'

$luaVersion = '5.4.8'
$luaUri = "https://www.lua.org/ftp/lua-$luaVersion.tar.gz"
$luaSha256 = '4F18DDAE154E793E46EEAB727C59EF1C0C0C2B744E7B94219710D76F530629AE'
$luaJitCommit = '3c4f9fe2052b8d08a917ac0d5f38563f0297b5a3'
$luaJitUri = "https://github.com/LuaJIT/LuaJIT/archive/$luaJitCommit.tar.gz"
$luaJitSha256 = '295F9E6722A2200AAF41297B28F73D337AC12236CDF1788981E46BD0AFD466FF'

function Assert-ChildPath([string] $Path, [string] $Parent) {
    $resolvedPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar)
    if (-not $resolvedPath.StartsWith(
        $resolvedParent + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside $resolvedParent`: $resolvedPath"
    }
}

function Get-VerifiedArchive(
    [string] $Uri,
    [string] $ExpectedSha256,
    [string] $Destination) {
    if (-not (Test-Path -LiteralPath $Destination)) {
        Write-Host "Downloading $Uri"
        Invoke-WebRequest -Uri $Uri -OutFile $Destination
    }
    $actual = (Get-FileHash -LiteralPath $Destination -Algorithm SHA256).Hash
    if ($actual -ne $ExpectedSha256) {
        Remove-Item -LiteralPath $Destination -Force
        throw "SHA-256 mismatch for $Uri. Expected $ExpectedSha256; got $actual."
    }
}

function Invoke-NativeCommand([string] $FilePath, [string[]] $Arguments) {
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath failed with exit code $LASTEXITCODE."
    }
}

function Get-VisualStudioDeveloperCommand([string] $TargetArchitecture) {
    $vsWhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio/Installer/vswhere.exe'
    if (-not (Test-Path -LiteralPath $vsWhere)) {
        throw 'vswhere.exe is required to build Lua and LuaJIT on Windows.'
    }
    $requiredComponent = if ($TargetArchitecture -eq 'arm64') {
        'Microsoft.VisualStudio.Component.VC.Tools.ARM64'
    } else {
        'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'
    }
    $installation = & $vsWhere -latest -products '*' -requires $requiredComponent `
        -property installationPath
    if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($installation)) {
        throw 'A Visual Studio installation with the C++ toolchain is required.'
    }
    $developerCommand = Join-Path $installation.Trim() 'Common7/Tools/VsDevCmd.bat'
    if (-not (Test-Path -LiteralPath $developerCommand)) {
        throw "Visual Studio developer command was not found: $developerCommand"
    }
    return $developerCommand
}

if ((Test-Path -LiteralPath $manifestPath) -and -not $Force) {
    $existing = Get-Content -Raw -LiteralPath $manifestPath | ConvertFrom-Json
    if ((Test-Path -LiteralPath $existing.lua.executable) -and
        (Test-Path -LiteralPath $existing.luaJit.executable) -and
        $existing.lua.version -eq $luaVersion -and
        $existing.luaJit.commit -eq $luaJitCommit) {
        Write-Host "Pinned cross-runtime tools already exist at $OutputDirectory"
        Get-Content -Raw -LiteralPath $manifestPath
        return
    }
}

$artifactsRoot = Join-Path $repositoryRoot 'artifacts'
Assert-ChildPath $OutputDirectory $artifactsRoot
if (Test-Path -LiteralPath $OutputDirectory) {
    Remove-Item -LiteralPath $OutputDirectory -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$downloadDirectory = Join-Path $OutputDirectory 'downloads'
$sourceDirectory = Join-Path $OutputDirectory 'src'
$binDirectory = Join-Path $OutputDirectory 'bin'
New-Item -ItemType Directory -Force -Path $downloadDirectory, $sourceDirectory, $binDirectory | Out-Null

$luaArchive = Join-Path $downloadDirectory "lua-$luaVersion.tar.gz"
$luaJitArchive = Join-Path $downloadDirectory "luajit-$luaJitCommit.tar.gz"
Get-VerifiedArchive $luaUri $luaSha256 $luaArchive
Get-VerifiedArchive $luaJitUri $luaJitSha256 $luaJitArchive
Invoke-NativeCommand 'tar' @('-xzf', $luaArchive, '-C', $sourceDirectory)
Invoke-NativeCommand 'tar' @('-xzf', $luaJitArchive, '-C', $sourceDirectory)

$luaSource = Join-Path $sourceDirectory "lua-$luaVersion"
$luaJitSource = Join-Path $sourceDirectory "LuaJIT-$luaJitCommit"
if (-not (Test-Path -LiteralPath $luaSource) -or -not (Test-Path -LiteralPath $luaJitSource)) {
    throw 'A pinned runtime source archive did not contain the expected root directory.'
}

if ($IsWindows) {
    $targetArchitecture = if ($RuntimeIdentifier -eq 'win-arm64') { 'arm64' } else { 'x64' }
    $developerCommand = Get-VisualStudioDeveloperCommand $targetArchitecture
    $luaSources = @(
        'lapi.c', 'lcode.c', 'lctype.c', 'ldebug.c', 'ldo.c', 'ldump.c', 'lfunc.c',
        'lgc.c', 'llex.c', 'lmem.c', 'lobject.c', 'lopcodes.c', 'lparser.c', 'lstate.c',
        'lstring.c', 'ltable.c', 'ltm.c', 'lundump.c', 'lvm.c', 'lzio.c', 'lauxlib.c',
        'lbaselib.c', 'lcorolib.c', 'ldblib.c', 'liolib.c', 'lmathlib.c', 'loadlib.c',
        'loslib.c', 'lstrlib.c', 'ltablib.c', 'lutf8lib.c', 'linit.c'
    ) -join ' '
    $buildCommand = Join-Path $OutputDirectory 'build-runtimes.cmd'
    @"
@echo off
call "$developerCommand" -no_logo -arch=$targetArchitecture
if errorlevel 1 exit /b %errorlevel%
pushd "$luaSource\src"
cl /nologo /O2 /MD /DNDEBUG /D_CRT_SECURE_NO_WARNINGS /DLUA_COMPAT_5_3 /c $luaSources
if errorlevel 1 exit /b %errorlevel%
lib /nologo /out:lua54.lib *.obj
if errorlevel 1 exit /b %errorlevel%
cl /nologo /O2 /MD /DNDEBUG /D_CRT_SECURE_NO_WARNINGS lua.c lua54.lib /Fe:lua.exe
if errorlevel 1 exit /b %errorlevel%
popd
pushd "$luaJitSource\src"
call msvcbuild.bat static
if errorlevel 1 exit /b %errorlevel%
popd
"@ | Set-Content -LiteralPath $buildCommand -Encoding ascii
    Invoke-NativeCommand 'cmd.exe' @('/d', '/c', $buildCommand)
    $luaExecutable = Join-Path $binDirectory 'lua.exe'
    $luaJitExecutable = Join-Path $binDirectory 'luajit.exe'
    Copy-Item -LiteralPath (Join-Path $luaSource 'src/lua.exe') -Destination $luaExecutable
    Copy-Item -LiteralPath (Join-Path $luaJitSource 'src/luajit.exe') -Destination $luaJitExecutable
} else {
    # The benchmark is non-interactive; the generic macOS target avoids a Homebrew readline
    # dependency while retaining the standard language, math, string, table, and os.clock APIs.
    $luaTarget = if ($IsMacOS) { 'generic' } else { 'linux' }
    $jobs = [Math]::Max(2, [Environment]::ProcessorCount)
    Invoke-NativeCommand 'make' @(
        '-C', (Join-Path $luaSource 'src'), $luaTarget,
        "-j$jobs", 'MYCFLAGS=-O3 -DNDEBUG')
    Invoke-NativeCommand 'make' @(
        '-C', $luaJitSource, "-j$jobs", 'BUILDMODE=static', 'XCFLAGS=-O3 -DNDEBUG')
    $luaExecutable = Join-Path $binDirectory 'lua'
    $luaJitExecutable = Join-Path $binDirectory 'luajit'
    Copy-Item -LiteralPath (Join-Path $luaSource 'src/lua') -Destination $luaExecutable
    Copy-Item -LiteralPath (Join-Path $luaJitSource 'src/luajit') -Destination $luaJitExecutable
    Invoke-NativeCommand 'chmod' @('+x', $luaExecutable, $luaJitExecutable)
}

$luaVersionOutput = (& $luaExecutable -v 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $luaVersionOutput -notmatch [regex]::Escape($luaVersion)) {
    throw "Built native Lua did not report version $luaVersion`: $luaVersionOutput"
}
$luaJitVersionOutput = (& $luaJitExecutable -v 2>&1 | Out-String).Trim()
if ($LASTEXITCODE -ne 0 -or $luaJitVersionOutput -notmatch 'LuaJIT 2\.1') {
    throw "Built LuaJIT did not report the expected version: $luaJitVersionOutput"
}

$manifest = [ordered]@{
    schemaVersion = 1
    rid = $RuntimeIdentifier
    generatedAtUtc = [DateTimeOffset]::UtcNow.ToString('O')
    lua = [ordered]@{
        version = $luaVersion
        sourceUri = $luaUri
        sourceSha256 = $luaSha256.ToLowerInvariant()
        executable = [IO.Path]::GetFullPath($luaExecutable)
        executableSha256 = (Get-FileHash -LiteralPath $luaExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
        versionOutput = $luaVersionOutput
    }
    luaJit = [ordered]@{
        version = '2.1'
        commit = $luaJitCommit
        sourceUri = $luaJitUri
        sourceSha256 = $luaJitSha256.ToLowerInvariant()
        executable = [IO.Path]::GetFullPath($luaJitExecutable)
        executableSha256 = (Get-FileHash -LiteralPath $luaJitExecutable -Algorithm SHA256).Hash.ToLowerInvariant()
        versionOutput = $luaJitVersionOutput
    }
    moonSharp = [ordered]@{
        package = 'MoonSharp'
        version = '2.0.0'
    }
}
$manifest | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath $manifestPath -Encoding utf8
Write-Host "Pinned cross-runtime tools installed at $OutputDirectory"
$manifest | ConvertTo-Json -Depth 6
