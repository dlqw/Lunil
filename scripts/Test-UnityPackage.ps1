[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string[]] $UnityEditorPaths,
    [switch] $SkipPlayer
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$packageOutput = @(& (Join-Path $PSScriptRoot 'New-UnityPackage.ps1') -Version $Version)
$packageInfo = @($packageOutput | Where-Object {
    $_ -isnot [string] -and $_.PSObject.Properties.Name -contains 'Path'
}) | Select-Object -Last 1
if ($null -eq $packageInfo) { throw 'Unity package build did not return package metadata.' }
$tarball = $packageInfo.Path
$fixtureAssets = Join-Path $repositoryRoot 'tests/Lunil.Unity.Fixture/Assets'
$sharedGameplaySource = Join-Path $repositoryRoot 'tests/Lunil.Gameplay.Fixture/SharedGameplayFixture.cs'
$sharedSoakSource = Join-Path $repositoryRoot 'tests/Lunil.Gameplay.Fixture/SharedEngineSoakFixture.cs'
$resultRoot = Join-Path $repositoryRoot "artifacts/unity/$Version/verification"
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()

function Invoke-Unity([string] $Editor, [string[]] $Arguments) {
    $quoted = @($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    })
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $Editor
    $start.Arguments = $quoted -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $process = [Diagnostics.Process]::Start($start)
    try {
        $process.WaitForExit()
        return $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

foreach ($editorInput in $UnityEditorPaths) {
    $editor = [IO.Path]::GetFullPath($editorInput)
    if (-not (Test-Path -LiteralPath $editor -PathType Leaf)) {
        throw "Unity Editor does not exist: $editor"
    }
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($editor).ProductVersion
    $versionLabel = ($fileVersion -split '_')[0]
    if ([string]::IsNullOrWhiteSpace($versionLabel)) {
        throw "Could not determine Unity version from $editor"
    }

    $project = Join-Path $resultRoot ("project-" + ($versionLabel -replace '[^0-9A-Za-z.-]', '-'))
    if (Test-Path -LiteralPath $project) {
        Remove-Item -LiteralPath $project -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $project 'Assets') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $project 'Packages') -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $project 'ProjectSettings') -Force | Out-Null
    Copy-Item -Path (Join-Path $fixtureAssets '*') -Destination (Join-Path $project 'Assets') -Recurse -Force
    Copy-Item -LiteralPath $sharedGameplaySource `
        -Destination (Join-Path $project 'Assets/SharedGameplayFixture.cs') -Force
    Copy-Item -LiteralPath $sharedSoakSource `
        -Destination (Join-Path $project 'Assets/SharedEngineSoakFixture.cs') -Force

    $tarballUri = 'file:' + $tarball.Replace('\\', '/')
    $manifest = [ordered]@{
        dependencies = [ordered]@{
            'com.dlqw.lunil' = $tarballUri
            'com.unity.test-framework' = '1.1.33'
        }
        testables = @('com.dlqw.lunil')
    } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText(
        (Join-Path $project 'Packages/manifest.json'),
        $manifest,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $project 'ProjectSettings/ProjectVersion.txt'),
        "m_EditorVersion: $versionLabel`nm_EditorVersionWithRevision: $fileVersion`n",
        [Text.UTF8Encoding]::new($false))

    $importLog = Join-Path $project 'fresh-import.log'
    $importExit = Invoke-Unity $editor @(
        '-batchmode', '-nographics', '-quit', '-projectPath', $project, '-logFile', $importLog)
    if ($importExit -ne 0 -or
        (Select-String -LiteralPath $importLog -Pattern 'error CS\d+|Scripts have compiler errors' -Quiet)) {
        $tail = if (Test-Path -LiteralPath $importLog) {
            (Get-Content -LiteralPath $importLog -Tail 120) -join "`n"
        } else { '<missing Unity import log>' }
        throw "Unity $versionLabel fresh offline package import failed with exit $importExit.`n$tail"
    }

    $bindingOutput = 'Assets/LuaClrGeneratedBindings.g.cs'
    $bindingLog = Join-Path $project 'binding-generation.log'
    $bindingExit = Invoke-Unity $editor @(
        '-batchmode', '-nographics', '-quit', '-projectPath', $project,
        '-executeMethod', 'Lunil.Unity.Editor.LuaUnityBindingPrecompiler.GenerateFromCommandLine',
        '-lunilBindingOutput', $bindingOutput, '-logFile', $bindingLog)
    if ($bindingExit -ne 0 -or
        -not (Test-Path -LiteralPath (Join-Path $project $bindingOutput) -PathType Leaf)) {
        $tail = if (Test-Path -LiteralPath $bindingLog) {
            (Get-Content -LiteralPath $bindingLog -Tail 120) -join "`n"
        } else { '<missing Unity binding-generation log>' }
        throw "Unity $versionLabel C# 9 binding pre-generation failed with exit $bindingExit.`n$tail"
    }

    $editorLog = Join-Path $project 'editor-tests.log'
    $testResults = Join-Path $project 'editor-tests.xml'
    $testExit = Invoke-Unity $editor @(
        '-batchmode', '-nographics', '-projectPath', $project,
        '-runTests', '-testPlatform', 'EditMode', '-testResults', $testResults,
        '-logFile', $editorLog)
    if ($testExit -ne 0 -or -not (Test-Path -LiteralPath $testResults -PathType Leaf)) {
        $tail = if (Test-Path -LiteralPath $editorLog) {
            (Get-Content -LiteralPath $editorLog -Tail 120) -join "`n"
        } else { '<missing Unity editor log>' }
        throw "Unity $versionLabel Editor tests failed with exit $testExit.`n$tail"
    }
    [xml]$testXml = Get-Content -Raw -LiteralPath $testResults
    if ($testXml.'test-run'.result -ne 'Passed') {
        throw "Unity $versionLabel Editor tests reported $($testXml.'test-run'.result)."
    }
    $expectedGameplayTrace = '5dca24ae91fa6dc36374459305f4bbdd3d596dc6a4dd40764c3cb5f951300d05'
    $expectedGameplaySnapshot = '1760139754:156633:9457:616802:1666:5153828:2:100000:100000'
    $gameplayPattern = 'LUNIL_GAMEPLAY_TRACE host=unity ticks=100000 revision=2 trace=([0-9a-f]{64}) snapshot=([^\s]+) active=0 pending=0'
    $editorGameplayMatch = Select-String -LiteralPath $editorLog -Pattern $gameplayPattern |
        Select-Object -Last 1
    if ($null -eq $editorGameplayMatch -or
        $editorGameplayMatch.Matches[0].Groups[1].Value -ne $expectedGameplayTrace -or
        $editorGameplayMatch.Matches[0].Groups[2].Value -ne $expectedGameplaySnapshot) {
        throw "Unity $versionLabel Editor shared gameplay trace is missing or inconsistent."
    }

    $playerSucceeded = $false
    $playerGameplayTrace = $null
    $playerGameplaySnapshot = $null
    if (-not $SkipPlayer) {
        $playerDirectory = Join-Path $project 'Player'
        $player = Join-Path $playerDirectory 'LunilUnityFixture.exe'
        $buildLog = Join-Path $project 'player-build.log'
        $buildExit = Invoke-Unity $editor @(
            '-batchmode', '-nographics', '-quit', '-projectPath', $project,
            '-executeMethod', 'Lunil.Unity.Fixture.Editor.PlayerBuilder.Build',
            '-lunilPlayerOutput', $player, '-logFile', $buildLog)
        if ($buildExit -ne 0 -or -not (Test-Path -LiteralPath $player -PathType Leaf)) {
            $tail = if (Test-Path -LiteralPath $buildLog) {
                (Get-Content -LiteralPath $buildLog -Tail 120) -join "`n"
            } else { '<missing Unity player build log>' }
            throw "Unity $versionLabel Mono player build failed with exit $buildExit.`n$tail"
        }

        $playerLog = Join-Path $project 'player-run.log'
        $process = Start-Process -FilePath $player `
            -ArgumentList @('-batchmode', '-nographics', '-logFile', $playerLog) `
            -WindowStyle Hidden -PassThru
        if (-not $process.WaitForExit(120000)) {
            $process.Kill()
            throw "Unity $versionLabel Mono player timed out."
        }
        if ($process.ExitCode -ne 0 -or
            -not (Select-String -LiteralPath $playerLog -SimpleMatch 'LUNIL_UNITY_PLAYER_OK' -Quiet)) {
            $tail = if (Test-Path -LiteralPath $playerLog) {
                (Get-Content -LiteralPath $playerLog -Tail 120) -join "`n"
            } else { '<missing Unity player log>' }
            throw "Unity $versionLabel Mono player failed with exit $($process.ExitCode).`n$tail"
        }
        $playerGameplayMatch = Select-String -LiteralPath $playerLog -Pattern $gameplayPattern |
            Select-Object -Last 1
        if ($null -eq $playerGameplayMatch -or
            $playerGameplayMatch.Matches[0].Groups[1].Value -ne $expectedGameplayTrace -or
            $playerGameplayMatch.Matches[0].Groups[2].Value -ne $expectedGameplaySnapshot) {
            throw "Unity $versionLabel Mono player shared gameplay trace is missing or inconsistent."
        }
        $playerGameplayTrace = $playerGameplayMatch.Matches[0].Groups[1].Value
        $playerGameplaySnapshot = $playerGameplayMatch.Matches[0].Groups[2].Value
        $playerSucceeded = $true
    }

    $results.Add([pscustomobject]@{
        unityVersion = $versionLabel
        editorPath = $editor
        packageSha256 = $packageInfo.Sha256
        editorTests = 'passed'
        monoPlayer = if ($playerSucceeded) { 'passed' } else { 'skipped' }
        gameplayTicks = 100000
        gameplayRevision = 2
        gameplayTrace = $editorGameplayMatch.Matches[0].Groups[1].Value
        gameplaySnapshot = $editorGameplayMatch.Matches[0].Groups[2].Value
        monoGameplayTrace = $playerGameplayTrace
        monoGameplaySnapshot = $playerGameplaySnapshot
        fixture = 'tests/Lunil.Unity.Fixture'
    })
}

$resultPath = Join-Path $resultRoot 'results.json'
[IO.File]::WriteAllText(
    $resultPath,
    ($results | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))
$results
