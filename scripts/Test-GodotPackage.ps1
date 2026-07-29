[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string[]] $GodotEditorPaths,
    [switch] $ExportDesktop,
    [switch] $ExportMobile,
    [string] $AndroidSdkRoot = $env:ANDROID_SDK_ROOT,
    [string] $AndroidAvdHome = $env:ANDROID_AVD_HOME,
    [string] $AndroidAvdName = 'lunil-api34'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureSource = Join-Path $repositoryRoot 'tests/Lunil.Godot.Fixture'
$sharedGameplaySource = Join-Path $repositoryRoot `
    'tests/Lunil.Gameplay.Fixture/SharedGameplayFixture.cs'
$sharedSoakSource = Join-Path $repositoryRoot `
    'tests/Lunil.Gameplay.Fixture/SharedEngineSoakFixture.cs'
$addonSource = Join-Path $repositoryRoot 'integrations/godot/addons/lunil'
$resultRoot = Join-Path $repositoryRoot "artifacts/godot/$Version/verification"
$packageRoot = Join-Path $repositoryRoot "artifacts/godot/$Version/packages"
$artifactsRoot = [IO.Path]::GetFullPath((Join-Path $repositoryRoot 'artifacts'))
$runningOnWindows = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::Windows)
$runningOnMacOS = [Runtime.InteropServices.RuntimeInformation]::IsOSPlatform(
    [Runtime.InteropServices.OSPlatform]::OSX)

function Remove-SafeDirectory([string] $Path) {
    $resolved = [IO.Path]::GetFullPath($Path)
    if (-not $resolved.StartsWith(
        $artifactsRoot + [IO.Path]::DirectorySeparatorChar,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Godot verification path escaped the artifacts directory: $resolved"
    }
    if (Test-Path -LiteralPath $resolved) {
        Remove-Item -LiteralPath $resolved -Recurse -Force
    }
}

function Wait-FileUnlocked([string] $Path, [int] $TimeoutSeconds = 10) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { return }
    $deadline = [DateTime]::UtcNow.AddSeconds($TimeoutSeconds)
    do {
        try {
            $stream = [IO.File]::Open(
                $Path,
                [IO.FileMode]::Open,
                [IO.FileAccess]::ReadWrite,
                [IO.FileShare]::None)
            $stream.Dispose()
            return
        }
        catch [IO.IOException] {
            Start-Sleep -Milliseconds 250
        }
    } while ([DateTime]::UtcNow -lt $deadline)
    throw "Timed out waiting for the Android emulator to release $Path"
}

