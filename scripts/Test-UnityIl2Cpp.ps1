[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [Parameter(Mandatory)][string[]] $FullMatrixEditorPaths,
    [string[]] $CompileGateEditorPaths = @(),
    [ValidateSet('windows', 'android', 'ios', 'webgl')]
    [string[]] $Targets = @('windows', 'android', 'ios', 'webgl'),
    [string] $ChromePath,
    [string] $AndroidSdkRoot = $env:ANDROID_SDK_ROOT,
    [string] $AndroidAvdHome = $env:ANDROID_AVD_HOME,
    [string] $AndroidAvdName = 'lunil-api34'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$fixtureAssets = Join-Path $repositoryRoot 'tests/Lunil.Unity.Fixture/Assets'
$sharedGameplaySource = Join-Path $repositoryRoot 'tests/Lunil.Gameplay.Fixture/SharedGameplayFixture.cs'
$sharedSoakSource = Join-Path $repositoryRoot 'tests/Lunil.Gameplay.Fixture/SharedEngineSoakFixture.cs'
$resultRoot = Join-Path $repositoryRoot "artifacts/unity/$Version/il2cpp-verification"
$packageOutput = @(& (Join-Path $PSScriptRoot 'New-UnityPackage.ps1') -Version $Version)
$packageInfo = @($packageOutput | Where-Object {
    $_ -isnot [string] -and $_.PSObject.Properties.Name -contains 'Path'
}) | Select-Object -Last 1
if ($null -eq $packageInfo) { throw 'Unity package build did not return package metadata.' }
$tarball = $packageInfo.Path
New-Item -ItemType Directory -Path $resultRoot -Force | Out-Null
$results = [Collections.Generic.List[object]]::new()

if ($Targets -contains 'webgl') {
    if ([string]::IsNullOrWhiteSpace($ChromePath) -or
        -not (Test-Path -LiteralPath $ChromePath -PathType Leaf)) {
        throw 'A valid -ChromePath is required for the WebGL run gate.'
    }
    $ChromePath = [IO.Path]::GetFullPath($ChromePath)
}

$adb = $null
$emulator = $null
if ($Targets -contains 'android') {
    if ([string]::IsNullOrWhiteSpace($AndroidSdkRoot)) {
        throw '-AndroidSdkRoot is required for the Android run gate.'
    }
    $AndroidSdkRoot = [IO.Path]::GetFullPath($AndroidSdkRoot)
    $adb = Join-Path $AndroidSdkRoot 'platform-tools/adb.exe'
    $emulator = Join-Path $AndroidSdkRoot 'emulator/emulator.exe'
    if (-not (Test-Path -LiteralPath $adb -PathType Leaf) -or
        -not (Test-Path -LiteralPath $emulator -PathType Leaf)) {
        throw "Android SDK is missing adb or emulator below $AndroidSdkRoot"
    }
    if ([string]::IsNullOrWhiteSpace($AndroidAvdHome)) {
        throw '-AndroidAvdHome is required for the Android run gate.'
    }
    $env:ANDROID_SDK_ROOT = $AndroidSdkRoot
    $env:ANDROID_HOME = $AndroidSdkRoot
    $env:ANDROID_AVD_HOME = [IO.Path]::GetFullPath($AndroidAvdHome)
}

function Invoke-Process(
    [string] $FilePath,
    [string[]] $Arguments,
    [int] $TimeoutMinutes,
    [string] $StandardOutput,
    [string] $StandardError
) {
    $quoted = @($Arguments | ForEach-Object {
        if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
    })
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $FilePath
    $start.Arguments = $quoted -join ' '
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    if (-not [string]::IsNullOrWhiteSpace($StandardOutput)) {
        $start.RedirectStandardOutput = $true
    }
    if (-not [string]::IsNullOrWhiteSpace($StandardError)) {
        $start.RedirectStandardError = $true
    }
    $process = [Diagnostics.Process]::Start($start)
    try {
        $outputTask = if ($start.RedirectStandardOutput) {
            $process.StandardOutput.ReadToEndAsync()
        } else { $null }
        $errorTask = if ($start.RedirectStandardError) {
            $process.StandardError.ReadToEndAsync()
        } else { $null }
        if (-not $process.WaitForExit($TimeoutMinutes * 60 * 1000)) {
            try { $process.Kill() } catch { }
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

function Invoke-Unity([string] $Editor, [string[]] $Arguments, [int] $TimeoutMinutes = 90) {
    return Invoke-Process $Editor $Arguments $TimeoutMinutes $null $null
}

function Get-UnityVersion([string] $Editor) {
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($Editor).ProductVersion
    $versionLabel = ($fileVersion -split '_')[0]
    if ([string]::IsNullOrWhiteSpace($versionLabel)) {
        throw "Could not determine Unity version from $Editor"
    }
    return [pscustomobject]@{ Label = $versionLabel; Full = $fileVersion }
}

function New-FixtureProject([string] $Editor, [string] $Kind) {
    $editor = [IO.Path]::GetFullPath($Editor)
    if (-not (Test-Path -LiteralPath $editor -PathType Leaf)) {
        throw "Unity Editor does not exist: $editor"
    }
    $unity = Get-UnityVersion $editor
    $project = Join-Path $resultRoot ("project-" + $Kind + '-' +
        ($unity.Label -replace '[^0-9A-Za-z.-]', '-'))
    if (Test-Path -LiteralPath $project) {
        Remove-Item -LiteralPath $project -Recurse -Force
    }
    New-Item -ItemType Directory -Path (Join-Path $project 'Assets'),
        (Join-Path $project 'Packages'),(Join-Path $project 'ProjectSettings') -Force | Out-Null
    Copy-Item -Path (Join-Path $fixtureAssets '*') -Destination (Join-Path $project 'Assets') `
        -Recurse -Force
    Copy-Item -LiteralPath $sharedGameplaySource `
        -Destination (Join-Path $project 'Assets/SharedGameplayFixture.cs') -Force
    Copy-Item -LiteralPath $sharedSoakSource `
        -Destination (Join-Path $project 'Assets/SharedEngineSoakFixture.cs') -Force

    $manifest = [ordered]@{
        dependencies = [ordered]@{
            'com.dlqw.lunil' = 'file:' + $tarball.Replace('\', '/')
            'com.unity.test-framework' = '1.1.33'
        }
        testables = @('com.dlqw.lunil')
    } | ConvertTo-Json -Depth 8
    [IO.File]::WriteAllText((Join-Path $project 'Packages/manifest.json'), $manifest,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText((Join-Path $project 'ProjectSettings/ProjectVersion.txt'),
        "m_EditorVersion: $($unity.Label)`nm_EditorVersionWithRevision: $($unity.Full)`n",
        [Text.UTF8Encoding]::new($false))

    $importLog = Join-Path $project 'fresh-import.log'
    $exit = Invoke-Unity $editor @(
        '-batchmode', '-nographics', '-quit', '-projectPath', $project,
        '-logFile', $importLog)
    Assert-UnitySucceeded $exit $importLog "Unity $($unity.Label) fresh package import"

    $bindingOutput = 'Assets/LuaClrGeneratedBindings.g.cs'
    $bindingLog = Join-Path $project 'binding-generation.log'
    $exit = Invoke-Unity $editor @(
        '-batchmode', '-nographics', '-quit', '-projectPath', $project,
        '-executeMethod', 'Lunil.Unity.Editor.LuaUnityBindingPrecompiler.GenerateFromCommandLine',
        '-lunilBindingOutput', $bindingOutput, '-logFile', $bindingLog)
    Assert-UnitySucceeded $exit $bindingLog "Unity $($unity.Label) binding generation"
    if (-not (Test-Path -LiteralPath (Join-Path $project $bindingOutput) -PathType Leaf)) {
        throw "Unity $($unity.Label) did not generate C# 9 bindings."
    }
    return [pscustomobject]@{
        Editor = $editor
        Version = $unity.Label
        Project = $project
    }
}

function Assert-UnitySucceeded([int] $ExitCode, [string] $Log, [string] $Operation) {
    if ($ExitCode -eq 0 -and
        -not (Select-String -LiteralPath $Log -Pattern 'error CS\d+|Scripts have compiler errors' -Quiet)) {
        return
    }
    $tail = if (Test-Path -LiteralPath $Log) {
        (Get-Content -LiteralPath $Log -Tail 160) -join "`n"
    } else { '<missing Unity log>' }
    throw "$Operation failed with exit $ExitCode.`n$tail"
}

function Build-Target([object] $Fixture, [string] $Target) {
    $output = switch ($Target) {
        'windows' { Join-Path $Fixture.Project 'Player/windows/LunilUnityFixture.exe' }
        'android' { Join-Path $Fixture.Project 'Player/android/LunilUnityFixture.apk' }
        'ios' { Join-Path $Fixture.Project 'Player/ios' }
        'webgl' { Join-Path $Fixture.Project 'Player/webgl' }
    }
    $cliTarget = switch ($Target) {
        'windows' { 'Win64' }
        'android' { 'Android' }
        'ios' { 'iOS' }
        'webgl' { 'WebGL' }
    }
    $log = Join-Path $Fixture.Project "il2cpp-$Target-build.log"
    $exit = Invoke-Unity $Fixture.Editor @(
        '-batchmode', '-nographics', '-quit', '-projectPath', $Fixture.Project,
        '-buildTarget', $cliTarget,
        '-executeMethod', 'Lunil.Unity.Fixture.Editor.PlayerBuilder.BuildIl2Cpp',
        '-lunilBuildTarget', $Target, '-lunilPlayerOutput', $output,
        '-logFile', $log) 120
    Assert-UnitySucceeded $exit $log "Unity $($Fixture.Version) $Target IL2CPP build"
    if (-not (Test-Path -LiteralPath $output)) {
        throw "Unity $($Fixture.Version) $Target output is missing: $output"
    }
    return $output
}

function Test-WindowsPlayer([object] $Fixture, [string] $Player) {
    $log = Join-Path $Fixture.Project 'il2cpp-windows-run.log'
    $exit = Invoke-Process $Player @('-batchmode', '-nographics', '-logFile', $log) 5 $null $null
    if ($exit -ne 0 -or
        -not (Select-String -LiteralPath $log -SimpleMatch 'LUNIL_UNITY_IL2CPP_OK' -Quiet) -or
        -not (Select-String -LiteralPath $log -SimpleMatch 'LUNIL_UNITY_RESOURCE_TRACE' -Quiet)) {
        $tail = (Get-Content -LiteralPath $log -Tail 160) -join "`n"
        throw "Unity $($Fixture.Version) Windows IL2CPP player failed with exit $exit.`n$tail"
    }
}

function Start-AndroidEmulator([string] $LogRoot) {
    $devices = & $adb devices
    if ($devices -match '(?m)^emulator-\d+\s+device\s*$') { return $null }
    $available = @(& $emulator -list-avds)
    if ($available -notcontains $AndroidAvdName) {
        throw "Android AVD '$AndroidAvdName' is not installed in $env:ANDROID_AVD_HOME"
    }
    $process = Start-Process -FilePath $emulator -ArgumentList @(
        '-avd', $AndroidAvdName, '-no-window', '-no-audio', '-no-boot-anim',
        '-gpu', 'swiftshader_indirect', '-no-snapshot') -WindowStyle Hidden `
        -RedirectStandardOutput (Join-Path $LogRoot 'android-emulator.out.log') `
        -RedirectStandardError (Join-Path $LogRoot 'android-emulator.err.log') -PassThru
    $deadline = [DateTime]::UtcNow.AddMinutes(8)
    do {
        Start-Sleep -Seconds 2
        $devices = & $adb devices
        $booted = if ($devices -match '(?m)^emulator-\d+\s+device\s*$') {
            (& $adb shell getprop sys.boot_completed 2>$null).Trim() -eq '1'
        } else { $false }
    } while (-not $booted -and [DateTime]::UtcNow -lt $deadline -and -not $process.HasExited)
    if (-not $booted) {
        try { $process.Kill() } catch { }
        throw 'Android emulator did not finish booting within eight minutes.'
    }
    return $process
}

function Test-AndroidPlayer([object] $Fixture, [string] $Apk) {
    & $adb logcat -c
    & $adb install -r -t $Apk | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unity $($Fixture.Version) Android APK installation failed." }
    & $adb shell input keyevent KEYCODE_WAKEUP | Out-Null
    & $adb shell wm dismiss-keyguard | Out-Null
    $activity = @(& $adb shell cmd package resolve-activity --brief `
        -c android.intent.category.LAUNCHER com.dlqw.lunil.fixture) |
        Where-Object { $_ -match '/' } | Select-Object -Last 1
    if ([string]::IsNullOrWhiteSpace($activity)) {
        throw "Unity $($Fixture.Version) Android launcher activity could not be resolved."
    }
    & $adb shell am force-stop com.dlqw.lunil.fixture | Out-Null
    & $adb shell am start -W -n $activity | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Unity $($Fixture.Version) Android player launch failed." }
    $deadline = [DateTime]::UtcNow.AddMinutes(5)
    $log = ''
    do {
        Start-Sleep -Seconds 2
        $log = (& $adb logcat -d -v brief) -join "`n"
    } while ($log -notmatch 'LUNIL_UNITY_IL2CPP_OK' -and [DateTime]::UtcNow -lt $deadline)
    [IO.File]::WriteAllText((Join-Path $Fixture.Project 'il2cpp-android-run.log'), $log,
        [Text.UTF8Encoding]::new($false))
    & $adb uninstall com.dlqw.lunil.fixture | Out-Null
    if ($log -notmatch 'LUNIL_UNITY_IL2CPP_OK' -or
        $log -notmatch 'LUNIL_UNITY_RESOURCE_TRACE') {
        throw "Unity $($Fixture.Version) Android player did not emit the required markers."
    }
}

function Test-WebGlPlayer([object] $Fixture, [string] $Output) {
    $python = (Get-Command python -ErrorAction Stop).Source
    $port = Get-Random -Minimum 18000 -Maximum 28000
    $serverOut = Join-Path $Fixture.Project 'webgl-server.out.log'
    $serverErr = Join-Path $Fixture.Project 'webgl-server.err.log'
    $server = Start-Process -FilePath $python -ArgumentList @(
        '-m', 'http.server', $port, '--bind', '127.0.0.1', '--directory', $Output) `
        -WindowStyle Hidden -RedirectStandardOutput $serverOut -RedirectStandardError $serverErr `
        -PassThru
    try {
        Start-Sleep -Seconds 2
        if ($server.HasExited) { throw 'The WebGL fixture HTTP server exited before the run.' }
        $debugPort = Get-Random -Minimum 28001 -Maximum 38000
        $browserOut = Join-Path $Fixture.Project 'il2cpp-webgl-browser.out.log'
        $browserErr = Join-Path $Fixture.Project 'il2cpp-webgl-run.log'
        $profile = Join-Path $Fixture.Project 'ChromeProfile'
        $browser = Start-Process -FilePath $ChromePath -ArgumentList @(
            '--headless=new', '--disable-gpu', '--no-sandbox', '--disable-dev-shm-usage',
            '--autoplay-policy=no-user-gesture-required', "--remote-debugging-port=$debugPort",
            "--user-data-dir=$profile", '--no-first-run', '--no-default-browser-check',
            "http://127.0.0.1:$port/") -WindowStyle Hidden `
            -RedirectStandardOutput $browserOut -RedirectStandardError $browserErr -PassThru
        try {
            $deadline = [DateTime]::UtcNow.AddMinutes(5)
            $marker = $null
            do {
                Start-Sleep -Seconds 2
                try {
                    $targets = @(Invoke-RestMethod "http://127.0.0.1:$debugPort/json" `
                        -TimeoutSec 5 -ErrorAction Stop)
                    $page = $targets | Where-Object { $_.type -eq 'page' } | Select-Object -First 1
                    if ($null -ne $page) {
                        $marker = $page.title
                    }
                }
                catch {
                    if ($browser.HasExited) { break }
                }
            } while ($marker -ne 'LUNIL_UNITY_IL2CPP_OK' -and
                [DateTime]::UtcNow -lt $deadline -and -not $browser.HasExited)
            [IO.File]::WriteAllText(
                (Join-Path $Fixture.Project 'il2cpp-webgl-marker.txt'),
                [string]$marker,
                [Text.UTF8Encoding]::new($false))
            if ($marker -ne 'LUNIL_UNITY_IL2CPP_OK') {
                throw "Unity $($Fixture.Version) WebGL player did not publish its browser marker."
            }
        }
        finally {
            if (-not $browser.HasExited) { $browser.Kill() }
            $browser.Dispose()
        }
    }
    finally {
        if (-not $server.HasExited) { $server.Kill() }
        $server.Dispose()
    }
}

