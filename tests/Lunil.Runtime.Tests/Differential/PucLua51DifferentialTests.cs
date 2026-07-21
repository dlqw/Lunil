using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Lunil.Compiler;
using Lunil.Core;
using Lunil.IR.Lua51;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Runtime.Tests.Differential;

public sealed class PucLua51DifferentialTests
{
    private static readonly string[] Scripts =
    [
        "local x = 1.5; local y = 2.25; return x + y, x / y, x ^ 2",
        "local sum = 0; for i = 1, 5 do sum = sum + i end; return sum",
        "local function iterator(limit, control) control = control + 1; if control <= limit then return control end end; local sum = 0; for k in iterator, 4, 0 do sum = sum + k end; return sum",
        "local function outer(value) return function(delta) return value + delta end end; return outer(40)(2)",
        "local values = {10, 20, 30, key = 7}; return values[2], values.key, #values",
        "return (function(...) local values = {...}; return values[1], values[2] end)(7, 8)",
        "local x = 3; if x < 4 and x ~= 2 then x = x + 10 end; return x",
        "local t = {a=1,b=2}; local n=0; for k,v in pairs(t) do n = n + v end; return n",
        "local s = 0; for i,v in ipairs({10,20,30}) do s = s + i + v end; return s",
        "local function f(a, b) return a or b, a and b end; return f(nil, 5), f(3, 4)",
        "local mt = {__add = function(a,b) return a.n + b.n end}; local a=setmetatable({n=2}, mt); local b=setmetatable({n=5}, mt); return a+b",
        "local function rec(n) if n <= 1 then return 1 end return n * rec(n-1) end; return rec(5)",
        "local t={}; for i=1,10 do t[i]=i*i end; return t[1], t[10], #t",
        BuildLongStringScript(),
        BuildLargeConstantScript(),
    ];

    [Fact]
    public void Lua51SourceCorpusMatchesConfiguredPucOracle()
    {
        var executable = FindLua51Executable();
        if (executable is null)
        {
            return;
        }

        foreach (var source in Scripts)
        {
            Assert.Equal(RunOracle(executable, source), RunLunilSource(source));
        }
    }

    [Fact]
    public void ImportedPucLua51ChunksMatchSourceOracleWhenCompilerIsConfigured()
    {
        var executable = FindLua51Executable();
        var compiler = FindLuac51Executable(executable);
        if (executable is null || compiler is null)
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"lunil-lua51-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            for (var index = 0; index < Scripts.Length; index++)
            {
                var sourcePath = Path.Combine(directory, $"case-{index}.lua");
                var chunkPath = Path.Combine(directory, $"case-{index}.luac");
                File.WriteAllText(sourcePath, Scripts[index]);
                CompileChunk(compiler, sourcePath, chunkPath);
                Assert.Equal(
                    RunOracle(executable, Scripts[index]),
                    RunLunilChunk(File.ReadAllBytes(chunkPath)));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void LunilProducedLua51ChunksExecuteInPucLua51WhenOracleIsConfigured()
    {
        var executable = FindLua51Executable();
        if (executable is null)
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"lunil-lua51-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            for (var index = 0; index < Scripts.Length; index++)
            {
                var compilation = new LuaCompiler(new LuaCompilerOptions
                {
                    LanguageVersion = LuaLanguageVersion.Lua51,
                }).CompileUtf8(Scripts[index], $"@writer-case-{index}.lua");
                Assert.True(
                    compilation.Succeeded,
                    string.Join(Environment.NewLine, compilation.Diagnostics));
                var chunkPath = Path.Combine(directory, $"case-{index}.luac");
                File.WriteAllBytes(
                    chunkPath,
                    Lua51CanonicalPrototypeWriter.Write(
                        compilation.Module!,
                        compilation.Module!.MainFunctionId));

                Assert.Equal(
                    RunOracle(executable, Scripts[index]),
                    RunOracleChunk(executable, chunkPath));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string? FindLua51Executable()
    {
        var configured = Environment.GetEnvironmentVariable("LUNIL_PUC_LUA51");
        return FindExecutable([configured, "lua5.1", "lua51"], "Lua 5.1");
    }

    private static string? FindLuac51Executable(string? luaExecutable)
    {
        var configured = Environment.GetEnvironmentVariable("LUNIL_PUC_LUAC51");
        var sibling = string.IsNullOrWhiteSpace(luaExecutable)
            ? null
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(luaExecutable))!,
                OperatingSystem.IsWindows() ? "luac.exe" : "luac");
        return FindExecutable([configured, sibling, "luac5.1", "luac51"], "Lua 5.1");
    }

    private static string? FindExecutable(IEnumerable<string?> candidates, string versionMarker)
    {
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = "-v",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit(5_000);
                if (process is { ExitCode: 0 } &&
                    (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd())
                    .Contains(versionMarker, StringComparison.Ordinal))
                {
                    return candidate;
                }
            }
            catch (Exception exception) when (
                exception is Win32Exception or InvalidOperationException)
            {
                // Try the next configured executable.
            }
        }

        return null;
    }

