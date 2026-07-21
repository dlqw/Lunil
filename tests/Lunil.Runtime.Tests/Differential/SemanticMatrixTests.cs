using System.Text.Json;
using Lunil.Compiler;
using Lunil.Core;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.Runtime.Tests.Differential;

/// <summary>
/// Executable semantic-matrix gate for Lunil and optional PUC peers declared in
/// benchmarks/cross-runtime/semantic-matrix.json.
/// </summary>
public sealed class SemanticMatrixTests
{
    private static readonly string MatrixPath = Path.Combine(
        AppContext.BaseDirectory,
        "Fixtures",
        "semantic-matrix.json");

    [Fact]
    public void SemanticMatrixManifestIsPinnedAndComplete()
    {
        Assert.True(File.Exists(MatrixPath), MatrixPath);
        using var doc = JsonDocument.Parse(File.ReadAllText(MatrixPath));
        var root = doc.RootElement;
        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.True(root.GetProperty("engines").GetArrayLength() >= 10);
        Assert.True(root.GetProperty("workloads").GetArrayLength() >= 5);
        Assert.True(root.GetProperty("comparisonPolicy").GetProperty("compareOnlyWithinSemanticGroup").GetBoolean());
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51)]
    [InlineData(LuaLanguageVersion.Lua52)]
    [InlineData(LuaLanguageVersion.Lua53)]
    [InlineData(LuaLanguageVersion.Lua54)]
    [InlineData(LuaLanguageVersion.Lua55)]
    public void LunilLanguageVersionSurfaceWorkload(LuaLanguageVersion version)
    {
        var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
        LuaStandardLibrary.InstallAll(state);
        var features = LuaVersionFeatureTable.Get(version);
        Assert.Equal(features.HasUtf8Library, state.GetGlobal("utf8").Kind == LuaValueKind.Table);
        Assert.Equal(features.HasBit32Library, state.GetGlobal("bit32").Kind == LuaValueKind.Table);
        Assert.Equal(features.HasWarnLibrary, state.GetGlobal("warn").Kind == LuaValueKind.Function);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51)]
    [InlineData(LuaLanguageVersion.Lua52)]
    [InlineData(LuaLanguageVersion.Lua53)]
    [InlineData(LuaLanguageVersion.Lua54)]
    [InlineData(LuaLanguageVersion.Lua55)]
    public void LunilErrorPropagationWorkload(LuaLanguageVersion version)
    {
        var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
        LuaStandardLibrary.InstallAll(state);
        var compilation = new LuaCompiler(new LuaCompilerOptions { LanguageVersion = version })
            .CompileUtf8(
                "local ok, err = pcall(function() error('boom') end); return ok, type(err), tostring(err)",
                "@semantic-error.lua");
        Assert.True(compilation.Succeeded);
        var result = new LuaInterpreter().Execute(state, state.CreateMainClosure(compilation.Module!));
        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.False(result.Values[0].IsTruthy);
        Assert.Equal("string", result.Values[1].AsString().ToString());
        Assert.Contains("boom", result.Values[2].AsString().ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua54)]
    [InlineData(LuaLanguageVersion.Lua55)]
    public void CoroutineCloseWorkloadWhenAvailable(LuaLanguageVersion version)
    {
        var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
        LuaStandardLibrary.InstallAll(state);
        var compilation = new LuaCompiler(new LuaCompilerOptions { LanguageVersion = version })
            .CompileUtf8(
                "local co = coroutine.create(function() coroutine.yield(1) end); " +
                "coroutine.resume(co); local ok = coroutine.close(co); return ok, coroutine.status(co)",
                "@semantic-close.lua");
        Assert.True(compilation.Succeeded, string.Join(Environment.NewLine, compilation.Diagnostics));
        var result = new LuaInterpreter().Execute(state, state.CreateMainClosure(compilation.Module!));
        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.True(result.Values[0].IsTruthy);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51)]
    [InlineData(LuaLanguageVersion.Lua52)]
    [InlineData(LuaLanguageVersion.Lua53)]
    [InlineData(LuaLanguageVersion.Lua54)]
    [InlineData(LuaLanguageVersion.Lua55)]
    public void TableStringBoundaryWorkload(LuaLanguageVersion version)
    {
        var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
        LuaStandardLibrary.InstallAll(state);
        var compilation = new LuaCompiler(new LuaCompilerOptions { LanguageVersion = version })
            .CompileUtf8(
                "local t = { 'a', 'b', 'c' }; return table.concat(t, '-'), string.sub('hello', 2, 4)",
                "@semantic-table-string.lua");
        Assert.True(compilation.Succeeded);
        var result = new LuaInterpreter().Execute(state, state.CreateMainClosure(compilation.Module!));
        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.Equal("a-b-c", result.Values[0].AsString().ToString());
        Assert.Equal("ell", result.Values[1].AsString().ToString());
    }
}
