using System.Diagnostics;
using System.Globalization;
using System.Text;
using Luac.Core.Text;
using Luac.Runtime.Execution;
using Luac.Runtime.Values;
using Luac.Semantics.Binding;
using Luac.Semantics.Lowering;
using Luac.Syntax.Parsing;

namespace Luac.Runtime.Tests.Differential;

public sealed class PucLua548DifferentialTests
{
    private const string OraclePrelude = """
        local function encode(value)
            local kind = type(value)
            if kind == "nil" then return "n" end
            if kind == "boolean" then return "b:" .. tostring(value) end
            if kind == "number" then
                local prefix = math.type(value) == "integer" and "i:" or "f:"
                return prefix .. tostring(value)
            end
            if kind == "string" then
                return "s:" .. (value:gsub(".", function(c)
                    return string.format("%02x", string.byte(c))
                end))
            end
            return kind
        end
        function emit(...)
            local values = {}
            for index = 1, select("#", ...) do
                values[index] = encode(select(index, ...))
            end
            print(table.concat(values, "|"))
        end
        """;

    [Theory]
    [MemberData(nameof(Scripts))]
    public void MatchesPucLua548ObservableResults(string source)
    {
        if (!IsPucLuaAvailable())
        {
            return;
        }

        var expected = RunPucLua(source);
        var actual = RunLuac(source);

        Assert.Equal(expected, actual);
    }

    public static TheoryData<string> Scripts => new()
    {
        """
        local sum = 0
        for i = 1, 10 do sum = sum + i end
        emit(sum, -3 // 2, -3 % 2, 1 << 64, 8 >> -1)
        """,
        """
        local function make(value)
            return function(add, ...) value = value + add; return value, ... end
        end
        local f = make(10)
        emit(f(2, 7, 8))
        emit(f(3))
        """,
        """
        local log = ""
        local mt = {}
        function mt.__add(a, b) return a.value + b.value end
        function mt.__close(self, error)
            log = log .. self.name .. (error and "E" or "N")
        end
        local a = setmetatable({ value = 4 }, mt)
        local b = setmetatable({ value = 6 }, mt)
        local ok = pcall(function()
            local first <close> = setmetatable({ name = "a" }, mt)
            local second <close> = setmetatable({ name = "b" }, mt)
            return nil + 1
        end)
        emit(a + b, ok, log)
        """,
        """
        local decimal = "12"
        local hexadecimal = "0x1.ap1"
        local count = 0
        for i = 0x7ffffffffffffffe, 0x7fffffffffffffff do
            count = count + 1
        end
        emit(decimal + 3, -decimal, hexadecimal * 2, count)
        """,
    };

    private static bool IsPucLuaAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "lua",
                Arguments = "-v",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            process?.WaitForExit(5_000);
            return process is { ExitCode: 0 } &&
                (process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd())
                .Contains("Lua 5.4", StringComparison.Ordinal);
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return false;
        }
    }

    private static string[] RunPucLua(string source)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "lua",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-e");
        startInfo.ArgumentList.Add(OraclePrelude + Environment.NewLine + source);
        using var process = Process.Start(startInfo) ??
            throw new InvalidOperationException("Could not start the PUC Lua oracle.");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, error);
        return output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
    }

    private static string[] RunLuac(string source)
    {
        var output = new List<string>();
        var state = new LuaState();
        state.InstallProtectedCallFunctions();
        state.SetGlobal(
            "setmetatable",
            LuaValue.FromFunction(new LuaNativeFunction(
                "setmetatable",
                static (_, arguments) =>
                {
                    arguments[0].AsTable().SetMetatable(arguments[1].AsTable());
                    return [arguments[0]];
                })));
        state.SetGlobal(
            "emit",
            LuaValue.FromFunction(new LuaNativeFunction(
                "emit",
                (_, arguments) =>
                {
                    output.Add(string.Join('|', arguments.ToArray().Select(Encode)));
                    return [];
                })));
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        new LuaInterpreter().Execute(state, state.CreateMainClosure(lowering.Module!));
        return output.ToArray();
    }

    private static string Encode(LuaValue value) => value.Kind switch
    {
        LuaValueKind.Nil => "n",
        LuaValueKind.Boolean => $"b:{value.ToString().ToLowerInvariant()}",
        LuaValueKind.Integer => $"i:{value.AsInteger().ToString(CultureInfo.InvariantCulture)}",
        LuaValueKind.Float => $"f:{value.AsFloat().ToString(CultureInfo.InvariantCulture)}",
        LuaValueKind.String => $"s:{Convert.ToHexString(value.AsString().AsSpan()).ToLowerInvariant()}",
        _ => value.Kind.ToString().ToLowerInvariant(),
    };
}