function Test-IosOutput([object] $Fixture, [string] $Output) {
    $project = Get-ChildItem -LiteralPath $Output -Filter '*.xcodeproj' -Directory |
        Select-Object -First 1
    if ($null -eq $project -or
        -not (Test-Path -LiteralPath (Join-Path $project.FullName 'project.pbxproj') -PathType Leaf)) {
        throw "Unity $($Fixture.Version) iOS output is missing its Xcode project."
    }
    $fixtureCpp = Get-ChildItem -LiteralPath $Output -Filter '*Lunil.Unity.Fixture*.cpp' -File -Recurse |
        Select-Object -First 1
    if ($null -eq $fixtureCpp -or
        -not (Select-String -LiteralPath $fixtureCpp.FullName -SimpleMatch 'LuaClrGeneratedBindings' -Quiet)) {
        throw "Unity $($Fixture.Version) iOS output lost generated binding IL2CPP code."
    }
}

$emulatorProcess = $null
try {
    if ($Targets -contains 'android') {
        $emulatorProcess = Start-AndroidEmulator $resultRoot
    }
    foreach ($editor in $FullMatrixEditorPaths) {
        $fixture = New-FixtureProject $editor 'matrix'
        $platforms = [ordered]@{}
        foreach ($target in $Targets) {
            $output = Build-Target $fixture $target
            switch ($target) {
                'windows' { Test-WindowsPlayer $fixture $output; $platforms.windows = 'build-and-run' }
                'android' { Test-AndroidPlayer $fixture $output; $platforms.android = 'build-and-run' }
                'webgl' { Test-WebGlPlayer $fixture $output; $platforms.webgl = 'build-and-run' }
                'ios' { Test-IosOutput $fixture $output; $platforms.ios = 'build-only' }
            }
        }
        $results.Add([pscustomobject]@{
            unityVersion = $fixture.Version
            editorPath = $fixture.Editor
            packageSha256 = $packageInfo.Sha256
            managedStrippingLevel = 'High'
            bindingMode = 'RegistryOnly'
            platforms = $platforms
        })
    }
    foreach ($editor in $CompileGateEditorPaths) {
        $fixture = New-FixtureProject $editor 'compile-gate'
        $results.Add([pscustomobject]@{
            unityVersion = $fixture.Version
            editorPath = $fixture.Editor
            packageSha256 = $packageInfo.Sha256
            compileGate = 'passed'
        })
    }
}
finally {
    if ($Targets -contains 'android') {
        try { & $adb emu kill | Out-Null } catch { }
    }
    if ($null -ne $emulatorProcess) {
        try {
            if (-not $emulatorProcess.HasExited) { $emulatorProcess.WaitForExit(30000) | Out-Null }
        } catch { }
        $emulatorProcess.Dispose()
    }
}

$resultPath = Join-Path $resultRoot 'results.json'
[IO.File]::WriteAllText($resultPath, ($results | ConvertTo-Json -Depth 8),
    [Text.UTF8Encoding]::new($false))
$results
