using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.StandardLibrary;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaFfiBufferLifecycleTests
{
    [Fact]
    public void ContextDisposeClosesLiveBuffersAndReturnsTheAllocationBudget()
    {
        var state = new LuaState();
        using var context = new LuaFfiContext(state, CreateOptions());

        var buffer = new LuaFfiBuffer(context, 3000);
        Assert.False(buffer.IsClosed);

        // The 3000-byte buffer consumes most of the 4096-byte budget.
        Assert.Throws<LuaFfiException>(() => context.Reserve(2000));

        context.Dispose();

        // Disposing the context reclaimed the live buffer's native memory and budget.
        Assert.True(buffer.IsClosed);
        context.Reserve(2000);
    }

    [Fact]
    public void FreeReturnsTheAllocationBudgetToLuaScripts()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state, CreateStandardOptions());
        LuaStandardLibrary.InstallFfi(state, CreateStandardOptions());

        var values = Run(state, """
            local buffer = ffi.alloc(3000)
            local ok1, err1 = pcall(ffi.alloc, 2000)
            ffi.free(buffer)
            local ok2, buffer2 = pcall(ffi.alloc, 2000)
            return ok1, ok2, buffer2 ~= nil
            """);

        Assert.Equal(LuaValue.FromBoolean(false), values[0]);
        Assert.Equal(LuaValue.FromBoolean(true), values[1]);
        Assert.Equal(LuaValue.FromBoolean(true), values[2]);
    }

    private static LuaFfiOptions CreateOptions() => new()
    {
        Enabled = true,
        AllowedLibraryNames = ["fixture"],
        AllowedSymbolNames = ["fixture!placeholder"],
        MaximumAllocationBytes = 4096,
    };

    private static LuaStandardLibraryOptions CreateStandardOptions() => new()
    {
        Ffi = CreateOptions(),
    };

    private static LuaValue[] Run(LuaState state, string source)
    {
        var syntax = LuaParser.Parse(
            SourceText.FromUtf8(source),
            parserOptions: new LuaParserOptions { LanguageVersion = state.LanguageVersion });
        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(
                syntax,
                LuaBinderOptions.Default with { LanguageVersion = state.LanguageVersion }));
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values
            .ToArray();
    }
}
