using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Lunil.Compiler;
using Lunil.Core;
using Lunil.IR.Lua52;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Runtime.Tests.Differential;

public sealed class PucLua52DifferentialTests
{
    private static readonly string[] Scripts =
    [
        "local x = 1.5; local y = 2.25; return x + y, x / y, x ^ 2",
        "local sum = 0; for i = 1, 5 do sum = sum + i end; return sum",
        "local function outer(value) return function(delta) return value + delta end end; return outer(40)(2)",
        "local values = {10, 20, 30, key = 7}; return values[2], values.key, #values",
        "local function iterator(limit, control) control = control + 1; if control <= limit then return control end end; local sum = 0; for k in iterator, 4, 0 do sum = sum + k end; return sum",
        "return (function(...) local values = {...}; return values[1], values[2] end)(7, 8)",
        "goto done; ::done:: return 9",
        "local _ENV = {x = 3}; return x",
        "local s = 0; for i,v in ipairs({1,2,3}) do s = s + v end; return s, rawlen({1,2,3,4})",
        "local function rec(n) if n==0 then return 1 end return n*rec(n-1) end; return rec(6)",
        "local a,b = 2,3; repeat a = a + 1 until a > 5; return a, b * 2",
    ];

    [Fact]
    public void Lua52SourceCorpusMatchesConfiguredPucOracle()
    {
        var executable = FindExecutable(
            Environment.GetEnvironmentVariable("LUNIL_PUC_LUA52"),
            "lua5.2",
            "lua52");
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
    public void Lua52ImportedAndProducedChunksMatchConfiguredPucOracle()
    {
        var executable = FindExecutable(
            Environment.GetEnvironmentVariable("LUNIL_PUC_LUA52"),
            "lua5.2",
            "lua52");
        var compiler = FindExecutable(
            Environment.GetEnvironmentVariable("LUNIL_PUC_LUAC52"),
            executable is null ? null : Path.Combine(Path.GetDirectoryName(executable)!, "luac.exe"),
            "luac5.2",
            "luac52");
        if (executable is null || compiler is null)
        {
            return;
        }

        var directory = Path.Combine(Path.GetTempPath(), $"lunil-lua52-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var (source, index) in Scripts.Select((value, index) => (value, index)))
            {
                var sourcePath = Path.Combine(directory, $"case-{index}.lua");
                var chunkPath = Path.Combine(directory, $"case-{index}.luac");
                File.WriteAllText(sourcePath, source);
                CompileChunk(compiler, sourcePath, chunkPath);
                Assert.Equal(RunLunilSource(source), RunLunilChunk(File.ReadAllBytes(chunkPath)));

                var module = new LuaCompiler(new LuaCompilerOptions
                {
                    LanguageVersion = LuaLanguageVersion.Lua52,
                }).CompileUtf8(source).Module!;
                var producedPath = Path.Combine(directory, $"produced-{index}.luac");
                File.WriteAllBytes(producedPath, Lua52CanonicalPrototypeWriter.Write(module, 0));
                Assert.Equal(RunOracle(executable, source), RunOracleChunk(executable, producedPath));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string RunLunilSource(string source)
    {
        var compilation = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua52,
        }).CompileUtf8(source);
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
            LanguageVersion = LuaLanguageVersion.Lua52,
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
        LuaValueKind.Boolean => $"boolean:{value.IsTruthy.ToString().ToLowerInvariant()}",
        LuaValueKind.Integer => $"number:{value.AsInteger().ToString(CultureInfo.InvariantCulture)}",
        LuaValueKind.Float => $"number:{value.AsFloat().ToString("G17", CultureInfo.InvariantCulture)}",
        LuaValueKind.String => $"string:{value.AsString().Length}:{value.AsString()}",
        _ => $"{value.Kind.ToString().ToLowerInvariant()}:{value}",
    };

    private static string RunOracle(string executable, string source) =>
        RunOracleProgram(executable,
            "local function e(v) local t=type(v); return t .. ':' .. (t=='number' and string.format('%.17g',v) or tostring(v)) end; " +
            $"local a,b,c,d,e1,f = (function() {source} end)(); " +
            "local r={a,b,c,d,e1,f}; for i=1,#r do if i>1 then io.write('\\t') end; io.write(e(r[i])) end");

    private static string RunOracleChunk(string executable, string path) =>
        RunOracleProgram(executable,
            "local f=assert(loadfile(os.getenv('LUNIL_DIFFERENTIAL_CHUNK'))); " +
            "local a,b,c,d,e1,fv=f(); local r={a,b,c,d,e1,fv}; " +
            "for i=1,#r do local t=type(r[i]); if i>1 then io.write('\\t') end; io.write(t..':'..(t=='number' and string.format('%.17g',r[i]) or tostring(r[i]))) end",
            path);

    private static string RunOracleProgram(string executable, string program, string? chunk = null)
    {
        var start = new ProcessStartInfo
        {
            FileName = executable,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(program);
        if (chunk is not null)
        {
            start.Environment["LUNIL_DIFFERENTIAL_CHUNK"] = chunk;
        }

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, output + error);
        return output;
    }

    private static void CompileChunk(string compiler, string source, string output)
    {
        var start = new ProcessStartInfo
        {
            FileName = compiler,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add("-o");
        start.ArgumentList.Add(output);
        start.ArgumentList.Add(source);
        using var process = Process.Start(start)!;
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
    }

    private static string? FindExecutable(params string?[] candidates)
    {
        foreach (var candidate in candidates.Where(static value => !string.IsNullOrWhiteSpace(value)))
        {
            try
            {
                using var process = Process.Start(new ProcessStartInfo
                {
                    FileName = candidate!,
                    Arguments = "-v",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                process?.WaitForExit(5_000);
                if (process is { ExitCode: 0 })
                {
                    var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                    if (output.Contains("5.2", StringComparison.Ordinal))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception exception) when (exception is Win32Exception or InvalidOperationException)
            {
            }
        }

        return null;
    }
}