function Invoke-Process(
    [string] $FilePath,
    [string[]] $Arguments,
    [int] $TimeoutMinutes,
    [string] $StandardOutput,
    [string] $StandardError,
    [switch] $DirectFileRedirection,
    [string] $CompletedOutput,
    [string] $CompletionMarker
) {
    $quoted = @($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    })

    # Godot 4.4's exporter can intermittently finish an artifact and its editor
    # shutdown sequence but leave the console process alive on Windows. Keep live
    # logs, require fresh completion evidence, and bound that shutdown separately.
    if ($DirectFileRedirection -and $runningOnWindows -and
        (-not [string]::IsNullOrWhiteSpace($StandardOutput) -or
         -not [string]::IsNullOrWhiteSpace($StandardError))) {
        $startParameters = @{
            FilePath = $FilePath
            ArgumentList = $quoted -join ' '
            PassThru = $true
            WindowStyle = 'Hidden'
        }
        if (-not [string]::IsNullOrWhiteSpace($StandardOutput)) {
            $startParameters.RedirectStandardOutput = $StandardOutput
        }
        if (-not [string]::IsNullOrWhiteSpace($StandardError)) {
            $startParameters.RedirectStandardError = $StandardError
        }
        $process = Start-Process @startParameters
        try {
            # Windows PowerShell releases the native process handle too early unless it
            # is materialized before a fast child exits, which would erase ExitCode.
            $null = $process.Handle
            $deadline = [DateTime]::UtcNow.AddMinutes($TimeoutMinutes)
            $completedAt = $null
            $forcedAfterCompletion = $false
            while (-not $process.WaitForExit(1000)) {
                $hasCompletionEvidence = $false
                if (-not [string]::IsNullOrWhiteSpace($CompletedOutput) -and
                    -not [string]::IsNullOrWhiteSpace($CompletionMarker) -and
                    (Test-Path -LiteralPath $CompletedOutput -PathType Leaf) -and
                    (Get-Item -LiteralPath $CompletedOutput).Length -gt 0 -and
                    (Test-Path -LiteralPath $StandardOutput -PathType Leaf)) {
                    try {
                        $completionLog = Get-Content -LiteralPath $StandardOutput -Raw
                        $hasCompletionEvidence = $completionLog.Contains($CompletionMarker)
                    }
                    catch {
                        $hasCompletionEvidence = $false
                    }
                }
                if ($hasCompletionEvidence) {
                    if ($null -eq $completedAt) { $completedAt = [DateTime]::UtcNow }
                    if (([DateTime]::UtcNow - $completedAt).TotalSeconds -ge 15) {
                        try { $process.Kill() }
                        catch { throw "Completed process $FilePath could not be stopped: $($_.Exception.Message)" }
                        if (-not $process.WaitForExit(30000)) {
                            throw "Completed process $FilePath did not stop within thirty seconds."
                        }
                        $forcedAfterCompletion = $true
                        break
                    }
                }
                else {
                    $completedAt = $null
                }
                if ([DateTime]::UtcNow -ge $deadline) {
                    try { $process.Kill() } catch { }
                    throw "$FilePath timed out after $TimeoutMinutes minute(s)."
                }
            }
            $process.WaitForExit()
            $process.Refresh()
            if ($forcedAfterCompletion) { return 0 }
            return $process.ExitCode
        }
        finally {
            $process.Dispose()
        }
    }

    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.Arguments = $quoted -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = -not [string]::IsNullOrWhiteSpace($StandardOutput)
    $start.RedirectStandardError = -not [string]::IsNullOrWhiteSpace($StandardError)
    $process = [Diagnostics.Process]::Start($start)
    try {
        $outputTask = if ($start.RedirectStandardOutput) {
            $process.StandardOutput.ReadToEndAsync()
        } else { $null }
        $errorTask = if ($start.RedirectStandardError) {
            $process.StandardError.ReadToEndAsync()
        } else { $null }
        if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
            try { $process.Kill($true) } catch { try { $process.Kill() } catch { } }
            throw "$FilePath timed out after $TimeoutMinutes minute(s)."
        }
        if ($null -ne $outputTask) {
            [IO.File]::WriteAllText($StandardOutput, $outputTask.GetAwaiter().GetResult(),
                [Text.UTF8Encoding]::new($false))
        }
        if ($null -ne $errorTask) {
            [IO.File]::WriteAllText($StandardError, $errorTask.GetAwaiter().GetResult(),
                [Text.UTF8Encoding]::new($false))
        }
        return $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

function Get-GodotVersion([string] $Editor) {
    $versionOut = Join-Path $resultRoot ('version-' + [Guid]::NewGuid().ToString('N') + '.out.log')
    $versionErr = $versionOut + '.err'
    $exit = Invoke-Process $Editor @('--version') 2 $versionOut $versionErr
    if ($exit -ne 0) { throw "Godot version query failed for $Editor." }
    $text = (Get-Content -Raw $versionOut).Trim()
    $match = [Regex]::Match($text, '^(?<major>4)\.(?<minor>4|6)\.(?<patch>\d+)')
    if (-not $match.Success) {
        throw "Only Godot 4.4 and 4.6 .NET editors are accepted, found '$text'."
    }
    return [pscustomobject]@{
        Label = "$($match.Groups['major'].Value).$($match.Groups['minor'].Value).$($match.Groups['patch'].Value)"
        Sdk = "$($match.Groups['major'].Value).$($match.Groups['minor'].Value).$($match.Groups['patch'].Value)"
        Full = $text
    }
}

