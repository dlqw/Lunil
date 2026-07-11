using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lunil.Core.Text;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.Runtime.Tests.Differential;

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
        var actual = RunLunil(source);

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(Scripts))]
    public void ImportedBinaryChunkMatchesPucLua548ObservableResults(string source)
    {
        if (!IsPucLuaAvailable())
        {
            return;
        }

        Assert.Equal(RunPucLua(source), RunBinaryChunk(source));
    }

    [Fact]
    public void DeterministicCoroutineStateMachineFuzzMatchesPucLua548()
    {
        if (!IsPucLuaAvailable())
        {
            return;
        }

        for (var seed = 0; seed < 16; seed++)
        {
            var source = GenerateCoroutineStateMachine(seed);
            var expected = RunPucLua(source);
            Assert.Equal(expected, RunLunil(source));
            Assert.Equal(expected, RunBinaryChunk(source));
        }
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
        local stringLimitCount = 0
        for i = 0x7ffffffffffffffe, "9223372036854775807" do
            stringLimitCount = stringLimitCount + 1
        end
        emit(decimal + 3, -decimal, hexadecimal * 2, count, stringLimitCount)
        """,
        """
        local co = coroutine.create(function(a)
            local self, ismain = coroutine.running()
            local b, c = coroutine.yield(a, nil, coroutine.status(self), ismain,
                coroutine.isyieldable())
            return b, c
        end)
        emit(coroutine.status(co))
        emit(coroutine.resume(co, 3))
        emit(coroutine.status(co))
        emit(coroutine.resume(co, 4, 5))
        emit(coroutine.status(co))
        local wrapped = coroutine.wrap(function(v) return v * 2 end)
        emit(wrapped(6))
        """,
        """
        local outer
        local inner = coroutine.create(function()
            emit(coroutine.status(outer))
            coroutine.yield(8)
            return 9
        end)
        outer = coroutine.create(function()
            emit(coroutine.resume(inner))
            emit(coroutine.resume(inner))
        end)
        emit(coroutine.resume(outer))
        """,
        """
        local seen = false
        local mt = { __close = function(self, err) seen = err ~= nil end }
        local co = coroutine.create(function()
            local value <close> = setmetatable({}, mt)
            return nil + 1
        end)
        local resumed, original = coroutine.resume(co)
        emit(resumed, original ~= nil, seen)
        local closed, closeError = coroutine.close(co)
        emit(closed, closeError ~= nil, seen, coroutine.status(co))
        """,
        """
        local co = coroutine.create(function() return coroutine.yield(2, nil, 3) end)
        emit(coroutine.resume(co))
        emit(coroutine.resume(co, 4, nil, 5))
        local resumed, resumeError = coroutine.resume(co)
        emit(resumed, resumeError ~= nil)
        local wrapped = coroutine.wrap(function() return nil + 1 end)
        local ok, wrapError = pcall(wrapped)
        emit(ok, wrapError ~= nil)
        """,
        """
        local mt = {
            __add = function()
                local resumed = coroutine.yield(6)
                return resumed
            end
        }
        local value = setmetatable({}, mt)
        local co = coroutine.create(function()
            local ok, result = xpcall(function() return value + value end, function(e) return e end)
            return ok, result
        end)
        emit(coroutine.resume(co))
        emit(coroutine.resume(co, 7))
        """,
        """
        local function iterator(limit, control)
            control = control + 1
            if control <= limit then return control, control * 2 end
        end
        local sum = 0
        for key, value in iterator, 4, 0 do
            sum = sum + key + value
        end
        emit(sum)
        """,
        """
        local object = {}
        object.answer = 2
        object[3] = 4
        local key = 5
        object[key] = 6
        function object:add(value) return self.answer + value end
        local left, right, suffix = "a", 1, "c"
        local selected = true and object:add(3) or 0
        local fallback = false and 9 or 7
        emit(selected, fallback, object[3], object[key], #(left .. right .. suffix))
        """,
        """
        local value = 7
        emit(value == 7, value ~= 8, value < 8, value <= 7, value > 6, value >= 7,
            (value & 3), (value | 8), (value ~ 2), value << 2, value >> 1,
            -value, ~value, not value)
        """,
        """
        local value = setmetatable({}, {
            __shl = function() return "left" end,
            __shr = function() return "right" end,
        })
        emit(value << 2, value >> 2, 2 << value)
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

    private static string[] RunLunil(string source)
    {
        var output = new List<string>();
        var state = CreateState(output);
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);
        new LuaInterpreter().Execute(state, state.CreateMainClosure(lowering.Module!));
        return output.ToArray();
    }

    private static string[] RunBinaryChunk(string source)
    {
        var output = new List<string>();
        var state = CreateState(output);
        var binary = CompileWithPucLunil(source);
        new LuaInterpreter().ExecuteBinaryChunk(state, binary);
        return output.ToArray();
    }

    private static LuaState CreateState(List<string> output)
    {
        var state = new LuaState();
        state.InstallProtectedCallFunctions();
        state.InstallCoroutineModule();
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
        return state;
    }

    private static byte[] CompileWithPucLunil(string source)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"luac-differential-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "fixture.lua");
            var outputPath = Path.Combine(directory, "fixture.luac");
            File.WriteAllText(
                sourcePath,
                source,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            var startInfo = new ProcessStartInfo
            {
                FileName = "luac",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            startInfo.ArgumentList.Add("-o");
            startInfo.ArgumentList.Add(outputPath);
            startInfo.ArgumentList.Add(sourcePath);
            using var process = Process.Start(startInfo) ??
                throw new InvalidOperationException("Could not start PUC luac.");
            var error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert.True(process.ExitCode == 0, error);
            return File.ReadAllBytes(outputPath);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string GenerateCoroutineStateMachine(int seed)
    {
        var random = new Random(seed);
        var source = new StringBuilder(
            "local threads = {}\n" +
            "for i = 1, 8 do threads[i] = coroutine.create(function(v) " +
            "local a = coroutine.yield(v, i); " +
            "local b = coroutine.yield(a, i * 2); return b, i * 3 end) end\n");
        for (var action = 0; action < 96; action++)
        {
            var thread = random.Next(1, 9);
            switch (random.Next(3))
            {
                case 0:
                    source.Append(CultureInfo.InvariantCulture, $"emit(coroutine.resume(threads[{thread}], {action}))\n");
                    break;
                case 1:
                    source.Append(CultureInfo.InvariantCulture, $"emit(coroutine.status(threads[{thread}]))\n");
                    break;
                default:
                    source.Append(CultureInfo.InvariantCulture, $"emit(coroutine.close(threads[{thread}]))\n");
                    break;
            }
        }

        return source.ToString();
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
