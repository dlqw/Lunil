[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidateNotNullOrEmpty()]
    [string]$LuaExecutable,

    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9A-Fa-f]{64}$')]
    [string]$ExpectedExecutableSha256
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$corpusDirectory = Join-Path $repositoryRoot 'tests/Lunil.BackendDifferential.Tests/PucLua54'
$goldenPath = Join-Path $corpusDirectory 'goldens.json'
$archivePath = Join-Path $repositoryRoot `
    'tests/Lunil.Conformance.Tests/Fixtures/lua-5.4.8-tests.tar.gz'
$archiveSha256 = '9581D5A7C39FFBF29B8CCDE2709083C380F7BBDDBD968DCB15712D2F2E33F4E5'

$resolvedExecutable = (Resolve-Path -LiteralPath $LuaExecutable).Path
$actualExecutableSha256 = (Get-FileHash -LiteralPath $resolvedExecutable -Algorithm SHA256).Hash
if ($actualExecutableSha256 -ne $ExpectedExecutableSha256.ToUpperInvariant()) {
    throw "PUC-Lua executable SHA-256 mismatch. Expected $ExpectedExecutableSha256; actual $actualExecutableSha256."
}

$actualArchiveSha256 = (Get-FileHash -LiteralPath $archivePath -Algorithm SHA256).Hash
if ($actualArchiveSha256 -ne $archiveSha256) {
    throw "Lua 5.4.8 test archive SHA-256 mismatch. Expected $archiveSha256; actual $actualArchiveSha256."
}

function Invoke-PucLua {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $startInfo = [System.Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $resolvedExecutable
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in $Arguments) {
        [void]$startInfo.ArgumentList.Add($argument)
    }

    $process = [System.Diagnostics.Process]::Start($startInfo)
    $standardOutput = $process.StandardOutput.ReadToEnd()
    $standardError = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        throw "PUC-Lua exited with code $($process.ExitCode). stdout: $standardOutput stderr: $standardError"
    }

    return [pscustomobject]@{
        StandardOutput = $standardOutput
        StandardError = $standardError
    }
}

$versionResult = Invoke-PucLua -Arguments @('-v')
$versionText = ($versionResult.StandardOutput + $versionResult.StandardError).Trim()
if ($versionText -notmatch '^Lua 5\.4\.8(?:\s|$)') {
    throw "The oracle executable did not report Lua 5.4.8: $versionText"
}

$driver = @'
if _VERSION ~= "Lua 5.4" then
    error("expected _VERSION Lua 5.4, got " .. tostring(_VERSION), 0)
end

local function hex(value)
    return (value:gsub(".", function(character)
        return string.format("%02X", string.byte(character))
    end))
end

local function observe(value)
    local kind = type(value)
    if kind == "nil" then
        return "Nil", "nil"
    elseif kind == "boolean" then
        return "Boolean", value and "true" or "false"
    elseif kind == "number" then
        if math.type(value) == "integer" then
            return "Integer", tostring(value)
        end
        return "Float", hex(string.pack(">d", value))
    elseif kind == "string" then
        return "String", hex(value)
    elseif kind == "table" then
        return "Table", "Table"
    elseif kind == "function" then
        return "Function", "Function"
    elseif kind == "thread" then
        return "Thread", "Thread"
    elseif kind == "userdata" then
        return "Userdata", "Userdata"
    end
    error("unsupported PUC-Lua value kind: " .. kind, 0)
end

local chunk, loadError = loadfile(arg[1], "bt")
if not chunk then
    error(loadError, 0)
end

local results = table.pack(pcall(chunk))
if not results[1] then
    error("observable corpus case failed: " .. tostring(results[2]), 0)
end

local fields = { "Completed" }
for index = 2, results.n do
    local kind, representation = observe(results[index])
    fields[#fields + 1] = kind
    fields[#fields + 1] = representation
end
io.write(table.concat(fields, "\t"), "\n")
'@

$temporaryDriver = Join-Path ([System.IO.Path]::GetTempPath()) `
    ("lunil-puc548-golden-{0}.lua" -f [guid]::NewGuid().ToString('N'))

try {
    [System.IO.File]::WriteAllText(
        $temporaryDriver,
        $driver,
        [System.Text.UTF8Encoding]::new($false))

    $cases = foreach ($sourceFile in Get-ChildItem -LiteralPath $corpusDirectory -Filter '*.lua' |
            Sort-Object Name) {
        $result = Invoke-PucLua -Arguments @($temporaryDriver, $sourceFile.FullName)
        $fields = $result.StandardOutput.TrimEnd("`r", "`n").Split("`t")
        if ($fields.Length -lt 1 -or $fields[0] -ne 'Completed' -or
            (($fields.Length - 1) % 2) -ne 0) {
            throw "Invalid oracle output for $($sourceFile.Name): $($result.StandardOutput)"
        }

        $values = for ($index = 1; $index -lt $fields.Length; $index += 2) {
            [ordered]@{
                kind = $fields[$index]
                representation = $fields[$index + 1]
            }
        }

        [ordered]@{
            name = [System.IO.Path]::GetFileNameWithoutExtension($sourceFile.Name)
            file = $sourceFile.Name
            sourceSha256 = (Get-FileHash -LiteralPath $sourceFile.FullName -Algorithm SHA256).Hash
            installStandardLibrary = $true
            values = @($values)
        }
    }

    $document = [ordered]@{
        schemaVersion = 1
        oracle = [ordered]@{
            implementation = 'PUC-Lua'
            version = 'Lua 5.4.8'
            luaVersionGlobal = 'Lua 5.4'
            executableSha256 = $actualExecutableSha256
            sourceArchiveSha256 = $actualArchiveSha256
        }
        cases = @($cases)
    }

    $json = $document | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        $goldenPath,
        $json + "`n",
        [System.Text.UTF8Encoding]::new($false))
    Write-Host "Updated $goldenPath from $versionText ($actualExecutableSha256)."
}
finally {
    Remove-Item -LiteralPath $temporaryDriver -Force -ErrorAction SilentlyContinue
}