function Assert-Exit([int] $ExitCode, [string] $Log, [string] $ErrorLog, [string] $Description) {
    if ($ExitCode -eq 0) { return }
    $tail = @()
    if (Test-Path -LiteralPath $Log) { $tail += Get-Content -LiteralPath $Log -Tail 120 }
    if (Test-Path -LiteralPath $ErrorLog) { $tail += Get-Content -LiteralPath $ErrorLog -Tail 120 }
    throw "$Description failed with exit $ExitCode.`n$($tail -join "`n")"
}

function New-FixtureProject([string] $Editor) {
    $editor = [IO.Path]::GetFullPath($Editor)
    if (-not (Test-Path -LiteralPath $editor -PathType Leaf)) {
        throw "Godot editor does not exist: $editor"
    }
    $godot = Get-GodotVersion $editor
    $project = Join-Path $resultRoot ("project-" + $godot.Label)
    Remove-SafeDirectory $project
    New-Item -ItemType Directory -Path $project -Force | Out-Null
    Copy-Item -Path (Join-Path $fixtureSource '*') -Destination $project -Recurse -Force
    Copy-Item -LiteralPath $sharedGameplaySource `
        -Destination (Join-Path $project 'SharedGameplayFixture.cs') -Force
    Copy-Item -LiteralPath $sharedSoakSource `
        -Destination (Join-Path $project 'SharedEngineSoakFixture.cs') -Force
    $addon = Join-Path $project 'addons/lunil'
    New-Item -ItemType Directory -Path $addon -Force | Out-Null
    Copy-Item -Path (Join-Path $addonSource '*') -Destination $addon -Recurse -Force

    $targetFramework = if ($godot.Label.StartsWith('4.6.', [StringComparison]::Ordinal)) {
        'net9.0'
    } else {
        'net8.0'
    }
    $projectFile = @"
<Project Sdk="Godot.NET.Sdk/$($godot.Sdk)">
  <PropertyGroup>
    <TargetFramework>$targetFramework</TargetFramework>
    <EnableDynamicLoading>true</EnableDynamicLoading>
    <RootNamespace>Lunil.Godot.Fixture</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Lunil.Godot" Version="$Version" />
  </ItemGroup>
</Project>
"@
    [IO.File]::WriteAllText((Join-Path $project 'Lunil.Godot.Fixture.csproj'),
        $projectFile, [Text.UTF8Encoding]::new($false))
    $solutionFile = @"
Microsoft Visual Studio Solution File, Format Version 12.00
# Visual Studio Version 17
VisualStudioVersion = 17.0.31903.59
MinimumVisualStudioVersion = 10.0.40219.1
Project("{FAE04EC0-301F-11D3-BF4B-00C04F79EFBC}") = "Lunil.Godot.Fixture", "Lunil.Godot.Fixture.csproj", "{15B71DC8-A2F5-4598-BA99-C265B0910258}"
EndProject
Global
	GlobalSection(SolutionConfigurationPlatforms) = preSolution
		Debug|Any CPU = Debug|Any CPU
		ExportDebug|Any CPU = ExportDebug|Any CPU
		ExportRelease|Any CPU = ExportRelease|Any CPU
		Release|Any CPU = Release|Any CPU
	EndGlobalSection
	GlobalSection(ProjectConfigurationPlatforms) = postSolution
		{15B71DC8-A2F5-4598-BA99-C265B0910258}.Debug|Any CPU.ActiveCfg = Debug|Any CPU
		{15B71DC8-A2F5-4598-BA99-C265B0910258}.Debug|Any CPU.Build.0 = Debug|Any CPU
		{15B71DC8-A2F5-4598-BA99-C265B0910258}.ExportDebug|Any CPU.ActiveCfg = ExportDebug|Any CPU
		{15B71DC8-A2F5-4598-BA99-C265B0910258}.ExportDebug|Any CPU.Build.0 = ExportDebug|Any CPU
		{15B71DC8-A2F5-4598-BA99-C265B0910258}.ExportRelease|Any CPU.ActiveCfg = ExportRelease|Any CPU
		{15B71DC8-A2F5-4598-BA99-C265B0910258}.ExportRelease|Any CPU.Build.0 = ExportRelease|Any CPU
		{15B71DC8-A2F5-4598-BA99-C265B0910258}.Release|Any CPU.ActiveCfg = Release|Any CPU
		{15B71DC8-A2F5-4598-BA99-C265B0910258}.Release|Any CPU.Build.0 = Release|Any CPU
	EndGlobalSection
EndGlobal
"@
    [IO.File]::WriteAllText((Join-Path $project 'Lunil.Godot.Fixture.sln'),
        $solutionFile, [Text.UTF8Encoding]::new($false))
    $escapedPackageRoot = [Security.SecurityElement]::Escape($packageRoot)
    $escapedGlobalPackages = [Security.SecurityElement]::Escape(
        (Join-Path $project '.nuget/packages'))
    $nuget = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <config>
    <add key="globalPackagesFolder" value="$escapedGlobalPackages" />
  </config>
  <packageSources>
    <clear />
    <add key="LunilLocal" value="$escapedPackageRoot" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@
    [IO.File]::WriteAllText((Join-Path $project 'NuGet.config'),
        $nuget, [Text.UTF8Encoding]::new($false))

    $restoreLog = Join-Path $project 'dotnet-restore.log'
    $restoreErr = Join-Path $project 'dotnet-restore.err.log'
    $exit = Invoke-Process 'dotnet' @(
        'restore', (Join-Path $project 'Lunil.Godot.Fixture.csproj'),
        '--configfile', (Join-Path $project 'NuGet.config')) 15 $restoreLog $restoreErr
    Assert-Exit $exit $restoreLog $restoreErr "Godot $($godot.Label) package restore"

    $buildLog = Join-Path $project 'dotnet-build.log'
    $buildErr = Join-Path $project 'dotnet-build.err.log'
    $exit = Invoke-Process 'dotnet' @(
        'build', (Join-Path $project 'Lunil.Godot.Fixture.csproj'),
        '-c', 'Debug', '--no-restore') 15 $buildLog $buildErr
    Assert-Exit $exit $buildLog $buildErr "Godot $($godot.Label) C# build"

    $editorLog = Join-Path $project 'headless-editor.log'
    $editorErr = Join-Path $project 'headless-editor.err.log'
    $exit = Invoke-Process $editor @(
        '--headless', '--editor', '--path', $project, '--quit-after', '3', '--verbose') `
        10 $editorLog $editorErr
    Assert-Exit $exit $editorLog $editorErr "Godot $($godot.Label) headless editor"

    return [pscustomobject]@{
        Editor = $editor
        Version = $godot.Label
        FullVersion = $godot.Full
        Project = $project
    }
}

