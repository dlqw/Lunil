[CmdletBinding()]
param(
    [string]$Version
)

$ErrorActionPreference = 'Stop'

# 单一事实来源：Directory.Build.props 的 VersionPrefix（与 Get-LunilVersion.ps1 一致）。
# release.yml 传入 -Version（来自 tag 解析），本地运行时省略则自动读取项目版本。
$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = & (Join-Path $PSScriptRoot 'Get-LunilVersion.ps1')
}
if ($Version -notmatch '^(0|[1-9]\d*)\.(0|[1-9]\d*)\.(0|[1-9]\d*)(-(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*)(\.(0|[1-9]\d*|\d*[A-Za-z-][0-9A-Za-z-]*))*)?$') {
    throw "Unsupported release SemVer: $Version"
}

$errors = [System.Collections.Generic.List[string]]::new()

function Assert-JsonProperty {
    param(
        [string]$Path,
        [string]$Property,
        [string]$Expected
    )
    $absolute = Join-Path $repositoryRoot $Path
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
        $errors.Add("Missing release input: $Path")
        return
    }
    $json = Get-Content -Raw -LiteralPath $absolute | ConvertFrom-Json -AsHashtable
    $actual = $json[$Property]
    if ([string]$actual -ne $Expected) {
        $errors.Add("${Path}: $Property is '$actual', expected '$Expected'.")
    }
}

function Assert-JsonPackageRootVersion {
    param(
        [string]$Path,
        [string]$Expected
    )
    $absolute = Join-Path $repositoryRoot $Path
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
        $errors.Add("Missing release input: $Path")
        return
    }
    $json = Get-Content -Raw -LiteralPath $absolute | ConvertFrom-Json -AsHashtable
    if ([string]$json['version'] -ne $Expected) {
        $errors.Add("${Path}: version is '$($json['version'])', expected '$Expected'.")
    }
    $rootPackage = $json['packages']['']
    if ($null -eq $rootPackage -or [string]$rootPackage['version'] -ne $Expected) {
        $actualRoot = if ($null -eq $rootPackage) { '<missing>' } else { $rootPackage['version'] }
        $errors.Add("${Path}: packages[''].version is '$actualRoot', expected '$Expected'.")
    }
}

function Assert-GodotPluginVersion {
    param(
        [string]$Path,
        [string]$Expected
    )
    $absolute = Join-Path $repositoryRoot $Path
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
        $errors.Add("Missing release input: $Path")
        return
    }
    $line = Get-Content -LiteralPath $absolute | Where-Object { $_ -match '^version\s*=' } | Select-Object -First 1
    if ($null -eq $line -or $line -notmatch '^version\s*=\s*"([^"]+)"') {
        $errors.Add("${Path}: " + 'no version="..." line found.')
        return
    }
    if ($Matches[1] -ne $Expected) {
        $errors.Add("${Path}: version is '$($Matches[1])', expected '$Expected'.")
    }
}

function Assert-UnitySampleManifest {
    param(
        [string]$Path,
        [string]$Expected
    )
    $absolute = Join-Path $repositoryRoot $Path
    if (-not (Test-Path -LiteralPath $absolute -PathType Leaf)) {
        $errors.Add("Missing release input: $Path")
        return
    }
    $json = Get-Content -Raw -LiteralPath $absolute | ConvertFrom-Json -AsHashtable
    $reference = [string]$json['dependencies']['com.dlqw.lunil']
    $expectedReference = "file:../../UnityPackages/com.dlqw.lunil-$Expected.tgz"
    if ($reference -ne $expectedReference) {
        $errors.Add("${Path}: com.dlqw.lunil is '$reference', expected '$expectedReference'.")
    }
}

# 发布输入清单（Unity / Godot / VS Code / Unity samples），任一漂移都会在发布前失败。
Assert-JsonProperty 'integrations/unity/com.dlqw.lunil/package.json' 'version' $Version
Assert-GodotPluginVersion 'integrations/godot/addons/lunil/plugin.cfg' $Version
Assert-JsonProperty 'editors/vscode/package.json' 'version' $Version
Assert-JsonPackageRootVersion 'editors/vscode/package-lock.json' $Version
Assert-UnitySampleManifest 'samples/Lunil.Unity.2022.3/Packages/manifest.json' $Version
Assert-UnitySampleManifest 'samples/Lunil.Unity.6/Packages/manifest.json' $Version

$changelog = Join-Path $repositoryRoot "changelogs/$Version.md"
if (-not (Test-Path -LiteralPath $changelog -PathType Leaf)) {
    $errors.Add("Missing changelogs/$Version.md.")
}

if ($errors.Count -gt 0) {
    throw "Release input versions are inconsistent for ${Version}:`n" + ($errors -join "`n")
}

Write-Output "Release input versions are consistent for $Version."
