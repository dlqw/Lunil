using System.Runtime.InteropServices;
using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.StandardLibrary;
using Lunil.Syntax.Parsing;
using System.Linq;

[assembly: LuaFfiGenerateBinding("genlib", "add", "i32(i32,i32)", "platform")]
[assembly: LuaFfiGenerateBinding("genlib", "measure", "f64(f64, f64)", "platform")]
[assembly: LuaFfiGenerateBinding("genlib", "flag", "bool()", "platform")]
[assembly: LuaFfiGenerateBinding("genlib", "greet_length", "i32(cstring)", "platform")]

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaFfiGeneratedBindingTests
{
    // UnmanagedCallersOnly entries expose true native entry points, so the generated
    // delegates can bind them exactly like real native library exports.
    [UnmanagedCallersOnly]
    private static int AddNative(int left, int right) => left + right;

    [UnmanagedCallersOnly]
    private static double MeasureNative(double left, double right) => left * right + 0.5;

    [UnmanagedCallersOnly]
    private static byte FlagNative() => 1;

    [UnmanagedCallersOnly]
    private static int GreetLengthNative(IntPtr text) =>
        Marshal.PtrToStringAnsi(text)?.Length ?? 0;

    [Fact]
    public void GeneratedBindingsInvokeThroughTypedDelegates()
    {
        var registry = new LuaFfiBindingRegistry();
        new Lunil.Generated.LuaFfiGeneratedBindings().RegisterBindings(registry);
        var bindings = registry.GetBindings();
        Assert.Equal(4, bindings.Length);
        Assert.All(bindings, binding => Assert.NotNull(binding.AddressedInvoker));

        var options = new LuaStandardLibraryOptions
        {
            Ffi = new LuaFfiOptions
            {
                Enabled = true,
                AllowedLibraryNames = ["genlib"],
                AllowedSymbolNames =
                [
                    "genlib!add", "genlib!measure", "genlib!flag", "genlib!greet_length",
                ],
                BindingRegistry = registry,
                LibraryLoader = new FixedLoader(),
            },
        };

        var result = Execute(
            "local lib=ffi.load('genlib'); " +
            "local add=ffi.bind(lib,'add','i32(i32,i32)'); " +
            "local measure=ffi.bind(lib,'measure','f64(f64,f64)'); " +
            "local flag=ffi.bind(lib,'flag','bool()'); " +
            "local greet=ffi.bind(lib,'greet_length','i32(cstring)'); " +
            "return add(20,22), measure(1.5,2), flag(), greet('hello');",
            options);

        Assert.Equal(42L, result[0].AsInteger());
        Assert.Equal(3.5, result[1].AsFloat());
        Assert.True(result[2].AsBoolean());
        Assert.Equal(5L, result[3].AsInteger());
    }

    [Fact]
    public unsafe void GeneratedAddressedInvokerMarshalsDirectly()
    {
        var registry = new LuaFfiBindingRegistry();
        new Lunil.Generated.LuaFfiGeneratedBindings().RegisterBindings(registry);
        var add = registry.GetBindings().Single(binding => binding.SymbolName == "add");
        var address = (nint)(delegate* unmanaged<int, int, int>)&AddNative;
        var result = add.AddressedInvoker!(address, [20, 22]);
        Assert.Equal(42, result);
    }

    [Fact]
    public void GeneratedBindingsRejectMismatchedSignatures()
    {
        var registry = new LuaFfiBindingRegistry();
        new Lunil.Generated.LuaFfiGeneratedBindings().RegisterBindings(registry);
        var options = new LuaStandardLibraryOptions
        {
            Ffi = new LuaFfiOptions
            {
                Enabled = true,
                AllowedLibraryNames = ["genlib"],
                AllowedSymbolNames = ["genlib!add"],
                BindingRegistry = registry,
                LibraryLoader = new FixedLoader(),
            },
        };

        Assert.Throws<LuaRuntimeException>(() => Execute(
            "local lib=ffi.load('genlib'); " +
            "local add=ffi.bind(lib,'add','i64(i32,i32)'); " +
            "return add(1,2)",
            options));
    }

    private static LuaValue[] Execute(string source, LuaStandardLibraryOptions options)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state, options);
        LuaStandardLibrary.InstallFfi(state, options);
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

    /// <summary>
    /// Resolves the generated symbols to reverse P/Invoke entries of managed delegates,
    /// so the typed marshalling of the generated invokers is exercised without a native
    /// library on every platform.
    /// </summary>
    private sealed class FixedLoader : ILuaFfiLibraryLoader
    {
        public IntPtr Load(string libraryName) => new(1);

        public unsafe IntPtr GetExport(IntPtr libraryHandle, string symbolName) => symbolName switch
        {
            "add" => (nint)(delegate* unmanaged<int, int, int>)&AddNative,
            "measure" => (nint)(delegate* unmanaged<double, double, double>)&MeasureNative,
            "flag" => (nint)(delegate* unmanaged<byte>)&FlagNative,
            "greet_length" => (nint)(delegate* unmanaged<IntPtr, int>)&GreetLengthNative,
            _ => IntPtr.Zero,
        };

        public void Free(IntPtr libraryHandle)
        {
        }
    }
}
