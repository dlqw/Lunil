[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $UnityEditorPath,
    [Parameter(Mandatory)][string] $UnityProjectPath,
    [string] $OutputAssetPath = 'Assets/LunilGenerated/LuaClrGeneratedBindings.g.cs'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$editor = [IO.Path]::GetFullPath($UnityEditorPath)
$project = [IO.Path]::GetFullPath($UnityProjectPath)
if (-not (Test-Path -LiteralPath $editor -PathType Leaf)) { throw "Unity Editor does not exist: $editor" }
if (-not (Test-Path -LiteralPath (Join-Path $project 'Packages/manifest.json') -PathType Leaf)) {
    throw "Unity project manifest does not exist below $project."
}
if (-not $OutputAssetPath.Replace('\\', '/').StartsWith('Assets/', [StringComparison]::Ordinal)) {
    throw 'OutputAssetPath must be below Assets/.'
}

$log = Join-Path $project 'Logs/LunilBindingGeneration.log'
New-Item -ItemType Directory -Path (Split-Path $log) -Force | Out-Null
$arguments = @(
    '-batchmode', '-nographics', '-quit', '-projectPath', $project,
    '-executeMethod', 'Lunil.Unity.Editor.LuaUnityBindingPrecompiler.GenerateFromCommandLine',
    '-lunilBindingOutput', $OutputAssetPath, '-logFile', $log)
$quotedArguments = @($arguments | ForEach-Object {
    if ($_ -match '[\s"]') { '"' + $_.Replace('"', '\"') + '"' } else { $_ }
})
$start = [Diagnostics.ProcessStartInfo]::new()
$start.FileName = $editor
$start.Arguments = $quotedArguments -join ' '
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$process = [Diagnostics.Process]::Start($start)
try {
    $process.WaitForExit()
    $exitCode = $process.ExitCode
}
finally {
    $process.Dispose()
}
if ($exitCode -ne 0) {
    $tail = if (Test-Path -LiteralPath $log) { (Get-Content $log -Tail 120) -join "`n" } else { '<missing log>' }
    throw "Unity binding generation failed with exit $exitCode.`n$tail"
}
$output = Join-Path $project $OutputAssetPath
if (-not (Test-Path -LiteralPath $output -PathType Leaf)) {
    throw "Unity binding generation did not create $output."
}
$output
