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

    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2067", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2070", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2072", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2075", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("Trimming", "IL2080", Justification = "The host owns exact CLR interop metadata and preserves its allowlist.")]
    [System.Diagnostics.CodeAnalysis.UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Exact allowlisted types are rooted by the host; delegate expressions use the interpreter on AOT runtimes.")]
    public sealed class LuaClrBridge
    {
        public Lunil.Runtime.LuaState State { get => throw null; }
        public Lunil.Hosting.LuaClrOptions Options { get => throw null; }
        public bool IsEnabled { get => throw null; }
        public int OwnerThreadId { get => throw null; }
        public LuaClrBridge(Lunil.Runtime.LuaState state, Lunil.Hosting.LuaClrOptions? options = null) { }
        public Lunil.Hosting.LuaClrTypeInfo ResolveType(string typeName) => throw null;
        public Lunil.Runtime.Values.LuaUserdata CreateInstance(string typeName, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaClrMemberInfo> ResolveMembers(string typeName) => throw null;
        public Lunil.Runtime.Values.LuaValue GetMember(Lunil.Runtime.Values.LuaValue target, string memberName, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> indexArguments = null) => throw null;
        public void SetMember(Lunil.Runtime.Values.LuaValue target, string memberName, Lunil.Runtime.Values.LuaValue value) { }
        public Lunil.Hosting.LuaClrInvocationResult InvokeMember(Lunil.Runtime.Values.LuaValue target, string memberName, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null, System.ReadOnlySpan<Lunil.Hosting.LuaClrNamedArgument> namedArguments = null) => throw null;
        public Lunil.Hosting.LuaClrInvocationResult InvokeStatic(string typeName, string memberName, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null, System.ReadOnlySpan<Lunil.Hosting.LuaClrNamedArgument> namedArguments = null) => throw null;
        public System.Delegate CreateDelegate(Lunil.Runtime.Values.LuaValue function, string delegateTypeName) => throw null;
        public Lunil.Hosting.LuaClrSubscription Subscribe(Lunil.Runtime.Values.LuaValue target, string eventName, Lunil.Runtime.Values.LuaValue callback) => throw null;
        public Lunil.Runtime.Values.LuaValue Await(Lunil.Runtime.Values.LuaValue value) => throw null;
        public Lunil.Runtime.Values.LuaUserdata CreateCancellation() => throw null;
        public void Cancel(Lunil.Runtime.Values.LuaValue value) { }
        public void DisposeValue(Lunil.Runtime.Values.LuaValue value) { }
        public void InstallGlobalModule() { }
    }

    public sealed class LuaClrCancellation : System.IDisposable
    {
        public System.Threading.CancellationToken Token { get => throw null; }
        public bool IsCancellationRequested { get => throw null; }
        public void Cancel() { }
        public void Dispose() { }
    }

    [System.Flags]
    public enum LuaClrCapabilities
    {
        None = 0,
        TypeDiscovery = 1,
        Construction = 2,
        MemberAccess = 4,
        DelegateConversion = 8,
        EventSubscription = 16,
        Async = 32,
        Disposal = 64
    }

    public sealed class LuaClrConstructorInfo : System.IEquatable<Lunil.Hosting.LuaClrConstructorInfo>
    {
        public System.Collections.Immutable.ImmutableArray<string> ParameterTypeNames { get => throw null; init { } }
        public LuaClrConstructorInfo(System.Collections.Immutable.ImmutableArray<string> ParameterTypeNames) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaClrConstructorInfo? left, Lunil.Hosting.LuaClrConstructorInfo? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaClrConstructorInfo? left, Lunil.Hosting.LuaClrConstructorInfo? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaClrConstructorInfo? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<string> ParameterTypeNames) => throw null;
    }

    public enum LuaClrErrorCode
    {
        CapabilityDenied = 0,
        InvalidTypeName = 1,
        TypeNotAllowed = 2,
        TypeNotFound = 3,
        AmbiguousType = 4,
        TypeNotConstructible = 5,
        NoMatchingConstructor = 6,
        ConstructionFailed = 7,
        MemberNotAllowed = 8,
        MemberNotFound = 9,
        NoMatchingMember = 10,
        InvocationFailed = 11,
        InvalidDelegate = 12,
        SubscriptionClosed = 13,
        AsyncFailed = 14,
        ThreadDenied = 15,
        InvalidRefOut = 16
    }

    public sealed class LuaClrException : System.Exception
    {
        public Lunil.Hosting.LuaClrErrorCode Code { get => throw null; }
        public LuaClrException(Lunil.Hosting.LuaClrErrorCode code, string message) { }
        public LuaClrException(Lunil.Hosting.LuaClrErrorCode code, string message, System.Exception innerException) { }
    }

    public sealed class LuaClrInvocationResult : System.IEquatable<Lunil.Hosting.LuaClrInvocationResult>
    {
        public Lunil.Runtime.Values.LuaValue ReturnValue { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Runtime.Values.LuaValue> RefOutValues { get => throw null; init { } }
        public LuaClrInvocationResult(Lunil.Runtime.Values.LuaValue ReturnValue, System.Collections.Immutable.ImmutableArray<Lunil.Runtime.Values.LuaValue> RefOutValues) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaClrInvocationResult? left, Lunil.Hosting.LuaClrInvocationResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaClrInvocationResult? left, Lunil.Hosting.LuaClrInvocationResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaClrInvocationResult? other) => throw null;
        public void Deconstruct(out Lunil.Runtime.Values.LuaValue ReturnValue, out System.Collections.Immutable.ImmutableArray<Lunil.Runtime.Values.LuaValue> RefOutValues) => throw null;
    }

    public sealed class LuaClrMemberInfo : System.IEquatable<Lunil.Hosting.LuaClrMemberInfo>
    {
        public string Name { get => throw null; init { } }
        public Lunil.Hosting.LuaClrMemberKind Kind { get => throw null; init { } }
        public bool IsStatic { get => throw null; init { } }
        public bool CanRead { get => throw null; init { } }
        public bool CanWrite { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> ParameterTypeNames { get => throw null; init { } }
        public string ReturnTypeName { get => throw null; init { } }
        public LuaClrMemberInfo(string Name, Lunil.Hosting.LuaClrMemberKind Kind, bool IsStatic, bool CanRead, bool CanWrite, System.Collections.Immutable.ImmutableArray<string> ParameterTypeNames, string ReturnTypeName) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaClrMemberInfo? left, Lunil.Hosting.LuaClrMemberInfo? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaClrMemberInfo? left, Lunil.Hosting.LuaClrMemberInfo? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaClrMemberInfo? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.Hosting.LuaClrMemberKind Kind, out bool IsStatic, out bool CanRead, out bool CanWrite, out System.Collections.Immutable.ImmutableArray<string> ParameterTypeNames, out string ReturnTypeName) => throw null;
    }

    public enum LuaClrMemberKind
    {
        Method = 0,
        Property = 1,
        Field = 2,
        Indexer = 3,
        Operator = 4,
        Event = 5
    }

    public readonly struct LuaClrNamedArgument : System.IEquatable<Lunil.Hosting.LuaClrNamedArgument>
    {
        public string Name { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue Value { get => throw null; init { } }
        public LuaClrNamedArgument(string Name, Lunil.Runtime.Values.LuaValue Value) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaClrNamedArgument left, Lunil.Hosting.LuaClrNamedArgument right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaClrNamedArgument left, Lunil.Hosting.LuaClrNamedArgument right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaClrNamedArgument other) => throw null;
        public void Deconstruct(out string Name, out Lunil.Runtime.Values.LuaValue Value) => throw null;
    }

    public sealed class LuaClrObject : System.IDisposable
    {
        public object Instance { get => throw null; }
        public System.Type ClrType { get => throw null; }
        public bool OwnsInstance { get => throw null; }
        public bool IsDisposed { get => throw null; }
        public LuaClrObject(object instance, bool ownsInstance = true) { }
        public void Dispose() { }
    }

    public sealed class LuaClrOptions : System.IEquatable<Lunil.Hosting.LuaClrOptions>
    {
        public static Lunil.Hosting.LuaClrOptions Disabled { get => throw null; }
        public Lunil.Hosting.LuaClrCapabilities Capabilities { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> AllowedAssemblyNames { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> AllowedTypeNames { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> AllowedMemberNames { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> AllowedDelegateTypeNames { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> AllowedEventNames { get => throw null; init { } }
        public bool InstallGlobalModule { get => throw null; init { } }
        public int MaximumTypeNameLength { get => throw null; init { } }
        public bool OwnConstructedObjects { get => throw null; init { } }
        public Lunil.Hosting.LuaClrThreadPolicy ThreadPolicy { get => throw null; init { } }
        public bool IncludeExceptionMessages { get => throw null; init { } }
        public int MaximumCachedMembers { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaClrOptions? left, Lunil.Hosting.LuaClrOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaClrOptions? left, Lunil.Hosting.LuaClrOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaClrOptions? other) => throw null;
    }

    public sealed class LuaClrSubscription : System.IDisposable
    {
        public Lunil.Runtime.Values.LuaValue Callback { get => throw null; }
        public bool IsDisposed { get => throw null; }
        public void Dispose() { }
    }

    public sealed class LuaClrTask : System.IDisposable
    {
        public System.Threading.Tasks.Task Task { get => throw null; }
        public Lunil.Hosting.LuaClrBridge Bridge { get => throw null; }
        public bool IsCompleted { get => throw null; }
        public bool IsFaulted { get => throw null; }
        public void Dispose() { }
    }

    public enum LuaClrThreadPolicy
    {
        OwnerThreadOnly = 0,
        AnyThreadWhenIdle = 1
    }

    public sealed class LuaClrTypeInfo : System.IEquatable<Lunil.Hosting.LuaClrTypeInfo>
    {
        public string FullName { get => throw null; init { } }
        public string AssemblyName { get => throw null; init { } }
        public bool IsValueType { get => throw null; init { } }
        public bool IsConstructible { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaClrConstructorInfo> Constructors { get => throw null; init { } }
        public LuaClrTypeInfo(string FullName, string AssemblyName, bool IsValueType, bool IsConstructible, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaClrConstructorInfo> Constructors) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaClrTypeInfo? left, Lunil.Hosting.LuaClrTypeInfo? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaClrTypeInfo? left, Lunil.Hosting.LuaClrTypeInfo? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaClrTypeInfo? other) => throw null;
        public void Deconstruct(out string FullName, out string AssemblyName, out bool IsValueType, out bool IsConstructible, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaClrConstructorInfo> Constructors) => throw null;
    }

    public sealed class LuaFunctionMigrationResult : System.IEquatable<Lunil.Hosting.LuaFunctionMigrationResult>
    {
        public string LogicalKey { get => throw null; init { } }
        public Lunil.Hosting.LuaFunctionMigrationStatus Status { get => throw null; init { } }
        public long PreviousGeneration { get => throw null; init { } }
        public long CurrentGeneration { get => throw null; init { } }
        public string PreviousUpvalueLayoutFingerprint { get => throw null; init { } }
        public string? CandidateUpvalueLayoutFingerprint { get => throw null; init { } }
        public LuaFunctionMigrationResult(string LogicalKey, Lunil.Hosting.LuaFunctionMigrationStatus Status, long PreviousGeneration, long CurrentGeneration, string PreviousUpvalueLayoutFingerprint, string? CandidateUpvalueLayoutFingerprint) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaFunctionMigrationResult? left, Lunil.Hosting.LuaFunctionMigrationResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaFunctionMigrationResult? left, Lunil.Hosting.LuaFunctionMigrationResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaFunctionMigrationResult? other) => throw null;
        public void Deconstruct(out string LogicalKey, out Lunil.Hosting.LuaFunctionMigrationStatus Status, out long PreviousGeneration, out long CurrentGeneration, out string PreviousUpvalueLayoutFingerprint, out string? CandidateUpvalueLayoutFingerprint) => throw null;
    }

    public enum LuaFunctionMigrationStatus
    {
        Updated = 0,
        ReplacementMissing = 1,
        UpvalueLayoutMismatch = 2,
        ConcurrentUpdate = 3
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
        public Lunil.Hosting.LuaClrBridge ClrBridge { get => throw null; }
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
        public Lunil.Hosting.LuaModuleReloadResult ReloadModule(string name, Lunil.Hosting.LuaModuleReloadOptions? options = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
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
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
        public Lunil.Compiler.LuaCompilerOptions Compiler { get => throw null; init { } }
        public Lunil.Runtime.LuaStateOptions State { get => throw null; init { } }
        public Lunil.Runtime.Execution.LuaInterpreterOptions Execution { get => throw null; init { } }
        public Lunil.Hosting.LuaHostExecutionBackend ExecutionBackend { get => throw null; init { } }
        public Lunil.CodeGen.Cil.Jit.LuaJitExecutorOptions Jit { get => throw null; init { } }
        public Lunil.Workspace.LuaWorkspaceOptions Workspace { get => throw null; init { } }
        public Lunil.Workspace.ILuaModuleResolver? ModuleResolver { get => throw null; init { } }
        public bool InstallStandardLibrary { get => throw null; init { } }
        public Lunil.StandardLibrary.LuaStandardLibraryOptions? StandardLibrary { get => throw null; init { } }
        public Lunil.Hosting.LuaClrOptions Clr { get => throw null; init { } }
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

    public delegate Lunil.Runtime.Values.LuaValue LuaModuleReloadCacheCallback(Lunil.Hosting.LuaModuleReloadContext context);

    public enum LuaModuleReloadCachePolicy
    {
        ReplaceCache = 0,
        PatchExistingTable = 1,
        Custom = 2
    }

    public sealed class LuaModuleReloadContext : System.IEquatable<Lunil.Hosting.LuaModuleReloadContext>
    {
        public string ModuleName { get => throw null; init { } }
        public Lunil.Runtime.LuaModuleRecord PreviousRecord { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue CandidateValue { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue CandidateLoader { get => throw null; init { } }
        public Lunil.IR.Canonical.LuaIrModule? CandidateModule { get => throw null; init { } }
        public LuaModuleReloadContext(string ModuleName, Lunil.Runtime.LuaModuleRecord PreviousRecord, Lunil.Runtime.Values.LuaValue CandidateValue, Lunil.Runtime.Values.LuaValue CandidateLoader, Lunil.IR.Canonical.LuaIrModule? CandidateModule) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaModuleReloadContext? left, Lunil.Hosting.LuaModuleReloadContext? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaModuleReloadContext? left, Lunil.Hosting.LuaModuleReloadContext? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaModuleReloadContext? other) => throw null;
        public void Deconstruct(out string ModuleName, out Lunil.Runtime.LuaModuleRecord PreviousRecord, out Lunil.Runtime.Values.LuaValue CandidateValue, out Lunil.Runtime.Values.LuaValue CandidateLoader, out Lunil.IR.Canonical.LuaIrModule? CandidateModule) => throw null;
    }

    public sealed class LuaModuleReloadOptions : System.IEquatable<Lunil.Hosting.LuaModuleReloadOptions>
    {
        public static Lunil.Hosting.LuaModuleReloadOptions Default { get => throw null; }
        public string? SourcePath { get => throw null; init { } }
        public Lunil.Hosting.LuaModuleReloadCachePolicy CachePolicy { get => throw null; init { } }
        public Lunil.Hosting.LuaModuleReloadCacheCallback? CustomCachePolicy { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaModuleReloadOptions? left, Lunil.Hosting.LuaModuleReloadOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaModuleReloadOptions? left, Lunil.Hosting.LuaModuleReloadOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaModuleReloadOptions? other) => throw null;
    }

    public sealed class LuaModuleReloadResult : System.IEquatable<Lunil.Hosting.LuaModuleReloadResult>
    {
        public string ModuleName { get => throw null; init { } }
        public Lunil.Hosting.LuaModuleReloadStatus Status { get => throw null; init { } }
        public Lunil.Runtime.LuaModuleRecord? PreviousRecord { get => throw null; init { } }
        public Lunil.Runtime.LuaModuleRecord? CurrentRecord { get => throw null; init { } }
        public Lunil.Compiler.LuaCompilationResult? Compilation { get => throw null; init { } }
        public Lunil.Runtime.Execution.LuaExecutionResult? Execution { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public bool SideEffectsMayHaveOccurred { get => throw null; init { } }
        public int ReusedUpvalueCount { get => throw null; init { } }
        public int UpvalueMismatchCount { get => throw null; init { } }
        public int PatchedExportCount { get => throw null; init { } }
        public int RemovedExportCount { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaFunctionMigrationResult> FunctionMigrations { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public int UpdatedFunctionCount { get => throw null; }
        public int IncompatibleFunctionCount { get => throw null; }
        public LuaModuleReloadResult(string ModuleName, Lunil.Hosting.LuaModuleReloadStatus Status, Lunil.Runtime.LuaModuleRecord? PreviousRecord, Lunil.Runtime.LuaModuleRecord? CurrentRecord, Lunil.Compiler.LuaCompilationResult? Compilation, Lunil.Runtime.Execution.LuaExecutionResult? Execution, string? Message, bool SideEffectsMayHaveOccurred, int ReusedUpvalueCount, int UpvalueMismatchCount, int PatchedExportCount, int RemovedExportCount, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaFunctionMigrationResult> FunctionMigrations = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaModuleReloadResult? left, Lunil.Hosting.LuaModuleReloadResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaModuleReloadResult? left, Lunil.Hosting.LuaModuleReloadResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaModuleReloadResult? other) => throw null;
        public void Deconstruct(out string ModuleName, out Lunil.Hosting.LuaModuleReloadStatus Status, out Lunil.Runtime.LuaModuleRecord? PreviousRecord, out Lunil.Runtime.LuaModuleRecord? CurrentRecord, out Lunil.Compiler.LuaCompilationResult? Compilation, out Lunil.Runtime.Execution.LuaExecutionResult? Execution, out string? Message, out bool SideEffectsMayHaveOccurred, out int ReusedUpvalueCount, out int UpvalueMismatchCount, out int PatchedExportCount, out int RemovedExportCount, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaFunctionMigrationResult> FunctionMigrations) => throw null;
    }

    public enum LuaModuleReloadStatus
    {
        Reloaded = 0,
        NotLoaded = 1,
        StateBusy = 2,
        UnsupportedLoader = 3,
        SourceReadFailed = 4,
        CompilationFailed = 5,
        ExecutionFailed = 6,
        CachePolicyFailed = 7
    }
}
