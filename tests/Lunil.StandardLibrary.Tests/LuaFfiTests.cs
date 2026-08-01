using System.Runtime.InteropServices;
using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.StandardLibrary;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaFfiTests
{
    [Fact]
    public void SignatureParserAcceptsSupportedAliasesAndRejectsVariadics()
    {
        var signature = LuaFfiSignature.Parse("  i32 ( const char *, usize )  ");

        Assert.Equal(LuaFfiNativeType.Int32, signature.ReturnType);
        Assert.Equal(
            [LuaFfiNativeType.Utf8String, LuaFfiNativeType.UIntPtr],
            signature.ParameterTypes.ToArray());
        Assert.Equal("i32(cstring, usize)", signature.ToString());
        Assert.Throws<LuaFfiException>(() => LuaFfiSignature.Parse("i32(i32, ...)"));
    }

    [Fact]
    public void FfiIsNotInstalledByDefaultAndRejectsNonAllowlistedLibraries()
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallAll(state);

        Assert.True(state.GetGlobal("ffi").IsNil);

        var loader = new CountingLoader();
        LuaStandardLibrary.InstallFfi(state, CreateOptions(loader));
        var values = Run(state, "local ok,e=pcall(ffi.load,'other'); return ok,e");

        Assert.False(values[0].AsBoolean());
        Assert.Contains(nameof(LuaFfiErrorCode.LibraryNotAllowed), values[1].AsString().ToString());
        Assert.Equal(0, loader.LoadCount);
    }

    [Fact]
    public void RegistryBindingInvokesWithoutDynamicSymbolResolutionAndCloseInvalidatesFunction()
    {
        var registry = new LuaFfiBindingRegistry();
        registry.Register(
            "fixture",
            "add",
            "i32(i32, i32)",
            static arguments => checked((int)arguments[0]! + (int)arguments[1]!));
        var loader = new CountingLoader { ThrowOnGetExport = true };
        var options = CreateOptions(loader);
        var state = CreateState(options with
        {
            Ffi = options.Ffi with
            {
                AllowedSymbolNames = ["fixture!add"],
                BindingRegistry = registry,
            },
        });

        var values = Run(
            state,
            "local lib=ffi.load('fixture'); local add=ffi.bind(lib,'add','i32(i32,i32)') " +
            "local result=add(20,22); ffi.close(lib); local ok,e=pcall(add,1,2) " +
            "return result,ok,e");

        Assert.Equal(42, values[0].AsInteger());
        Assert.False(values[1].AsBoolean());
        Assert.Contains(nameof(LuaFfiErrorCode.LibraryClosed), values[2].AsString().ToString());
        Assert.Equal(1, loader.LoadCount);
        Assert.Equal(0, loader.GetExportCount);
        Assert.Equal(1, loader.FreeCount);
    }

    [Fact]
    public void RegistryBindingsMarshalScalarsUtf8PointersAndStableFailures()
    {
        var registry = new LuaFfiBindingRegistry();
        registry.Register(
            "fixture",
            "scalars",
            "f64(f32, bool, i16, u32)",
            static arguments =>
                (double)(float)arguments[0]! +
                ((byte)arguments[1]! == 0 ? 0 : 1) +
                (short)arguments[2]! +
                (uint)arguments[3]!);
        registry.Register(
            "fixture",
            "echo",
            "cstring(cstring)",
            static arguments => Marshal.PtrToStringUTF8((IntPtr)arguments[0]!)!);
        registry.Register(
            "fixture",
            "write",
            "void(pointer)",
            static arguments =>
            {
                var pointer = (IntPtr)arguments[0]!;
                if (pointer != IntPtr.Zero)
                {
                    Marshal.WriteInt32(pointer, 0, 77);
                }

                return null;
            });
        registry.Register(
            "fixture",
            "identity",
            "pointer(pointer)",
            static arguments => arguments[0]);
        registry.Register(
            "fixture",
            "fail",
            "void()",
            static _ => throw new InvalidOperationException("fixture failure"));

        var loader = new CountingLoader();
        var baseOptions = CreateOptions(loader);
        var options = baseOptions with
        {
            Ffi = baseOptions.Ffi with
            {
                BindingRegistry = registry,
                AllowedSymbolNames =
                [
                    "fixture!scalars",
                    "fixture!echo",
                    "fixture!write",
                    "fixture!identity",
                    "fixture!fail",
                ],
            },
        };
        var state = CreateState(options);

        var values = Run(
            state,
            "local lib=ffi.load('fixture'); " +
            "local scalars=ffi.bind(lib,'scalars','f64(f32,bool,i16,u32)'); " +
            "local echo=ffi.bind(lib,'echo','cstring(cstring)'); " +
            "local write=ffi.bind(lib,'write','void(pointer)'); " +
            "local identity=ffi.bind(lib,'identity','pointer(pointer)'); " +
            "local fail=ffi.bind(lib,'fail','void()'); local b=ffi.alloc(8); " +
            "local nilPointer=identity(nil); write(b); local number=ffi.read(b,0,'i32'); " +
            "local text=echo('héllo'); local sum=scalars(1.5,true,-2,7); " +
            "local okOverflow,eOverflow=pcall(scalars,1.5,true,-40000,7); " +
            "local okFailure,eFailure=pcall(fail); ffi.free(b); ffi.close(lib); " +
            "return number,text,sum,nilPointer,okOverflow,eOverflow,okFailure,eFailure");

        Assert.Equal(77, values[0].AsInteger());
        Assert.Equal("héllo", values[1].AsString().ToString());
        Assert.Equal(7.5, values[2].AsFloat(), precision: 6);
        Assert.True(values[3].IsNil);
        Assert.False(values[4].AsBoolean());
        Assert.Contains(nameof(LuaFfiErrorCode.RangeExceeded), values[5].AsString().ToString());
        Assert.False(values[6].AsBoolean());
        Assert.Contains(nameof(LuaFfiErrorCode.NativeInvocationFailed), values[7].AsString().ToString());
    }

    [Fact]
    public void BufferReadWriteBoundsAndRepeatedFreeAreStable()
    {
        var loader = new CountingLoader();
        var options = CreateOptions(loader);
        var state = CreateState(options with
        {
            Ffi = options.Ffi with
            {
                AllowedSymbolNames = ["fixture!unused"],
                MaximumBufferBytes = 64,
                MaximumAllocationBytes = 64,
            },
        });

        var values = Run(
            state,
            "local b=ffi.alloc(32); ffi.write(b,0,'i32',-123); " +
            "ffi.write(b,4,'cstring','hello'); ffi.write(b,16,'usize',1234); " +
            "local i=ffi.read(b,0,'i32'); local s=ffi.read(b,4,'cstring'); " +
            "local u=ffi.read(b,16,'usize'); local ok1,e1=pcall(ffi.read,b,31,'i32'); " +
            "ffi.free(b); ffi.free(b); local ok2,e2=pcall(ffi.read,b,0,'i32'); " +
            "return i,s,u,ok1,e1,ok2,e2");

        Assert.Equal(-123, values[0].AsInteger());
        Assert.Equal("hello", values[1].AsString().ToString());
        Assert.Equal(1234, values[2].AsInteger());
        Assert.False(values[3].AsBoolean());
        Assert.Contains(nameof(LuaFfiErrorCode.RangeExceeded), values[4].AsString().ToString());
        Assert.False(values[5].AsBoolean());
        Assert.Contains(nameof(LuaFfiErrorCode.BufferClosed), values[6].AsString().ToString());
    }

    [Fact]
    public void LibraryAliasesKeepTheNativeHandleAliveUntilTheLastLeaseIsCollected()
    {
        var loader = new CountingLoader();
        var state = CreateState(CreateOptions(loader));

        _ = Run(
            state,
            "first=ffi.load('fixture'); second=ffi.load('fixture'); first=nil; " +
            "collectgarbage('collect'); return second~=nil");

        Assert.Equal(1, loader.LoadCount);
        Assert.Equal(0, loader.FreeCount);

        _ = Run(state, "second=nil; collectgarbage('collect'); return true");

        Assert.Equal(1, loader.FreeCount);
    }

    [Fact]
    public void AllocationAndOpenLibraryBudgetsFailWithoutLeakingResources()
    {
        var loader = new CountingLoader();
        var options = CreateOptions(loader) with
        {
            Ffi = CreateOptions(loader).Ffi with
            {
                AllowedLibraryNames = ["fixture", "other"],
                AllowedSymbolNames = ["fixture!unused", "other!unused"],
                MaximumOpenLibraries = 1,
                MaximumBufferBytes = 8,
                MaximumAllocationBytes = 8,
            },
        };
        var state = CreateState(options);

        var values = Run(
            state,
            "local first=ffi.load('fixture'); local okLibrary,eLibrary=pcall(ffi.load,'other'); " +
            "local b=ffi.alloc(8); local okMemory,eMemory=pcall(ffi.alloc,1); " +
            "ffi.free(b); ffi.close(first); return okLibrary,eLibrary,okMemory,eMemory");

        Assert.False(values[0].AsBoolean());
        Assert.Contains(nameof(LuaFfiErrorCode.ResourceLimitExceeded), values[1].AsString().ToString());
        Assert.False(values[2].AsBoolean());
        Assert.Contains(nameof(LuaFfiErrorCode.AllocationLimitExceeded), values[3].AsString().ToString());
        Assert.Equal(1, loader.LoadCount);
        Assert.Equal(1, loader.FreeCount);
    }

    [Fact]
    public void DynamicWindowsSmokeCallsAllowlistedCAbiFunction()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var state = CreateState(new LuaStandardLibraryOptions
        {
            Ffi = new LuaFfiOptions
            {
                Enabled = true,
                AllowedLibraryNames = ["kernel32.dll"],
                AllowedSymbolNames = ["kernel32.dll!GetCurrentProcessId"],
            },
        });

        var values = Run(
            state,
            "local lib=ffi.load('kernel32.dll'); local pid=ffi.bind(lib,'GetCurrentProcessId','u32()')(); " +
            "ffi.close(lib); return pid");

        Assert.True(values[0].AsInteger() > 0);
    }

    private static LuaStandardLibraryOptions CreateOptions(CountingLoader loader) =>
        new()
        {
            Ffi = new LuaFfiOptions
            {
                Enabled = true,
                AllowedLibraryNames = ["fixture"],
                AllowedSymbolNames = ["fixture!unused"],
                LibraryLoader = loader,
            },
        };

    private static LuaState CreateState(LuaStandardLibraryOptions options)
    {
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state, options);
        LuaStandardLibrary.InstallFfi(state, options);
        return state;
    }

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

    private sealed class CountingLoader : ILuaFfiLibraryLoader
    {
        public int LoadCount { get; private set; }

        public int GetExportCount { get; private set; }

        public int FreeCount { get; private set; }

        public bool ThrowOnGetExport { get; init; }

        public IntPtr Load(string libraryName)
        {
            LoadCount++;
            return new IntPtr(1);
        }

        public IntPtr GetExport(IntPtr libraryHandle, string symbolName)
        {
            GetExportCount++;
            if (ThrowOnGetExport)
            {
                throw new InvalidOperationException("registry-only test reached dynamic lookup");
            }

            return new IntPtr(1);
        }

        public void Free(IntPtr libraryHandle) => FreeCount++;
    }
}
