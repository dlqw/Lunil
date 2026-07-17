[CmdletBinding()]
param(
    [string] $Configuration = 'Release',

    [switch] $NoBuild,

    [switch] $NoRestore,

    [switch] $Update
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$compatibilityLine = (& (Join-Path $PSScriptRoot 'Get-LunilCompatibilityLine.ps1')).Trim()
$generatorVersion = '2.0.2'
$baselineDirectory = Join-Path $repositoryRoot "api/$compatibilityLine"
$generatedDirectory = Join-Path $repositoryRoot 'artifacts/public-api/generated'

function ConvertTo-NormalizedText([string] $Text) {
    $normalized = $Text.Replace("`r`n", "`n").Replace("`r", "`n")
    return $normalized.TrimEnd([char[]]@("`n")) + "`n"
}

function Write-NormalizedText([string] $Path, [string] $Text) {
    $parent = [System.IO.Path]::GetDirectoryName($Path)
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [System.IO.File]::WriteAllText(
        $Path,
        (ConvertTo-NormalizedText $Text),
        [System.Text.UTF8Encoding]::new($false))
}

function Get-TextSha256([string] $Text) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes((ConvertTo-NormalizedText $Text))
    $hash = [System.Security.Cryptography.SHA256]::HashData($bytes)
    return [Convert]::ToHexString($hash)
}

function Assert-TextMatches([string] $ExpectedPath, [string] $ActualText) {
    if (-not (Test-Path -LiteralPath $ExpectedPath -PathType Leaf)) {
        throw "Missing public API baseline: $ExpectedPath. Run scripts/Update-PublicApiBaselines.ps1."
    }

    $expected = ConvertTo-NormalizedText ([System.IO.File]::ReadAllText($ExpectedPath))
    $actual = ConvertTo-NormalizedText $ActualText
    if (-not [string]::Equals($expected, $actual, [StringComparison]::Ordinal)) {
        $expectedLines = $expected.Split("`n")
        $actualLines = $actual.Split("`n")
        $lineCount = [Math]::Max($expectedLines.Length, $actualLines.Length)
        $firstDifference = 0
        for ($index = 0; $index -lt $lineCount; $index++) {
            $expectedLine = if ($index -lt $expectedLines.Length) { $expectedLines[$index] } else { '<missing>' }
            $actualLine = if ($index -lt $actualLines.Length) { $actualLines[$index] } else { '<missing>' }
            if (-not [string]::Equals($expectedLine, $actualLine, [StringComparison]::Ordinal)) {
                $firstDifference = $index + 1
                break
            }
        }

        throw "Public API baseline differs at line $firstDifference in $ExpectedPath. Run scripts/Update-PublicApiBaselines.ps1 only for an intentional reviewed API change."
    }
}

Push-Location $repositoryRoot
try {
    & dotnet tool restore
    if ($LASTEXITCODE -ne 0) { throw 'dotnet tool restore failed.' }

    if (-not $NoBuild) {
        $arguments = @('build', 'Lunil.sln', '--configuration', $Configuration, '--nologo')
        if ($NoRestore) { $arguments += '--no-restore' }
        & dotnet @arguments
        if ($LASTEXITCODE -ne 0) { throw 'Building assemblies for public API validation failed.' }
    }

    $projects = @(Get-ChildItem -LiteralPath (Join-Path $repositoryRoot 'src') `
        -Recurse -Filter 'Lunil.*.csproj' | Sort-Object Name)
    if ($projects.Count -ne 13) {
        throw "Expected the active $compatibilityLine package scope to contain 13 projects, found $($projects.Count)."
    }

    $resolvedGeneratedDirectory = [System.IO.Path]::GetFullPath($generatedDirectory)
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
    if (-not $resolvedGeneratedDirectory.StartsWith(
        $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Generated API directory escaped the artifacts root: $resolvedGeneratedDirectory"
    }
    if (Test-Path -LiteralPath $resolvedGeneratedDirectory) {
        Remove-Item -LiteralPath $resolvedGeneratedDirectory -Recurse -Force
    }
    New-Item -ItemType Directory -Path $resolvedGeneratedDirectory -Force | Out-Null

    $assemblies = [Collections.Generic.List[object]]::new()
    foreach ($project in $projects) {
        $targetPath = (& dotnet msbuild $project.FullName -nologo `
            -getProperty:TargetPath -p:Configuration=$Configuration).Trim()
        if ($LASTEXITCODE -ne 0 -or -not (Test-Path -LiteralPath $targetPath -PathType Leaf)) {
            throw "Could not resolve the built target for $($project.FullName)."
        }

        $assemblyName = [System.IO.Path]::GetFileNameWithoutExtension($targetPath)
        $baselineName = "$assemblyName.cs"
        $generatedPath = Join-Path $resolvedGeneratedDirectory $baselineName
        & dotnet tool run Meziantou.Framework.PublicApiGenerator.Tool -- `
            --input "net10.0=$targetPath" `
            --output-file $generatedPath `
            --omit-auto-generated-comment
        if ($LASTEXITCODE -ne 0) {
            throw "Public API generation failed for $assemblyName."
        }

        $generatedText = ConvertTo-NormalizedText ([System.IO.File]::ReadAllText($generatedPath))
        Write-NormalizedText $generatedPath $generatedText
        $baselinePath = Join-Path $baselineDirectory $baselineName
        if ($Update) {
            Write-NormalizedText $baselinePath $generatedText
        }
        else {
            Assert-TextMatches $baselinePath $generatedText
        }

        $assemblies.Add([ordered]@{
            Assembly = $assemblyName
            Project = $project.FullName.Substring($repositoryRoot.Length + 1).Replace('\', '/')
            Baseline = "api/$compatibilityLine/$baselineName"
            Sha256 = Get-TextSha256 $generatedText
        })
    }

    $manifest = [ordered]@{
        SchemaVersion = 1
        CompatibilityLine = $compatibilityLine
        ChangePolicy = 'reviewed-public-api-snapshot'
        Generator = [ordered]@{
            Package = 'Meziantou.Framework.PublicApiGenerator.Tool'
            Version = $generatorVersion
        }
        AssemblyCount = $assemblies.Count
        Assemblies = $assemblies.ToArray()
    }
    $manifestText = ConvertTo-NormalizedText ($manifest | ConvertTo-Json -Depth 6)
    $manifestPath = Join-Path $baselineDirectory 'manifest.json'
    if ($Update) {
        $expectedNames = @($assemblies | ForEach-Object { [System.IO.Path]::GetFileName($_.Baseline) })
        Get-ChildItem -LiteralPath $baselineDirectory -Filter '*.cs' -File -ErrorAction SilentlyContinue |
            Where-Object Name -NotIn $expectedNames |
            Remove-Item -Force
        Write-NormalizedText $manifestPath $manifestText
        Write-Host "Updated $($assemblies.Count) public API baselines under $baselineDirectory."
    }
    else {
        Assert-TextMatches $manifestPath $manifestText
        Write-Host "Verified $($assemblies.Count) reviewed public API baselines for $compatibilityLine."
    }
}
finally {
    Pop-Location
}
