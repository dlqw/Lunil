// Target Frameworks: net10.0
#nullable enable

namespace Lunil.Hosting
{
    public sealed class LuaBufferedConsole : Lunil.StandardLibrary.ILuaConsole
    {
        public LuaBufferedConsole(System.ReadOnlySpan<byte> standardInput = null) { }
        public byte[] ReadStandardInput() => throw null;
        public void Write(System.ReadOnlyMemory<byte> bytes) { }
        public void WriteLine() { }
        public void WriteError(System.ReadOnlyMemory<byte> bytes) { }
        public byte[] GetStandardOutput() => throw null;
        public byte[] GetStandardError() => throw null;
        public void Clear() { }
    }

    public sealed class LuaHost : System.IDisposable
    {
        public Lunil.Hosting.LuaHostOptions Options { get => throw null; }
        public bool IsDynamicCodeAvailable { get => throw null; }
        public Lunil.Hosting.LuaHostExecutionBackend SelectedExecutionBackend { get => throw null; }
        public Lunil.CodeGen.Cil.Jit.LuaJitStatistics? JitStatistics { get => throw null; }
        public Lunil.Compiler.LuaCompiler Compiler { get => throw null; }
        public Lunil.Workspace.LuaWorkspace Workspace { get => throw null; }
        public Lunil.Runtime.LuaStateOptions StateOptions { get => throw null; }
        public Lunil.Runtime.LuaState State { get => throw null; }
        public Lunil.StandardLibrary.LuaStandardLibraryOptions? StandardLibraryOptions { get => throw null; }
        public Lunil.Hosting.LuaBufferedConsole? BufferedConsole { get => throw null; }
        public LuaHost(Lunil.Hosting.LuaHostOptions? options = null) { }
        public Lunil.Compiler.LuaCompilationResult Compile(Lunil.Compiler.LuaSourceDocument source, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Compiler.LuaCompilationResult CompileUtf8(string source, string? sourceName = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Hosting.LuaHostRunResult Run(Lunil.Compiler.LuaSourceDocument source, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Hosting.LuaHostRunResult RunUtf8(string source, string? sourceName = null, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Execute(Lunil.Compiler.LuaCompilationResult compilation, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Execute(Lunil.IR.Canonical.LuaIrModule module, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult ExecuteBinaryChunk(System.ReadOnlySpan<byte> binaryChunk, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null, Lunil.IR.Lua54.Lua54ChunkReaderOptions? readerOptions = null) => throw null;
        public System.Threading.Tasks.Task<Lunil.Workspace.LuaWorkspaceResult> AnalyzeWorkspaceAsync(System.Collections.Generic.IEnumerable<Lunil.Workspace.LuaWorkspaceDocument> roots, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public void Dispose() { }
    }

    public static class LuaHostCapabilityProfiles
    {
        public static Lunil.StandardLibrary.LuaStandardLibraryOptions Create(Lunil.Hosting.LuaHostProfile profile) => throw null;
    }

    public enum LuaHostExecutionBackend
    {
        Auto = 0,
        Interpreter = 1,
        Jit = 2
    }

    public sealed class LuaHostOptions : System.IEquatable<Lunil.Hosting.LuaHostOptions>
    {
        public static Lunil.Hosting.LuaHostOptions Default { get => throw null; }
        public static Lunil.Hosting.LuaHostOptions Trusted { get => throw null; }
        public static Lunil.Hosting.LuaHostOptions Restricted { get => throw null; }
        public static Lunil.Hosting.LuaHostOptions Deterministic { get => throw null; }
        public Lunil.Hosting.LuaHostProfile Profile { get => throw null; init { } }
        public Lunil.Compiler.LuaCompilerOptions Compiler { get => throw null; init { } }
        public Lunil.Runtime.LuaStateOptions State { get => throw null; init { } }
        public Lunil.Runtime.Execution.LuaInterpreterOptions Execution { get => throw null; init { } }
        public Lunil.Hosting.LuaHostExecutionBackend ExecutionBackend { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions Jit { get => throw null; init { } }
        public Lunil.Workspace.LuaWorkspaceOptions Workspace { get => throw null; init { } }
        public Lunil.Workspace.ILuaModuleResolver? ModuleResolver { get => throw null; init { } }
        public bool InstallStandardLibrary { get => throw null; init { } }
        public Lunil.StandardLibrary.LuaStandardLibraryOptions? StandardLibrary { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaHostOptions? left, Lunil.Hosting.LuaHostOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaHostOptions? left, Lunil.Hosting.LuaHostOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaHostOptions? other) => throw null;
    }

    public enum LuaHostProfile
    {
        Trusted = 0,
        Restricted = 1,
        Deterministic = 2
    }

    public sealed class LuaHostRunResult : System.IEquatable<Lunil.Hosting.LuaHostRunResult>
    {
        public Lunil.Compiler.LuaCompilationResult Compilation { get => throw null; init { } }
        public Lunil.Runtime.Execution.LuaExecutionResult? Execution { get => throw null; init { } }
        public bool CompilationSucceeded { get => throw null; }
        public bool ExecutionStarted { get => throw null; }
        public bool Succeeded { get => throw null; }
        public LuaHostRunResult(Lunil.Compiler.LuaCompilationResult Compilation, Lunil.Runtime.Execution.LuaExecutionResult? Execution) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaHostRunResult? left, Lunil.Hosting.LuaHostRunResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaHostRunResult? left, Lunil.Hosting.LuaHostRunResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaHostRunResult? other) => throw null;
        public void Deconstruct(out Lunil.Compiler.LuaCompilationResult Compilation, out Lunil.Runtime.Execution.LuaExecutionResult? Execution) => throw null;
    }
}
