using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using Lunil.Compiler;
using Lunil.Core;
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
    ];

    [Fact]
    public void Lua53CorpusMatchesPucLua53WhenOracleIsConfigured()
    {
        var executable = PucLua53Executable();
        if (executable is null)
        {
            return;
        }

        foreach (var source in Scripts)
        {
            Assert.Equal(RunOracle(executable, source), RunLunil(source));
        }
    }

    private static string? PucLua53Executable()
    {
        var configured = Environment.GetEnvironmentVariable("LUNIL_PUC_LUA53");
        foreach (var candidate in new[] { configured, "lua5.3", "lua53" })
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
                    .Contains("Lua 5.3", StringComparison.Ordinal))
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

    private static string RunOracle(string executable, string source)
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
        startInfo.ArgumentList.Add($"local a,b,c,d = (function() {source} end)(); print(a,b,c,d)");
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the Lua 5.3 oracle.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.TrimEnd('\r', '\n');
    }

    private static string RunLunil(string source)
    {
        var compiler = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });
        var compilation = compiler.CompileUtf8(source, "@lua53-differential.lua");
        Assert.True(compilation.Succeeded, string.Join(Environment.NewLine, compilation.Diagnostics));

        var state = new LuaState(new LuaStateOptions
        {
            LanguageVersion = LuaLanguageVersion.Lua53,
        });
        var execution = new LuaInterpreter().Execute(
            state,
            state.CreateMainClosure(compilation.Module!));
        Assert.Equal(LuaVmSignal.Completed, execution.Signal);
        return string.Join('\t', execution.Values.ToArray().Select(Encode));
    }

    private static string Encode(LuaValue value) => value.Kind switch
    {
        LuaValueKind.Nil => "nil",
        LuaValueKind.Boolean => value.IsTruthy ? "true" : "false",
        LuaValueKind.Integer => value.AsInteger().ToString(CultureInfo.InvariantCulture),
        LuaValueKind.Float => value.AsFloat().ToString("G17", CultureInfo.InvariantCulture),
        _ => value.ToString(),
    };
}
