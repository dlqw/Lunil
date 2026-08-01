using Lunil.Compiler;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.StandardLibrary;

namespace Lunil.FfiSmoke.Fixture;

/// <summary>
/// Platform FFI smoke for the dynamic loader path: loads the system math library through the
/// default <see cref="SystemLuaFfiLibraryLoader"/>, binds <c>fabs</c> with a <c>f64(f64)</c>
/// signature, calls it from Lua, and verifies the result. Intended to run on Linux (glibc and
/// musl/Alpine), where the loader must resolve the platform <c>dl</c> interface (including the
/// <c>libdl.so.2</c> to <c>libdl.so</c> fallback on musl distributions).
/// </summary>
public static class Program
{
    public static int Main()
    {
        if (!VerifyFfiDynamic())
        {
            Console.Error.WriteLine("The dynamic FFI platform smoke failed.");
            return 9;
        }

        Console.WriteLine("FFI_SMOKE_OK");
        return 0;
    }

    private static bool VerifyFfiDynamic()
    {
        var options = new LuaStandardLibraryOptions
        {
            Ffi = new LuaFfiOptions
            {
                Enabled = true,
                // Platform library naming differs: glibc resolves "libm" (libm.so.6), while
                // musl/Alpine exposes the math interface as libm.so.1 (a libc.musl alias) and
                // does not resolve the bare "libm" name.
                AllowedLibraryNames = ["libm", "libm.so.1", "libm.so.6"],
                AllowedSymbolNames = ["libm!fabs", "libm.so.1!fabs", "libm.so.6!fabs"],
            },
        };
        var state = new LuaState();
        LuaStandardLibrary.InstallBasic(state, options);
        LuaStandardLibrary.InstallFfi(state, options);
        var result = new LuaInterpreter().Execute(
            state,
            state.CreateMainClosure(Compile(
                "local lib\n" +
                "for _, name in ipairs({'libm', 'libm.so.1', 'libm.so.6'}) do\n" +
                "  local ok, loaded = pcall(ffi.load, name)\n" +
                "  if ok then lib = loaded break end\n" +
                "end\n" +
                "if lib == nil then error('no libm variant could be loaded') end\n" +
                "local fabs = ffi.bind(lib, 'fabs', 'f64(f64)')\n" +
                "local value = fabs(-5.0)\n" +
                "ffi.close(lib)\n" +
                "return value")));
        return result.Values[0].AsInteger() == 5;
    }

    private static LuaIrModule Compile(string source)
    {
        var compilation = new LuaCompiler().CompileUtf8(source, "=ffi-smoke");
        return compilation.Module ?? throw new InvalidOperationException(
            string.Join(
                "; ",
                compilation.Diagnostics.Select(static diagnostic => diagnostic.Message)));
    }
}
