using System.Diagnostics;
using System.Text;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Memory;
using Lunil.Runtime.Values;

namespace Lunil.Runtime.Tests.Execution;

public sealed class LuaBinaryChunkExecutionTests
{
    [Fact]
    public void ExecutesPucLuaChunkWithClosureVarargsTableAndNumericFor()
    {
        if (!IsPucLunilAvailable())
        {
            return;
        }

        const string source = """
            local function worker(limit, ...)
                local sum = 0
                for index = 1, limit do
                    sum = sum + index
                end
                local values = { ... }
                local object = { answer = sum, values[1] }
                local function add(value)
                    return object.answer + value
                end
                return add(3), object[1]
            end
            return worker(5, 7)
            """;
        var binary = CompileWithPucLunil(source);
        var state = new LuaState();

        var result = new LuaInterpreter().ExecuteBinaryChunk(state, binary);

        Assert.Equal(2, result.Values.Length);
        Assert.Equal(LuaValueKind.Integer, result.Values[0].Kind);
        Assert.Equal(18, result.Values[0].AsInteger());
        Assert.Equal(LuaValueKind.Integer, result.Values[1].Kind);
        Assert.Equal(7, result.Values[1].AsInteger());
    }

    [Fact]
    public void ConverterPreservesPrototypeTreeAndDebugProvenance()
    {
        if (!IsPucLunilAvailable())
        {
            return;
        }

        var binary = CompileWithPucLunil("local x = 1\nreturn function() return x end\n");
        var chunk = Lua54ChunkReader.Read(binary);

        var module = Lua54PrototypeConverter.Convert(chunk);

        Assert.Equal(2, module.Functions.Length);
        Assert.Equal(0, module.Functions[1].ParentFunctionId);
        Assert.Equal(chunk.MainPrototype.Source!.AsSpan(), module.Functions[0].SourceName.AsSpan());
        Assert.Contains(
            module.Functions[0].Instructions,
            instruction => instruction.LogicalProgramCounter >= 0 && instruction.SourceLine > 0);
        Assert.Empty(LuaIrVerifier.Verify(module));
    }

    [Fact]
    public void ExecutesImportedIntegerAndFloatArithmeticWithExactTags()
    {
        if (!IsPucLunilAvailable())
        {
            return;
        }

        var binary = CompileWithPucLunil("local x = 6; return x * 2, 6.5\n");
        var chunk = Lua54ChunkReader.Read(binary);
        Assert.Equal(6.5, chunk.MainPrototype.Constants[1].FloatValue);
        var module = Lua54PrototypeConverter.Convert(chunk);
        var floatConstant = Assert.Single(
            module.Functions[0].Constants,
            constant => constant.Kind == LuaIrConstantKind.Float);
        Assert.Equal(6.5, floatConstant.Float);

        var state = new LuaState();
        var result = new LuaInterpreter().Execute(state, state.CreateMainClosure(module));

        Assert.Equal(LuaValueKind.Integer, result.Values[0].Kind);
        Assert.Equal(12, result.Values[0].AsInteger());
        Assert.Equal(LuaValueKind.Float, result.Values[1].Kind);
        Assert.Equal(6.5, result.Values[1].AsFloat());
    }

    [Fact]
    public void ImportedSuspendedClosureSurvivesLogicalGcStress()
    {
        if (!IsPucLunilAvailable())
        {
            return;
        }

        const string source = """
            return coroutine.create(function()
                local object = { value = 41 }
                local increment = coroutine.yield(object.value)
                return object.value + increment
            end)
            """;
        var state = new LuaState(new LuaStateOptions
        {
            Heap = LuaHeapOptions.Default with { StressEveryAllocation = true },
        });
        state.InstallCoroutineModule();
        var interpreter = new LuaInterpreter();
        var loaded = interpreter.ExecuteBinaryChunk(state, CompileWithPucLunil(source));
        var thread = loaded.Values[0].AsThread();
        using var root = state.CreateHandle(loaded.Values[0]);

        var yielded = interpreter.Resume(state, thread);
        Assert.Equal(LuaVmSignal.Yielded, yielded.Signal);
        Assert.Equal(41, yielded.Values[0].AsInteger());

        state.Heap.CollectFull();
        var completed = interpreter.Resume(state, thread, [LuaValue.FromInteger(1)]);

        Assert.Equal(LuaVmSignal.Completed, completed.Signal);
        Assert.Equal(42, completed.Values[0].AsInteger());
    }

    private static bool IsPucLunilAvailable()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "luac",
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

    private static byte[] CompileWithPucLunil(string source)
    {
        var directory = Path.Combine(Path.GetTempPath(), $"luac-tests-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            var sourcePath = Path.Combine(directory, "fixture.lua");
            var outputPath = Path.Combine(directory, "fixture.luac");
            File.WriteAllText(sourcePath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
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
}