function Test-ProjectRun([object] $Fixture) {
    $runLog = Join-Path $Fixture.Project 'headless-run.log'
    $runErr = Join-Path $Fixture.Project 'headless-run.err.log'
    $exit = Invoke-Process $Fixture.Editor @(
        '--headless', '--path', $Fixture.Project, '--verbose') 5 $runLog $runErr
    Assert-Exit $exit $runLog $runErr "Godot $($Fixture.Version) headless fixture"
    if (-not (Select-String -LiteralPath $runLog -SimpleMatch 'LUNIL_GODOT_FIXTURE_OK' -Quiet) -or
        -not (Select-String -LiteralPath $runLog -SimpleMatch 'LUNIL_GODOT_RESOURCE_TRACE' -Quiet) -or
        -not (Select-String -LiteralPath $runLog -SimpleMatch 'LUNIL_GODOT_CONSOLE_TRACE' -Quiet) -or
        -not (Select-String -LiteralPath $runLog -SimpleMatch 'LUNIL_GAMEPLAY_TRACE host=godot' -Quiet)) {
        throw "Godot $($Fixture.Version) fixture did not emit its success/resource markers.`n$((Get-Content $runLog -Tail 160) -join "`n")"
    }

    $traceLine = Select-String -LiteralPath $runLog `
        -Pattern '^LUNIL_GAMEPLAY_TRACE host=godot ticks=(?<ticks>\d+) revision=(?<revision>\d+) trace=(?<trace>[0-9a-f]{64}) snapshot=(?<snapshot>\S+) active=(?<active>\d+) pending=(?<pending>\d+)$' |
        Select-Object -Last 1
    if ($null -eq $traceLine) {
        throw "Godot $($Fixture.Version) gameplay trace marker is malformed."
    }

    return [pscustomobject]@{
        ticks = [int]$traceLine.Matches[0].Groups['ticks'].Value
        revision = [int]$traceLine.Matches[0].Groups['revision'].Value
        trace = $traceLine.Matches[0].Groups['trace'].Value
        snapshot = $traceLine.Matches[0].Groups['snapshot'].Value
        active = [int]$traceLine.Matches[0].Groups['active'].Value
        pending = [int]$traceLine.Matches[0].Groups['pending'].Value
    }
}

function Export-Project(
    [object] $Fixture,
    [string] $Preset,
    [string] $RelativeOutput,
    [switch] $Debug
) {
    $output = Join-Path $resultRoot ("exports/$($Fixture.Version)/$RelativeOutput")
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force | Out-Null
    Remove-Item -LiteralPath $output -Force -ErrorAction SilentlyContinue
    $name = $Preset.ToLowerInvariant().Replace(' ', '-')
    $log = Join-Path $Fixture.Project "export-$name.log"
    $errorLog = Join-Path $Fixture.Project "export-$name.err.log"
    $exportMode = if ($Debug) { '--export-debug' } else { '--export-release' }
    $exit = Invoke-Process $Fixture.Editor @(
        '--headless', '--path', $Fixture.Project, $exportMode, $Preset, $output) `
        45 $log $errorLog -DirectFileRedirection:$runningOnWindows `
        -CompletedOutput $output -CompletionMarker 'loading_editor_layout: end'
    Assert-Exit $exit $log $errorLog "Godot $($Fixture.Version) $Preset export"
    $exportErrors = @(
        (Get-Content -LiteralPath $log -Raw),
        (Get-Content -LiteralPath $errorLog -Raw)) -join "`n"
    $knownGodot44UniversalCleanup =
        $Preset -eq 'macOS' -and
        $Fixture.Version.StartsWith('4.4.', [StringComparison]::Ordinal) -and
        $exportErrors.Contains('System.IO.DirectoryNotFoundException:') -and
        $exportErrors.Contains('godot-publish-dotnet') -and
        $exportErrors.Contains('GodotTools.Export.ExportPlugin._ExportEnd()') -and
        -not $exportErrors.Contains('Failed to build project') -and
        -not $exportErrors.Contains('Project export for preset "macOS" failed')
    $knownGodot46EditorSettings =
        $Fixture.Version.StartsWith('4.6.', [StringComparison]::Ordinal) -and
        ($exportErrors.Contains(
            'ERROR: EditorSettings not instantiated yet when getting setting "export/android/android_sdk_path".') -or
         $exportErrors.Contains(
            'ERROR: EditorSettings not instantiated yet when getting setting "export/android/shutdown_adb_on_exit".')) -and
        $exportErrors.Contains('dotnet_publish_project') -and
        (Test-Path -LiteralPath $output -PathType Leaf) -and
        (Get-Item -LiteralPath $output).Length -gt 0 -and
        -not $exportErrors.Contains('Failed to build project') -and
        -not $exportErrors.Contains('Cannot export project') -and
        -not $exportErrors.Contains("Project export for preset `"$Preset`" failed")
    $knownAndroidEditorSettingsShutdown =
        $Preset -eq 'Android' -and
        $exportErrors.Contains(
            'Condition "!EditorSettings::get_singleton() || !EditorSettings::get_singleton()->has_setting(p_setting)" is true. Returning: Variant()') -and
        $exportErrors.Contains('export: end') -and
        $exportErrors.Contains('Signed') -and
        -not $exportErrors.Contains('Failed to build project') -and
        -not $exportErrors.Contains('Cannot export project') -and
        -not $exportErrors.Contains('Project export for preset "Android" failed')
    $knownGodot44EditorSettingsShutdown =
        $Fixture.Version.StartsWith('4.4.', [StringComparison]::Ordinal) -and
        $exportErrors.Contains(
            'Condition "!EditorSettings::get_singleton() || !EditorSettings::get_singleton()->has_setting(p_setting)" is true. Returning: Variant()') -and
        $exportErrors.Contains('dotnet_publish_project: end') -and
        (Test-Path -LiteralPath $output -PathType Leaf) -and
        (Get-Item -LiteralPath $output).Length -gt 0 -and
        -not $exportErrors.Contains('Failed to build project') -and
        -not $exportErrors.Contains('Cannot export project') -and
        -not $exportErrors.Contains("Project export for preset `"$Preset`" failed")
    if ($exportErrors -match '(?m)^ERROR:' -and
        -not $knownGodot44UniversalCleanup -and
        -not $knownGodot46EditorSettings -and
        -not $knownAndroidEditorSettingsShutdown -and
        -not $knownGodot44EditorSettingsShutdown) {
        throw "Godot $($Fixture.Version) $Preset export reported an error.`n$((Get-Content $errorLog -Tail 160) -join "`n")"
    }
    if (-not (Test-Path -LiteralPath $output -PathType Leaf) -or
        (Get-Item -LiteralPath $output).Length -eq 0) {
        throw "Godot $($Fixture.Version) $Preset export output is missing: $output"
    }
    return $output
}

function Start-AndroidEmulator([string] $LogRoot) {
    $devices = & $script:adb devices
    if ($devices -match '(?m)^emulator-\d+\s+device\s*$') { return $null }
    $available = @(& $script:emulator -list-avds)
    if ($available -notcontains $AndroidAvdName) {
        throw "Android AVD '$AndroidAvdName' is not installed in $env:ANDROID_AVD_HOME"
    }
    $process = Start-Process -FilePath $script:emulator -ArgumentList @(
        '-avd', $AndroidAvdName, '-no-window', '-no-audio', '-no-boot-anim',
        '-gpu', 'swiftshader_indirect', '-no-snapshot') -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $LogRoot 'android-emulator.out.log') `
        -RedirectStandardError (Join-Path $LogRoot 'android-emulator.err.log') -PassThru
    $deadline = [DateTime]::UtcNow.AddMinutes(8)
    do {
        Start-Sleep -Seconds 2
        $devices = & $script:adb devices
        $booted = if ($devices -match '(?m)^emulator-\d+\s+device\s*$') {
            (& $script:adb shell getprop sys.boot_completed 2>$null).Trim() -eq '1'
        } else { $false }
    } while (-not $booted -and [DateTime]::UtcNow -lt $deadline -and -not $process.HasExited)
    if (-not $booted) {
        try { $process.Kill() } catch { }
        throw 'Android emulator did not finish booting within eight minutes.'
    }
    return $process
}

function Test-AndroidExport([object] $Fixture, [string] $Apk, [object] $Gameplay) {
    $packageName = 'com.dlqw.lunil.godotfixture'
    & $script:adb logcat -c
    & $script:adb uninstall $packageName 2>$null | Out-Null
    & $script:adb install -r -t $Apk | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Godot $($Fixture.Version) Android APK installation failed."
    }
    $activity = @(& $script:adb shell cmd package resolve-activity --brief `
        -c android.intent.category.LAUNCHER $packageName) |
        Where-Object { $_ -match '/' } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($activity)) {
        throw "Godot $($Fixture.Version) Android launcher activity could not be resolved."
    }
    & $script:adb shell am force-stop $packageName | Out-Null
    & $script:adb shell am start -W -n $activity | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Godot $($Fixture.Version) Android activity failed to start."
    }

    $deadline = [DateTime]::UtcNow.AddMinutes(3)
    do {
        Start-Sleep -Seconds 2
        $logcat = @(& $script:adb logcat -d -v brief) -join "`n"
        $succeeded = $logcat.Contains('LUNIL_GODOT_FIXTURE_OK') -and
            $logcat.Contains('LUNIL_GAMEPLAY_TRACE host=godot')
        $failed = $logcat -match '(?m)^.*Process: com\.dlqw\.lunil\.godotfixture,'
    } while (-not $succeeded -and -not $failed -and [DateTime]::UtcNow -lt $deadline)

    $logPath = Join-Path $Fixture.Project 'android-run.log'
    [IO.File]::WriteAllText($logPath, $logcat, [Text.UTF8Encoding]::new($false))
    if (-not $succeeded -or $failed -or
        -not $logcat.Contains("trace=$($Gameplay.trace) snapshot=$($Gameplay.snapshot)")) {
        throw "Godot $($Fixture.Version) Android gameplay run failed or diverged.`n$((Get-Content $logPath -Tail 180) -join "`n")"
    }
}

function Export-IosProject([object] $Fixture) {
    if (-not $runningOnMacOS) { return 'requires-macos' }
    $output = Join-Path $resultRoot "exports/$($Fixture.Version)/build/ios/LunilGodotFixture"
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($output)) -Force | Out-Null
    $log = Join-Path $Fixture.Project 'export-ios.log'
    $errorLog = Join-Path $Fixture.Project 'export-ios.err.log'
    $exit = Invoke-Process $Fixture.Editor @(
        '--headless', '--path', $Fixture.Project, '--export-debug', 'iOS', $output) `
        60 $log $errorLog
    Assert-Exit $exit $log $errorLog "Godot $($Fixture.Version) iOS Xcode export"
    $xcodeProject = Get-ChildItem -Path ([IO.Path]::GetDirectoryName($output)) `
        -Filter '*.xcodeproj' -Directory -Recurse | Select-Object -First 1
    if ($null -eq $xcodeProject -or
        -not (Test-Path -LiteralPath (Join-Path $xcodeProject.FullName 'project.pbxproj') -PathType Leaf)) {
        throw "Godot $($Fixture.Version) iOS export did not produce an Xcode project."
    }
    $buildLog = Join-Path $Fixture.Project 'xcodebuild-ios.log'
    $buildErr = Join-Path $Fixture.Project 'xcodebuild-ios.err.log'
    $exit = Invoke-Process 'xcodebuild' @(
        '-project', $xcodeProject.FullName, '-configuration', 'Debug', '-sdk', 'iphoneos',
        'CODE_SIGNING_ALLOWED=NO', 'build') 60 $buildLog $buildErr
    Assert-Exit $exit $buildLog $buildErr "Godot $($Fixture.Version) iOS Xcode build"
    return 'export-and-build'
}

Remove-SafeDirectory $resultRoot
Remove-SafeDirectory $packageRoot
New-Item -ItemType Directory -Path $resultRoot,$packageRoot -Force | Out-Null

& (Join-Path $PSScriptRoot 'New-NuGetPackages.ps1') -Version $Version `
    -OutputDirectory $packageRoot | Out-Null
if ($LASTEXITCODE -ne 0) { throw 'Godot fixture NuGet package creation failed.' }
$addonInfo = & (Join-Path $PSScriptRoot 'New-GodotAddon.ps1') -Version $Version `
    -OutputDirectory (Join-Path $repositoryRoot "artifacts/godot/$Version/addon")

$results = [Collections.Generic.List[object]]::new()
$emulatorProcess = $null
$stopAdbServer = $false
if ($ExportMobile) {
    if ([string]::IsNullOrWhiteSpace($AndroidSdkRoot) -or
        [string]::IsNullOrWhiteSpace($AndroidAvdHome)) {
        throw '-AndroidSdkRoot and -AndroidAvdHome are required for the Android run gate.'
    }
    $AndroidSdkRoot = [IO.Path]::GetFullPath($AndroidSdkRoot)
    $env:ANDROID_SDK_ROOT = $AndroidSdkRoot
    $env:ANDROID_HOME = $AndroidSdkRoot
    $env:ANDROID_AVD_HOME = [IO.Path]::GetFullPath($AndroidAvdHome)
    $script:adb = Join-Path $AndroidSdkRoot 'platform-tools/adb.exe'
    $script:emulator = Join-Path $AndroidSdkRoot 'emulator/emulator.exe'
    if (-not (Test-Path -LiteralPath $script:adb -PathType Leaf) -or
        -not (Test-Path -LiteralPath $script:emulator -PathType Leaf)) {
        throw "Android SDK is missing adb or emulator below $AndroidSdkRoot"
    }
    $existingAdbProcesses = @([Diagnostics.Process]::GetProcessesByName('adb'))
    try { $stopAdbServer = $existingAdbProcesses.Count -eq 0 }
    finally { $existingAdbProcesses | ForEach-Object { $_.Dispose() } }
    $emulatorProcess = Start-AndroidEmulator $resultRoot
}
try {
foreach ($editor in $GodotEditorPaths) {
    $fixture = New-FixtureProject $editor
    $gameplay = Test-ProjectRun $fixture
    $desktop = [ordered]@{}
    if ($ExportDesktop) {
        $windows = Export-Project $fixture 'Windows Desktop' 'build/windows/Lunil.Godot.Fixture.exe'
        $windowsLog = Join-Path $fixture.Project 'export-windows-run.log'
        $windowsErr = Join-Path $fixture.Project 'export-windows-run.err.log'
        $exit = Invoke-Process $windows @('--headless', '--log-file', $windowsLog) `
            5 $null $null
        Assert-Exit $exit $windowsLog $windowsErr "Godot $($fixture.Version) Windows export run"
        if (-not (Select-String -LiteralPath $windowsLog -SimpleMatch 'LUNIL_GODOT_FIXTURE_OK' -Quiet)) {
            throw "Godot $($fixture.Version) Windows export did not report success."
        }
        if (-not (Select-String -LiteralPath $windowsLog -SimpleMatch `
                "trace=$($gameplay.trace) snapshot=$($gameplay.snapshot)" -Quiet)) {
            throw "Godot $($fixture.Version) Windows export gameplay trace diverged."
        }
        $desktop.windows = 'export-and-run'
        Export-Project $fixture 'Linux' 'build/linux/Lunil.Godot.Fixture.x86_64' | Out-Null
        $desktop.linux = 'export-only'
        Export-Project $fixture 'macOS' 'build/macos/Lunil.Godot.Fixture.zip' | Out-Null
        $desktop.macos = 'export-only'
    }
    $mobile = [ordered]@{}
    if ($ExportMobile) {
        $android = Export-Project $fixture 'Android' 'build/android/Lunil.Godot.Fixture.apk' -Debug
        Test-AndroidExport $fixture $android $gameplay
        $mobile.android = 'export-and-run'
        $mobile.ios = Export-IosProject $fixture
    }
    $results.Add([pscustomobject]@{
        godotVersion = $fixture.Version
        fullVersion = $fixture.FullVersion
        editorPath = $fixture.Editor
        editorTest = 'passed'
        headlessRun = 'passed'
        desktop = $desktop
        mobile = $mobile
        packageVersion = $Version
        addonSha256 = $addonInfo.Sha256
        gameplay = $gameplay
    })
}
}
finally {
    try {
        if ($null -ne $emulatorProcess) {
            try {
                & $script:adb emu kill 2>$null | Out-Null
                $shutdownDeadline = [DateTime]::UtcNow.AddSeconds(60)
                do {
                    Start-Sleep -Seconds 1
                    $devices = & $script:adb devices
                    $emulatorStopped = $devices -notmatch '(?m)^emulator-\d+\s+device\s*$'
                } while (-not $emulatorStopped -and [DateTime]::UtcNow -lt $shutdownDeadline)
                if (-not $emulatorStopped) {
                    throw 'Android emulator did not stop within sixty seconds.'
                }
                try { $emulatorProcess.WaitForExit(30000) | Out-Null } catch { }
                Wait-FileUnlocked (Join-Path $resultRoot 'android-emulator.out.log') 30
                Wait-FileUnlocked (Join-Path $resultRoot 'android-emulator.err.log') 30
            }
            finally {
                $emulatorProcess.Dispose()
            }
        }
    }
    finally {
        if ($stopAdbServer) {
            & $script:adb kill-server 2>$null | Out-Null
        }
    }
}

$resultPath = Join-Path $resultRoot 'results.json'
[IO.File]::WriteAllText($resultPath, ($results | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))
$results