    private static string RunLunilSource(string source)
    {
        var compilation = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua51,
        }).CompileUtf8(source, "@lua51-differential.lua");
        Assert.True(compilation.Succeeded, string.Join(Environment.NewLine, compilation.Diagnostics));

        var state = CreateState();
        return Execute(state, state.CreateMainClosure(compilation.Module!));
    }

    private static string RunLunilChunk(byte[] chunk)
    {
        var state = CreateState();
        return Execute(state, state.LoadBinaryChunk(chunk));
    }

    private static LuaState CreateState()
    {
        var state = new LuaState(new LuaStateOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua51,
        });
        LuaStandardLibrary.InstallAll(state);
        return state;
    }

    private static string Execute(LuaState state, LuaClosure closure)
    {
        var result = new LuaInterpreter().Execute(state, closure);
        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        return string.Join('\t', result.Values.ToArray().Select(Encode));
    }

    private static string Encode(LuaValue value) => value.Kind switch
    {
        LuaValueKind.Nil => "nil:nil",
        LuaValueKind.Boolean => $"boolean:{(value.IsTruthy ? "true" : "false")}",
        LuaValueKind.Integer => $"number:{value.AsInteger().ToString(CultureInfo.InvariantCulture)}",
        LuaValueKind.Float => $"number:{value.AsFloat().ToString("G17", CultureInfo.InvariantCulture)}",
        LuaValueKind.String => $"string:{value.AsString().Length}:{value.AsString()}",
        _ => $"{value.Kind.ToString().ToLowerInvariant()}:{value}",
    };

    private static string RunOracle(string executable, string source) =>
        RunOracleProgram(executable, BuildEncodingProgram($"(function() {source} end)()"));

    private static string RunOracleChunk(string executable, string chunkPath)
    {
        var startInfo = CreateOracleStartInfo(
            executable,
            BuildEncodingProgram("assert(loadfile(os.getenv('LUNIL_DIFFERENTIAL_CHUNK')))()"));
        startInfo.Environment["LUNIL_DIFFERENTIAL_CHUNK"] = chunkPath;
        return RunOracleProcess(startInfo);
    }

    private static string BuildEncodingProgram(string invocation)
    {
        const string encode = "local function encode(v) local kind = type(v); " +
            "if kind == 'number' then return 'number:' .. string.format('%.17g', v) " +
            "elseif kind == 'string' then return 'string:' .. #v .. ':' .. v " +
            "else return kind .. ':' .. tostring(v) end end; ";
        return encode + $"local results = {{ {invocation} }}; " +
            "for i = 1, #results do if i > 1 then io.write('\\t') end; io.write(encode(results[i])) end";
    }

    private static string RunOracleProgram(string executable, string program) =>
        RunOracleProcess(CreateOracleStartInfo(executable, program));

    private static ProcessStartInfo CreateOracleStartInfo(string executable, string program)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(program);
        return startInfo;
    }

    private static string RunOracleProcess(ProcessStartInfo startInfo)
    {
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the Lua 5.1 oracle.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
        return output;
    }

    private static void CompileChunk(string compiler, string sourcePath, string chunkPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = compiler,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(chunkPath);
        startInfo.ArgumentList.Add(sourcePath);
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the Lua 5.1 compiler.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }

    private static string BuildLongStringScript()
    {
        var value = new string('x', 300);
        return $"local value = [=[{value}]=]; return #value, value == [=[{value}]=]";
    }

    private static string BuildLargeConstantScript() =>
        "local values = {" + string.Join(',', Enumerable.Range(0, 320).Select(index =>
            $"k{index}='value-{index}'")) + "}; " +
        "return values.k0, values.k255, values.k319";
}
