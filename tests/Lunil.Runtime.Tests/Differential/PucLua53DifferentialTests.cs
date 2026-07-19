using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Lunil.Compiler;
using Lunil.Core;
using Lunil.IR.Lua53;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Differential;

public sealed class PucLua53DifferentialTests
{
    private static readonly string[] Scripts =
    [
        "return 5 & 3, 8 >> 1, 5 // 2, -3 % 2",
        "return '12' + 3, '12' | 3, 1 << 64",
        "local sum = 0; for i = 1, 5 do sum = sum + i end; return sum",
        "local function iterator(limit, control) control = control + 1; if control <= limit then return control, control * 2 end end; local sum = 0; for k, v in iterator, 4, 0 do sum = sum + k + v end; return sum",
        "local function collect(prefix, ...) local values = {...}; return prefix .. values[1] .. values[2], #values end; return collect('x', 'a', 'b')",
        "local function outer(value) return function(delta) value = value + delta; return value end end; local next = outer(40); return next(1), next(1)",
        "local values = {10, 20, 30, key = 7}; return values[2], values.key, #values",
        "local x = 3; if x < 4 and x ~= 2 then x = x + 10 end; goto done; x = 0; ::done:: return x",
    ];

    [Fact]
    public void Lua53SourceCorpusMatchesPucLua53WhenOracleIsConfigured()
    {
        var executable = FindLua53Executable();
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
    public void ImportedPucLua53ChunksMatchSourceOracleWhenCompilerIsConfigured()
    {
        var executable = FindLua53Executable();
        var compiler = FindLuac53Executable(executable);
        if (executable is null || compiler is null)
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"lunil-lua53-{Guid.NewGuid():N}");
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

    private static string? FindLua53Executable()
    {
        var configured = Environment.GetEnvironmentVariable("LUNIL_PUC_LUA53");
        return FindExecutable([configured, "lua5.3", "lua53"], "Lua 5.3");
    }

    private static string? FindLuac53Executable(string? luaExecutable)
    {
        var configured = Environment.GetEnvironmentVariable("LUNIL_PUC_LUAC53");
        var sibling = string.IsNullOrWhiteSpace(luaExecutable)
            ? null
            : Path.Combine(
                Path.GetDirectoryName(Path.GetFullPath(luaExecutable))!,
                OperatingSystem.IsWindows() ? "luac.exe" : "luac");
        return FindExecutable([configured, sibling, "luac5.3", "luac53"], "Lua 5.3");
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

    [Fact]
    public void LunilProducedLua53ChunksExecuteInPucLua53WhenOracleIsConfigured()
    {
        var executable = FindLua53Executable();
        if (executable is null)
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"lunil-lua53-writer-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            for (var index = 0; index < Scripts.Length; index++)
            {
                var compiler = new LuaCompiler(new LuaCompilerOptions
                {
                    LanguageVersion = LuaLanguageVersion.Lua53,
                });
                var compilation = compiler.CompileUtf8(Scripts[index], $"@writer-case-{index}.lua");
                Assert.True(
                    compilation.Succeeded,
                    string.Join(Environment.NewLine, compilation.Diagnostics));
                var chunkPath = Path.Combine(directory, $"case-{index}.luac");
                File.WriteAllBytes(
                    chunkPath,
                    Lua53CanonicalPrototypeWriter.Write(
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
            "if kind == 'number' then return math.type(v) .. ':' .. string.format('%.17g', v) " +
            "elseif kind == 'string' then return 'string:' .. #v .. ':' .. v " +
            "else return kind .. ':' .. tostring(v) end end; ";
        return encode + $"local results = table.pack({invocation}); " +
            "for i = 1, results.n do if i > 1 then io.write('\\t') end; io.write(encode(results[i])) end";
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
            throw new InvalidOperationException("Could not start the Lua 5.3 oracle.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        var input = startInfo.Environment.TryGetValue("LUNIL_DIFFERENTIAL_CHUNK", out var chunk)
            ? chunk
            : "source";
        Assert.True(
            process.ExitCode == 0,
            $"Lua 5.3 oracle exited with code {process.ExitCode} while executing {input}." +
            $"{Environment.NewLine}{output}{error}");
        return output;
    }

    private static void CompileChunk(string executable, string sourcePath, string chunkPath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-o");
        startInfo.ArgumentList.Add(chunkPath);
        startInfo.ArgumentList.Add(sourcePath);
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the Lua 5.3 compiler.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
    }

    private static string RunLunilSource(string source)
    {
        var compiler = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });
        var compilation = compiler.CompileUtf8(source, "@lua53-differential.lua");
        Assert.True(compilation.Succeeded, string.Join(Environment.NewLine, compilation.Diagnostics));

        var state = CreateState();
        return Execute(state, state.CreateMainClosure(compilation.Module!));
    }

    private static string RunLunilChunk(byte[] chunk)
    {
        var state = CreateState();
        return Execute(state, state.LoadBinaryChunk(chunk));
    }

    private static LuaState CreateState() => new(new LuaStateOptions
    {
        LanguageVersion = LuaLanguageVersion.Lua53,
    });

    private static string Execute(LuaState state, LuaClosure closure)
    {
        var execution = new LuaInterpreter().Execute(state, closure);
        Assert.Equal(LuaVmSignal.Completed, execution.Signal);
        return string.Join('\t', execution.Values.ToArray().Select(Encode));
    }

    private static string Encode(LuaValue value) => value.Kind switch
    {
        LuaValueKind.Nil => "nil:nil",
        LuaValueKind.Boolean => $"boolean:{(value.IsTruthy ? "true" : "false")}",
        LuaValueKind.Integer => $"integer:{value.AsInteger().ToString(CultureInfo.InvariantCulture)}",
        LuaValueKind.Float => $"float:{value.AsFloat().ToString("G17", CultureInfo.InvariantCulture)}",
        LuaValueKind.String => $"string:{value.AsString().Length}:{value.AsString()}",
        _ => $"{value.Kind.ToString().ToLowerInvariant()}:{value}",
    };
}
