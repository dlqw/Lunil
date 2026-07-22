// Target Frameworks: net10.0
#nullable enable

namespace Lunil.Hosting
{
    public interface ILuaPatchCanonicalIrDecoder
    {
        Lunil.IR.Canonical.LuaIrModule Decode(string moduleName, System.ReadOnlySpan<byte> payload);
    }

    public interface ILuaPatchCrashRecoveryHandler
    {
        Lunil.Hosting.LuaPatchRecoveryResolution Recover(Lunil.Hosting.LuaPatchRecoveryRecord record);
    }

    public interface ILuaPatchDeploymentJournal
    {
        void Append(Lunil.Hosting.LuaPatchJournalEntry entry);
        System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchJournalEntry> ReadAll();
    }

    public interface ILuaPatchReplayStore
    {
        bool TryAccept(string patchId, string nonce, System.DateTimeOffset acceptedAt);
    }

    public interface ILuaPatchResourceMigrationAdapter
    {
        string AdapterId { get; }
        Lunil.Hosting.ILuaPatchResourceMigrationOperation Prepare(Lunil.Hosting.LuaPatchResourceMigrationContext context);
    }

    public interface ILuaPatchResourceMigrationOperation : System.IDisposable
    {
        bool IsActive { get; }
        void Apply(Lunil.Hosting.LuaPatchResourceDisposition disposition);
        void Rollback();
    }

    public interface ILuaPatchSignatureVerifier
    {
        bool IsTrusted(string algorithm, string keyId);
        bool VerifyDigest(string algorithm, string keyId, System.ReadOnlySpan<byte> digest, System.ReadOnlySpan<byte> signature);
    }

    public interface ILuaPatchSigner
    {
        string Algorithm { get; }
        string KeyId { get; }
        byte[] SignDigest(System.ReadOnlySpan<byte> digest);
    }

    public interface ILuaPatchStateMigrationAdapter
    {
        string AdapterId { get; }
        Lunil.Hosting.ILuaPatchStateMigrationOperation Prepare(Lunil.Hosting.LuaPatchStateMigrationContext context);
    }

    public interface ILuaPatchStateMigrationOperation : System.IDisposable
    {
        Lunil.Runtime.Values.LuaValue ResultValue { get; }
        void Apply();
        void Rollback();
    }

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
        public void SetPatchStateSchemaVersion(string schemaId, string version) { }
        public bool TryGetPatchStateSchemaVersion(string schemaId, out string? version) => throw null;
        public Lunil.Hosting.LuaPatchPrepareResult PreparePatch(Lunil.Hosting.LuaPatchBundle bundle, Lunil.Hosting.LuaPatchPrepareOptions? options = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public System.Threading.Tasks.Task<Lunil.Hosting.LuaPatchPrepareResult> PreparePatchAsync(Lunil.Hosting.LuaPatchBundle bundle, Lunil.Hosting.LuaPatchPrepareOptions? options = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Hosting.LuaPatchUpdateWindowResult TryOpenPatchUpdateWindow(Lunil.Hosting.LuaPatchUpdateWindowOptions? options = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Hosting.LuaPatchCommitResult CommitPatch(Lunil.Hosting.LuaPreparedPatch preparedPatch, Lunil.Hosting.LuaPatchUpdateWindow updateWindow, Lunil.Hosting.LuaPatchCommitOptions? options = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
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

    public sealed class LuaPatchAcceptancePolicy : System.IEquatable<Lunil.Hosting.LuaPatchAcceptancePolicy>
    {
        public required string TargetBuild { get => throw null; init { } }
        public required string CurrentRevision { get => throw null; init { } }
        public required string RuntimeAbi { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> AllowedChannels { get => throw null; init { } }
        public System.DateTimeOffset? MinimumCreatedAt { get => throw null; init { } }
        public System.TimeSpan MaximumFutureSkew { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchReplayLookup? ReplayLookup { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchAcceptanceResult Evaluate(Lunil.Hosting.LuaPatchManifest manifest, System.DateTimeOffset? utcNow = null) => throw null;
        public Lunil.Hosting.LuaPatchAcceptanceResult TryAccept(Lunil.Hosting.LuaPatchManifest manifest, Lunil.Hosting.ILuaPatchReplayStore replayStore, System.DateTimeOffset? utcNow = null) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchAcceptancePolicy? left, Lunil.Hosting.LuaPatchAcceptancePolicy? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchAcceptancePolicy? left, Lunil.Hosting.LuaPatchAcceptancePolicy? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchAcceptancePolicy? other) => throw null;
    }

    public sealed class LuaPatchAcceptanceResult : System.IEquatable<Lunil.Hosting.LuaPatchAcceptanceResult>
    {
        public Lunil.Hosting.LuaPatchAcceptanceStatus Status { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public bool Accepted { get => throw null; }
        public LuaPatchAcceptanceResult(Lunil.Hosting.LuaPatchAcceptanceStatus Status, string? Message) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchAcceptanceResult? left, Lunil.Hosting.LuaPatchAcceptanceResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchAcceptanceResult? left, Lunil.Hosting.LuaPatchAcceptanceResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchAcceptanceResult? other) => throw null;
        public void Deconstruct(out Lunil.Hosting.LuaPatchAcceptanceStatus Status, out string? Message) => throw null;
    }

    public enum LuaPatchAcceptanceStatus
    {
        Accepted = 0,
        TargetBuildMismatch = 1,
        BaseRevisionMismatch = 2,
        RuntimeAbiMismatch = 3,
        ChannelNotAllowed = 4,
        CreatedInFuture = 5,
        CreatedBeforeMinimum = 6,
        ReplayDetected = 7,
        Expired = 8
    }

    public sealed class LuaPatchBundle
    {
        public Lunil.Hosting.LuaPatchManifest Manifest { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchEntry> Entries { get => throw null; }
        public Lunil.Hosting.LuaPatchSignature Signature { get => throw null; }
        public static Lunil.Hosting.LuaPatchBundle Create(Lunil.Hosting.LuaPatchManifest manifest, System.Collections.Generic.IEnumerable<Lunil.Hosting.LuaPatchEntry> entries, Lunil.Hosting.ILuaPatchSigner signer) => throw null;
        public static Lunil.Hosting.LuaPatchBundle Read(System.IO.Stream stream, Lunil.Hosting.ILuaPatchSignatureVerifier signatureVerifier, Lunil.Hosting.LuaPatchBundleReadOptions? options = null) => throw null;
        public void Write(System.IO.Stream stream) { }
    }

    public sealed class LuaPatchBundleReadOptions : System.IEquatable<Lunil.Hosting.LuaPatchBundleReadOptions>
    {
        public static Lunil.Hosting.LuaPatchBundleReadOptions Default { get => throw null; }
        public long MaximumBundleBytes { get => throw null; init { } }
        public int MaximumManifestBytes { get => throw null; init { } }
        public int MaximumEntryCount { get => throw null; init { } }
        public long MaximumEntryBytes { get => throw null; init { } }
        public long MaximumTotalEntryBytes { get => throw null; init { } }
        public int MaximumNameBytes { get => throw null; init { } }
        public int MaximumSignatureBytes { get => throw null; init { } }
        public bool RequireSignature { get => throw null; init { } }
        public bool AllowExpired { get => throw null; init { } }
        public System.DateTimeOffset? UtcNow { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchBundleReadOptions? left, Lunil.Hosting.LuaPatchBundleReadOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchBundleReadOptions? left, Lunil.Hosting.LuaPatchBundleReadOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchBundleReadOptions? other) => throw null;
    }

    public sealed class LuaPatchCommitOptions : System.IEquatable<Lunil.Hosting.LuaPatchCommitOptions>
    {
        public static Lunil.Hosting.LuaPatchCommitOptions Default { get => throw null; }
        public System.TimeSpan MaximumPauseDuration { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchCommitOptions? left, Lunil.Hosting.LuaPatchCommitOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchCommitOptions? left, Lunil.Hosting.LuaPatchCommitOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchCommitOptions? other) => throw null;
    }

    public sealed class LuaPatchCommitResult : System.IEquatable<Lunil.Hosting.LuaPatchCommitResult>
    {
        public string PatchId { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchCommitStatus Status { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModuleCommitResult> Modules { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public bool SideEffectsMayHaveOccurred { get => throw null; init { } }
        public System.TimeSpan PauseDuration { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaPatchCommitResult(string PatchId, Lunil.Hosting.LuaPatchCommitStatus Status, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModuleCommitResult> Modules, string? Message, bool SideEffectsMayHaveOccurred, System.TimeSpan PauseDuration) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchCommitResult? left, Lunil.Hosting.LuaPatchCommitResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchCommitResult? left, Lunil.Hosting.LuaPatchCommitResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchCommitResult? other) => throw null;
        public void Deconstruct(out string PatchId, out Lunil.Hosting.LuaPatchCommitStatus Status, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModuleCommitResult> Modules, out string? Message, out bool SideEffectsMayHaveOccurred, out System.TimeSpan PauseDuration) => throw null;
    }

    public enum LuaPatchCommitStatus
    {
        Committed = 0,
        Deferred = 1,
        Cancelled = 2,
        RevisionConflict = 3,
        ExecutionFailed = 4,
        MigrationFailed = 5,
        CachePolicyFailed = 6,
        PublicationFailed = 7,
        BarrierAborted = 8,
        Expired = 9
    }

    public sealed class LuaPatchCoordinator
    {
        public Lunil.Hosting.LuaPatchRolloutResult Deploy(Lunil.Hosting.LuaPatchRolloutPlan plan, Lunil.Hosting.LuaPatchCoordinatorOptions? options = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Hosting.LuaPatchRingCommitResult CommitRing(string rolloutId, Lunil.Hosting.LuaPatchRolloutRing ring, Lunil.Hosting.LuaPatchCoordinatorOptions? options = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public sealed class LuaPatchCoordinatorOptions : System.IEquatable<Lunil.Hosting.LuaPatchCoordinatorOptions>
    {
        public static Lunil.Hosting.LuaPatchCoordinatorOptions Default { get => throw null; }
        public Lunil.Hosting.LuaPatchUpdateWindowOptions UpdateWindow { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchCommitOptions Commit { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchRingHealthCallback? HealthCheck { get => throw null; init { } }
        public Lunil.Hosting.ILuaPatchDeploymentJournal? Journal { get => throw null; init { } }
        public System.TimeProvider TimeProvider { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchResourceLimits ResourceLimits { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchCoordinatorOptions? left, Lunil.Hosting.LuaPatchCoordinatorOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchCoordinatorOptions? left, Lunil.Hosting.LuaPatchCoordinatorOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchCoordinatorOptions? other) => throw null;
    }

    public sealed class LuaPatchDependency : System.IEquatable<Lunil.Hosting.LuaPatchDependency>
    {
        public string ModuleName { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchDependencyKind Kind { get => throw null; init { } }
        public LuaPatchDependency(string ModuleName, Lunil.Hosting.LuaPatchDependencyKind Kind) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchDependency? left, Lunil.Hosting.LuaPatchDependency? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchDependency? left, Lunil.Hosting.LuaPatchDependency? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchDependency? other) => throw null;
        public void Deconstruct(out string ModuleName, out Lunil.Hosting.LuaPatchDependencyKind Kind) => throw null;
    }

    public sealed class LuaPatchDependencyComponent : System.IEquatable<Lunil.Hosting.LuaPatchDependencyComponent>
    {
        public int Id { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> Modules { get => throw null; init { } }
        public bool IsCyclic { get => throw null; init { } }
        public LuaPatchDependencyComponent(int Id, System.Collections.Immutable.ImmutableArray<string> Modules, bool IsCyclic) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchDependencyComponent? left, Lunil.Hosting.LuaPatchDependencyComponent? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchDependencyComponent? left, Lunil.Hosting.LuaPatchDependencyComponent? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchDependencyComponent? other) => throw null;
        public void Deconstruct(out int Id, out System.Collections.Immutable.ImmutableArray<string> Modules, out bool IsCyclic) => throw null;
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Lunil.Hosting.LuaPatchDependencyKind>))]
    public enum LuaPatchDependencyKind
    {
        Required = 0,
        Optional = 1
    }

    public sealed class LuaPatchDependencyPlan : System.IEquatable<Lunil.Hosting.LuaPatchDependencyPlan>
    {
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchDependencyComponent> Components { get => throw null; init { } }
        public LuaPatchDependencyPlan(System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchDependencyComponent> Components) { }
        public static Lunil.Hosting.LuaPatchDependencyPlan Create(System.Collections.Generic.IEnumerable<Lunil.Hosting.LuaPatchEntry> entries) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchDependencyPlan? left, Lunil.Hosting.LuaPatchDependencyPlan? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchDependencyPlan? left, Lunil.Hosting.LuaPatchDependencyPlan? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchDependencyPlan? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchDependencyComponent> Components) => throw null;
    }

    public sealed class LuaPatchDeploymentTarget : System.IEquatable<Lunil.Hosting.LuaPatchDeploymentTarget>
    {
        public string TargetId { get => throw null; init { } }
        public Lunil.Hosting.LuaHost Host { get => throw null; init { } }
        public Lunil.Hosting.LuaPreparedPatch PreparedPatch { get => throw null; init { } }
        public LuaPatchDeploymentTarget(string TargetId, Lunil.Hosting.LuaHost Host, Lunil.Hosting.LuaPreparedPatch PreparedPatch) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchDeploymentTarget? left, Lunil.Hosting.LuaPatchDeploymentTarget? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchDeploymentTarget? left, Lunil.Hosting.LuaPatchDeploymentTarget? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchDeploymentTarget? other) => throw null;
        public void Deconstruct(out string TargetId, out Lunil.Hosting.LuaHost Host, out Lunil.Hosting.LuaPreparedPatch PreparedPatch) => throw null;
    }

    public sealed class LuaPatchEcdsaSigner : Lunil.Hosting.ILuaPatchSigner
    {
        public const string AlgorithmName = "ECDSA-P256-SHA256";
        public string Algorithm { get => throw null; }
        public string KeyId { get => throw null; }
        public LuaPatchEcdsaSigner(string keyId, System.Security.Cryptography.ECDsa key) { }
        public byte[] SignDigest(System.ReadOnlySpan<byte> digest) => throw null;
    }

    public sealed class LuaPatchEcdsaTrustStore : Lunil.Hosting.ILuaPatchSignatureVerifier
    {
        public LuaPatchEcdsaTrustStore(System.Collections.Generic.IEnumerable<Lunil.Hosting.LuaPatchTrustedEcdsaKey> keys) { }
        public bool IsTrusted(string algorithm, string keyId) => throw null;
        public bool VerifyDigest(string algorithm, string keyId, System.ReadOnlySpan<byte> digest, System.ReadOnlySpan<byte> signature) => throw null;
    }

    public sealed class LuaPatchEntry : System.IEquatable<Lunil.Hosting.LuaPatchEntry>
    {
        public string Name { get => throw null; init { } }
        public string? ModuleName { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchEntryKind Kind { get => throw null; init { } }
        public System.ReadOnlyMemory<byte> Content { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchDependency> Dependencies { get => throw null; init { } }
        public LuaPatchEntry(string Name, string? ModuleName, Lunil.Hosting.LuaPatchEntryKind Kind, System.ReadOnlyMemory<byte> Content, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchDependency> Dependencies = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchEntry? left, Lunil.Hosting.LuaPatchEntry? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchEntry? left, Lunil.Hosting.LuaPatchEntry? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchEntry? other) => throw null;
        public void Deconstruct(out string Name, out string? ModuleName, out Lunil.Hosting.LuaPatchEntryKind Kind, out System.ReadOnlyMemory<byte> Content, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchDependency> Dependencies) => throw null;
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Lunil.Hosting.LuaPatchEntryKind>))]
    public enum LuaPatchEntryKind
    {
        Source = 0,
        BinaryChunk = 1,
        CanonicalIr = 2,
        CompanionData = 3
    }

    public sealed class LuaPatchEntryManifest : System.IEquatable<Lunil.Hosting.LuaPatchEntryManifest>
    {
        public required string Name { get => throw null; init { } }
        public string? ModuleName { get => throw null; init { } }
        public required Lunil.Hosting.LuaPatchEntryKind Kind { get => throw null; init { } }
        public required string ContentHash { get => throw null; init { } }
        public required long Length { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchDependency> Dependencies { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchEntryManifest? left, Lunil.Hosting.LuaPatchEntryManifest? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchEntryManifest? left, Lunil.Hosting.LuaPatchEntryManifest? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchEntryManifest? other) => throw null;
    }

    public enum LuaPatchErrorCode
    {
        InvalidHeader = 0,
        UnsupportedFormatVersion = 1,
        InvalidManifest = 2,
        NonCanonicalManifest = 3,
        InvalidSignature = 4,
        SignatureRequired = 5,
        UntrustedSigningKey = 6,
        Expired = 7,
        ResourceLimitExceeded = 8,
        UnsafeEntryName = 9,
        DuplicateEntry = 10,
        DuplicateModule = 11,
        MissingEntry = 12,
        EntryMetadataMismatch = 13,
        ContentHashMismatch = 14,
        MissingDependency = 15,
        TrailingData = 16
    }

    public sealed class LuaPatchFileJournal : Lunil.Hosting.ILuaPatchDeploymentJournal, System.IDisposable
    {
        public string Path { get => throw null; }
        public string WriterLockPath { get => throw null; }
        public LuaPatchFileJournal(string path, Lunil.Hosting.LuaPatchFileJournalOptions? options = null) { }
        public void Append(Lunil.Hosting.LuaPatchJournalEntry entry) { }
        public Lunil.Hosting.LuaPatchJournalCompactionResult Compact(Lunil.Hosting.LuaPatchJournalCompactionOptions? options = null) => throw null;
        public void Dispose() { }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchJournalEntry> ReadAll() => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchRecoveryRecord> GetIncompleteTransactions() => throw null;
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchRecoveryResult> RecoverIncomplete(Lunil.Hosting.ILuaPatchCrashRecoveryHandler handler, System.TimeProvider? timeProvider = null) => throw null;
    }

    public sealed class LuaPatchFileJournalOptions : System.IEquatable<Lunil.Hosting.LuaPatchFileJournalOptions>
    {
        public static Lunil.Hosting.LuaPatchFileJournalOptions Default { get => throw null; }
        public long MaximumBytes { get => throw null; init { } }
        public int MaximumEntries { get => throw null; init { } }
        public int MaximumLineBytes { get => throw null; init { } }
        public bool CreateDirectory { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchJournalCompactionOptions? AutomaticCompaction { get => throw null; init { } }
        public System.TimeSpan ConcurrentReadTimeout { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchFileJournalOptions? left, Lunil.Hosting.LuaPatchFileJournalOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchFileJournalOptions? left, Lunil.Hosting.LuaPatchFileJournalOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchFileJournalOptions? other) => throw null;
    }

    public static class LuaPatchFormat
    {
        public const int CurrentVersion = 1;
    }

    public sealed class LuaPatchFormatException : System.Exception
    {
        public Lunil.Hosting.LuaPatchErrorCode Code { get => throw null; }
        public LuaPatchFormatException(Lunil.Hosting.LuaPatchErrorCode code, string message) { }
        public LuaPatchFormatException(Lunil.Hosting.LuaPatchErrorCode code, string message, System.Exception innerException) { }
    }

    public sealed class LuaPatchJournalCompactionOptions : System.IEquatable<Lunil.Hosting.LuaPatchJournalCompactionOptions>
    {
        public static Lunil.Hosting.LuaPatchJournalCompactionOptions Default { get => throw null; }
        public int RetainCompletedTransactions { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchJournalCompactionOptions? left, Lunil.Hosting.LuaPatchJournalCompactionOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchJournalCompactionOptions? left, Lunil.Hosting.LuaPatchJournalCompactionOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchJournalCompactionOptions? other) => throw null;
    }

    public sealed class LuaPatchJournalCompactionResult : System.IEquatable<Lunil.Hosting.LuaPatchJournalCompactionResult>
    {
        public int OriginalEntryCount { get => throw null; init { } }
        public int RetainedEntryCount { get => throw null; init { } }
        public long OriginalBytes { get => throw null; init { } }
        public long RetainedBytes { get => throw null; init { } }
        public string? OriginalTailHash { get => throw null; init { } }
        public string? RetainedTailHash { get => throw null; init { } }
        public int RemovedEntryCount { get => throw null; }
        public bool Changed { get => throw null; }
        public LuaPatchJournalCompactionResult(int OriginalEntryCount, int RetainedEntryCount, long OriginalBytes, long RetainedBytes, string? OriginalTailHash, string? RetainedTailHash) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchJournalCompactionResult? left, Lunil.Hosting.LuaPatchJournalCompactionResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchJournalCompactionResult? left, Lunil.Hosting.LuaPatchJournalCompactionResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchJournalCompactionResult? other) => throw null;
        public void Deconstruct(out int OriginalEntryCount, out int RetainedEntryCount, out long OriginalBytes, out long RetainedBytes, out string? OriginalTailHash, out string? RetainedTailHash) => throw null;
    }

    public sealed class LuaPatchJournalEntry : System.IEquatable<Lunil.Hosting.LuaPatchJournalEntry>
    {
        public long Sequence { get => throw null; init { } }
        public required System.DateTimeOffset Timestamp { get => throw null; init { } }
        public required string TransactionId { get => throw null; init { } }
        public required string RolloutId { get => throw null; init { } }
        public required string RingName { get => throw null; init { } }
        public required string PatchId { get => throw null; init { } }
        public required string TargetRevision { get => throw null; init { } }
        public required Lunil.Hosting.LuaPatchJournalPhase Phase { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> TargetIds { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public string? PreviousHash { get => throw null; init { } }
        public string? Hash { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchJournalEntry? left, Lunil.Hosting.LuaPatchJournalEntry? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchJournalEntry? left, Lunil.Hosting.LuaPatchJournalEntry? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchJournalEntry? other) => throw null;
    }

    public enum LuaPatchJournalErrorCode
    {
        InvalidEntry = 0,
        Corrupted = 1,
        HashMismatch = 2,
        SequenceMismatch = 3,
        InvalidTransition = 4,
        ResourceLimitExceeded = 5,
        IoFailure = 6,
        WriterUnavailable = 7
    }

    public sealed class LuaPatchJournalException : System.Exception
    {
        public Lunil.Hosting.LuaPatchJournalErrorCode Code { get => throw null; }
        public LuaPatchJournalException(Lunil.Hosting.LuaPatchJournalErrorCode code, string message) { }
        public LuaPatchJournalException(Lunil.Hosting.LuaPatchJournalErrorCode code, string message, System.Exception innerException) { }
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Lunil.Hosting.LuaPatchJournalPhase>))]
    public enum LuaPatchJournalPhase
    {
        Started = 0,
        Prepared = 1,
        Publishing = 2,
        Committed = 3,
        RolledBack = 4,
        Failed = 5,
        RecoveredCommitted = 6,
        RecoveredRolledBack = 7
    }

    public sealed class LuaPatchManifest : System.IEquatable<Lunil.Hosting.LuaPatchManifest>
    {
        public int FormatVersion { get => throw null; init { } }
        public required string PatchId { get => throw null; init { } }
        public required string Channel { get => throw null; init { } }
        public required string TargetBuild { get => throw null; init { } }
        public required string BaseRevision { get => throw null; init { } }
        public required string TargetRevision { get => throw null; init { } }
        public required Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
        public required string RuntimeAbi { get => throw null; init { } }
        public required System.DateTimeOffset CreatedAt { get => throw null; init { } }
        public System.DateTimeOffset? ExpiresAt { get => throw null; init { } }
        public required string Nonce { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchEntryManifest> Entries { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchManifest? left, Lunil.Hosting.LuaPatchManifest? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchManifest? left, Lunil.Hosting.LuaPatchManifest? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchManifest? other) => throw null;
    }

    public static class LuaPatchManifestSerializer
    {
        public static byte[] Serialize(Lunil.Hosting.LuaPatchManifest manifest) => throw null;
        public static Lunil.Hosting.LuaPatchManifest Deserialize(System.ReadOnlySpan<byte> utf8Json) => throw null;
    }

    public sealed class LuaPatchMigrationSchema : System.IEquatable<Lunil.Hosting.LuaPatchMigrationSchema>
    {
        public int FormatVersion { get => throw null; init { } }
        public required string SchemaId { get => throw null; init { } }
        public required string BaseVersion { get => throw null; init { } }
        public required string TargetVersion { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModuleMigrationSchema> Modules { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchMigrationSchema? left, Lunil.Hosting.LuaPatchMigrationSchema? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchMigrationSchema? left, Lunil.Hosting.LuaPatchMigrationSchema? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchMigrationSchema? other) => throw null;
    }

    public enum LuaPatchMigrationSchemaErrorCode
    {
        InvalidJson = 0,
        NonCanonicalJson = 1,
        UnsupportedFormatVersion = 2,
        InvalidSchema = 3,
        DuplicateModule = 4,
        DuplicateRule = 5,
        UnknownModule = 6,
        AdapterRequired = 7,
        AdapterNotRegistered = 8,
        StatePathNotFound = 9,
        StateKindMismatch = 10,
        ResourceActive = 11,
        AdapterFailed = 12
    }

    public sealed class LuaPatchMigrationSchemaException : System.Exception
    {
        public Lunil.Hosting.LuaPatchMigrationSchemaErrorCode Code { get => throw null; }
        public LuaPatchMigrationSchemaException(Lunil.Hosting.LuaPatchMigrationSchemaErrorCode code, string message) { }
        public LuaPatchMigrationSchemaException(Lunil.Hosting.LuaPatchMigrationSchemaErrorCode code, string message, System.Exception innerException) { }
    }

    public static class LuaPatchMigrationSchemaFormat
    {
        public const int CurrentVersion = 1;
        public const string BundleEntryName = "migration/schema.json";
    }

    public static class LuaPatchMigrationSchemaSerializer
    {
        public static byte[] Serialize(Lunil.Hosting.LuaPatchMigrationSchema schema, Lunil.Hosting.LuaPatchResourceLimits? resourceLimits = null) => throw null;
        public static Lunil.Hosting.LuaPatchMigrationSchema Deserialize(System.ReadOnlySpan<byte> utf8Json, Lunil.Hosting.LuaPatchResourceLimits? resourceLimits = null) => throw null;
        public static Lunil.Hosting.LuaPatchMigrationSchema? ReadFromBundle(Lunil.Hosting.LuaPatchBundle bundle, Lunil.Hosting.LuaPatchResourceLimits? resourceLimits = null) => throw null;
    }

    public sealed class LuaPatchModuleCommitResult : System.IEquatable<Lunil.Hosting.LuaPatchModuleCommitResult>
    {
        public string ModuleName { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchModuleCommitStatus Status { get => throw null; init { } }
        public long ExpectedRevision { get => throw null; init { } }
        public long? ObservedRevision { get => throw null; init { } }
        public Lunil.Runtime.LuaModuleRecord? PreviousRecord { get => throw null; init { } }
        public Lunil.Runtime.LuaModuleRecord? CurrentRecord { get => throw null; init { } }
        public Lunil.Runtime.Execution.LuaExecutionResult? Execution { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public int ReusedUpvalueCount { get => throw null; init { } }
        public int UpvalueMismatchCount { get => throw null; init { } }
        public int PatchedExportCount { get => throw null; init { } }
        public int RemovedExportCount { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaFunctionMigrationResult> FunctionMigrations { get => throw null; init { } }
        public LuaPatchModuleCommitResult(string ModuleName, Lunil.Hosting.LuaPatchModuleCommitStatus Status, long ExpectedRevision, long? ObservedRevision, Lunil.Runtime.LuaModuleRecord? PreviousRecord, Lunil.Runtime.LuaModuleRecord? CurrentRecord, Lunil.Runtime.Execution.LuaExecutionResult? Execution, string? Message, int ReusedUpvalueCount, int UpvalueMismatchCount, int PatchedExportCount, int RemovedExportCount, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaFunctionMigrationResult> FunctionMigrations = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchModuleCommitResult? left, Lunil.Hosting.LuaPatchModuleCommitResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchModuleCommitResult? left, Lunil.Hosting.LuaPatchModuleCommitResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchModuleCommitResult? other) => throw null;
        public void Deconstruct(out string ModuleName, out Lunil.Hosting.LuaPatchModuleCommitStatus Status, out long ExpectedRevision, out long? ObservedRevision, out Lunil.Runtime.LuaModuleRecord? PreviousRecord, out Lunil.Runtime.LuaModuleRecord? CurrentRecord, out Lunil.Runtime.Execution.LuaExecutionResult? Execution, out string? Message, out int ReusedUpvalueCount, out int UpvalueMismatchCount, out int PatchedExportCount, out int RemovedExportCount, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaFunctionMigrationResult> FunctionMigrations) => throw null;
    }

    public enum LuaPatchModuleCommitStatus
    {
        NotExecuted = 0,
        RevisionConflict = 1,
        Executed = 2,
        Committed = 3,
        ExecutionFailed = 4,
        MigrationFailed = 5,
        CachePolicyFailed = 6,
        RolledBack = 7
    }

    public sealed class LuaPatchModuleMigrationSchema : System.IEquatable<Lunil.Hosting.LuaPatchModuleMigrationSchema>
    {
        public required string ModuleName { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchStateRule> State { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchResourceRule> Resources { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchModuleMigrationSchema? left, Lunil.Hosting.LuaPatchModuleMigrationSchema? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchModuleMigrationSchema? left, Lunil.Hosting.LuaPatchModuleMigrationSchema? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchModuleMigrationSchema? other) => throw null;
    }

    public sealed class LuaPatchModulePreflightResult : System.IEquatable<Lunil.Hosting.LuaPatchModulePreflightResult>
    {
        public string ModuleName { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchEntryKind Kind { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchPreflightStatus Status { get => throw null; init { } }
        public Lunil.Compiler.LuaCompilationResult? Compilation { get => throw null; init { } }
        public Lunil.IR.Canonical.LuaIrModule? Module { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaPatchModulePreflightResult(string ModuleName, Lunil.Hosting.LuaPatchEntryKind Kind, Lunil.Hosting.LuaPatchPreflightStatus Status, Lunil.Compiler.LuaCompilationResult? Compilation, Lunil.IR.Canonical.LuaIrModule? Module, string? Message) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchModulePreflightResult? left, Lunil.Hosting.LuaPatchModulePreflightResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchModulePreflightResult? left, Lunil.Hosting.LuaPatchModulePreflightResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchModulePreflightResult? other) => throw null;
        public void Deconstruct(out string ModuleName, out Lunil.Hosting.LuaPatchEntryKind Kind, out Lunil.Hosting.LuaPatchPreflightStatus Status, out Lunil.Compiler.LuaCompilationResult? Compilation, out Lunil.IR.Canonical.LuaIrModule? Module, out string? Message) => throw null;
    }

    public sealed class LuaPatchModulePrepareResult : System.IEquatable<Lunil.Hosting.LuaPatchModulePrepareResult>
    {
        public string ModuleName { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchPrepareStatus Status { get => throw null; init { } }
        public long? ExpectedRevision { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchModulePreflightResult Preflight { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaPatchModulePrepareResult(string ModuleName, Lunil.Hosting.LuaPatchPrepareStatus Status, long? ExpectedRevision, Lunil.Hosting.LuaPatchModulePreflightResult Preflight, string? Message) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchModulePrepareResult? left, Lunil.Hosting.LuaPatchModulePrepareResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchModulePrepareResult? left, Lunil.Hosting.LuaPatchModulePrepareResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchModulePrepareResult? other) => throw null;
        public void Deconstruct(out string ModuleName, out Lunil.Hosting.LuaPatchPrepareStatus Status, out long? ExpectedRevision, out Lunil.Hosting.LuaPatchModulePreflightResult Preflight, out string? Message) => throw null;
    }

    public static class LuaPatchPreflight
    {
        public static Lunil.Hosting.LuaPatchPreflightResult Analyze(Lunil.Hosting.LuaPatchBundle bundle, Lunil.Hosting.LuaHostOptions? hostOptions = null, Lunil.Hosting.ILuaPatchCanonicalIrDecoder? canonicalIrDecoder = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public sealed class LuaPatchPreflightResult : System.IEquatable<Lunil.Hosting.LuaPatchPreflightResult>
    {
        public Lunil.Hosting.LuaPatchManifest Manifest { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchDependencyPlan DependencyPlan { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModulePreflightResult> Modules { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaPatchPreflightResult(Lunil.Hosting.LuaPatchManifest Manifest, Lunil.Hosting.LuaPatchDependencyPlan DependencyPlan, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModulePreflightResult> Modules) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchPreflightResult? left, Lunil.Hosting.LuaPatchPreflightResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchPreflightResult? left, Lunil.Hosting.LuaPatchPreflightResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchPreflightResult? other) => throw null;
        public void Deconstruct(out Lunil.Hosting.LuaPatchManifest Manifest, out Lunil.Hosting.LuaPatchDependencyPlan DependencyPlan, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModulePreflightResult> Modules) => throw null;
    }

    public enum LuaPatchPreflightStatus
    {
        Ready = 0,
        LanguageVersionMismatch = 1,
        CompilationFailed = 2,
        ChunkValidationFailed = 3,
        CanonicalIrDecoderRequired = 4,
        CanonicalIrValidationFailed = 5
    }

    public sealed class LuaPatchPrepareOptions : System.IEquatable<Lunil.Hosting.LuaPatchPrepareOptions>
    {
        public static Lunil.Hosting.LuaPatchPrepareOptions Default { get => throw null; }
        public Lunil.Hosting.ILuaPatchCanonicalIrDecoder? CanonicalIrDecoder { get => throw null; init { } }
        public System.Collections.Generic.IReadOnlyDictionary<string, Lunil.Hosting.LuaModuleReloadOptions>? ModuleOptions { get => throw null; init { } }
        public System.Collections.Generic.IReadOnlyDictionary<string, Lunil.Hosting.ILuaPatchStateMigrationAdapter>? StateMigrationAdapters { get => throw null; init { } }
        public System.Collections.Generic.IReadOnlyDictionary<string, Lunil.Hosting.ILuaPatchResourceMigrationAdapter>? ResourceMigrationAdapters { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchResourceLimits ResourceLimits { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchAcceptancePolicy? AcceptancePolicy { get => throw null; init { } }
        public Lunil.Hosting.ILuaPatchReplayStore? ReplayStore { get => throw null; init { } }
        public System.TimeProvider TimeProvider { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchPrepareOptions? left, Lunil.Hosting.LuaPatchPrepareOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchPrepareOptions? left, Lunil.Hosting.LuaPatchPrepareOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchPrepareOptions? other) => throw null;
    }

    public sealed class LuaPatchPrepareResult : System.IEquatable<Lunil.Hosting.LuaPatchPrepareResult>
    {
        public Lunil.Hosting.LuaPatchPrepareStatus Status { get => throw null; init { } }
        public Lunil.Hosting.LuaPreparedPatch? PreparedPatch { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchPreflightResult Preflight { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModulePrepareResult> Modules { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchAcceptanceResult? Acceptance { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaPatchPrepareResult(Lunil.Hosting.LuaPatchPrepareStatus Status, Lunil.Hosting.LuaPreparedPatch? PreparedPatch, Lunil.Hosting.LuaPatchPreflightResult Preflight, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModulePrepareResult> Modules, string? Message) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchPrepareResult? left, Lunil.Hosting.LuaPatchPrepareResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchPrepareResult? left, Lunil.Hosting.LuaPatchPrepareResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchPrepareResult? other) => throw null;
        public void Deconstruct(out Lunil.Hosting.LuaPatchPrepareStatus Status, out Lunil.Hosting.LuaPreparedPatch? PreparedPatch, out Lunil.Hosting.LuaPatchPreflightResult Preflight, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchModulePrepareResult> Modules, out string? Message) => throw null;
    }

    public enum LuaPatchPrepareStatus
    {
        Ready = 0,
        PreflightFailed = 1,
        LanguageVersionMismatch = 2,
        ModuleNotLoaded = 3,
        UnsupportedCachePolicy = 4,
        MigrationAdapterMissing = 5,
        StateSchemaVersionMismatch = 6,
        AcceptanceRejected = 7
    }

    public sealed class LuaPatchRecoveryRecord : System.IEquatable<Lunil.Hosting.LuaPatchRecoveryRecord>
    {
        public string TransactionId { get => throw null; init { } }
        public string RolloutId { get => throw null; init { } }
        public string RingName { get => throw null; init { } }
        public string PatchId { get => throw null; init { } }
        public string TargetRevision { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchJournalPhase LastPhase { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> TargetIds { get => throw null; init { } }
        public System.DateTimeOffset LastUpdatedAt { get => throw null; init { } }
        public LuaPatchRecoveryRecord(string TransactionId, string RolloutId, string RingName, string PatchId, string TargetRevision, Lunil.Hosting.LuaPatchJournalPhase LastPhase, System.Collections.Immutable.ImmutableArray<string> TargetIds, System.DateTimeOffset LastUpdatedAt) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchRecoveryRecord? left, Lunil.Hosting.LuaPatchRecoveryRecord? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchRecoveryRecord? left, Lunil.Hosting.LuaPatchRecoveryRecord? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchRecoveryRecord? other) => throw null;
        public void Deconstruct(out string TransactionId, out string RolloutId, out string RingName, out string PatchId, out string TargetRevision, out Lunil.Hosting.LuaPatchJournalPhase LastPhase, out System.Collections.Immutable.ImmutableArray<string> TargetIds, out System.DateTimeOffset LastUpdatedAt) => throw null;
    }

    public enum LuaPatchRecoveryResolution
    {
        Manual = 0,
        Committed = 1,
        RolledBack = 2
    }

    public sealed class LuaPatchRecoveryResult : System.IEquatable<Lunil.Hosting.LuaPatchRecoveryResult>
    {
        public Lunil.Hosting.LuaPatchRecoveryRecord Record { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchRecoveryResolution Resolution { get => throw null; init { } }
        public LuaPatchRecoveryResult(Lunil.Hosting.LuaPatchRecoveryRecord Record, Lunil.Hosting.LuaPatchRecoveryResolution Resolution) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchRecoveryResult? left, Lunil.Hosting.LuaPatchRecoveryResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchRecoveryResult? left, Lunil.Hosting.LuaPatchRecoveryResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchRecoveryResult? other) => throw null;
        public void Deconstruct(out Lunil.Hosting.LuaPatchRecoveryRecord Record, out Lunil.Hosting.LuaPatchRecoveryResolution Resolution) => throw null;
    }

    public delegate bool LuaPatchReplayLookup(string patchId, string nonce);

    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Lunil.Hosting.LuaPatchResourceDisposition>))]
    public enum LuaPatchResourceDisposition
    {
        Continue = 0,
        Cancel = 1,
        Restart = 2,
        Drain = 3,
        RejectIfActive = 4
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Lunil.Hosting.LuaPatchResourceKind>))]
    public enum LuaPatchResourceKind
    {
        Coroutine = 0,
        Timer = 1,
        EventSubscription = 2,
        Task = 3
    }

    public sealed class LuaPatchResourceLimitException : System.Exception
    {
        public string LimitName { get => throw null; }
        public long Observed { get => throw null; }
        public long Maximum { get => throw null; }
        public LuaPatchResourceLimitException(string limitName, long observed, long maximum) { }
    }

    public sealed class LuaPatchResourceLimits : System.IEquatable<Lunil.Hosting.LuaPatchResourceLimits>
    {
        public static Lunil.Hosting.LuaPatchResourceLimits Default { get => throw null; }
        public int MaximumPatchModules { get => throw null; init { } }
        public int MaximumMigrationSchemaBytes { get => throw null; init { } }
        public int MaximumMigrationModules { get => throw null; init { } }
        public int MaximumStateMigrationRules { get => throw null; init { } }
        public int MaximumResourceMigrationRules { get => throw null; init { } }
        public int MaximumRingsPerRollout { get => throw null; init { } }
        public int MaximumTargetsPerRing { get => throw null; init { } }
        public int MaximumTargetsPerRollout { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchResourceLimits? left, Lunil.Hosting.LuaPatchResourceLimits? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchResourceLimits? left, Lunil.Hosting.LuaPatchResourceLimits? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchResourceLimits? other) => throw null;
    }

    public sealed class LuaPatchResourceMigrationContext : System.IEquatable<Lunil.Hosting.LuaPatchResourceMigrationContext>
    {
        public string ModuleName { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchResourceRule Rule { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue PreviousModuleState { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue CandidateModuleState { get => throw null; init { } }
        public LuaPatchResourceMigrationContext(string ModuleName, Lunil.Hosting.LuaPatchResourceRule Rule, Lunil.Runtime.Values.LuaValue PreviousModuleState, Lunil.Runtime.Values.LuaValue CandidateModuleState) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchResourceMigrationContext? left, Lunil.Hosting.LuaPatchResourceMigrationContext? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchResourceMigrationContext? left, Lunil.Hosting.LuaPatchResourceMigrationContext? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchResourceMigrationContext? other) => throw null;
        public void Deconstruct(out string ModuleName, out Lunil.Hosting.LuaPatchResourceRule Rule, out Lunil.Runtime.Values.LuaValue PreviousModuleState, out Lunil.Runtime.Values.LuaValue CandidateModuleState) => throw null;
    }

    public sealed class LuaPatchResourceRule : System.IEquatable<Lunil.Hosting.LuaPatchResourceRule>
    {
        public required string ResourceId { get => throw null; init { } }
        public required Lunil.Hosting.LuaPatchResourceKind Kind { get => throw null; init { } }
        public required Lunil.Hosting.LuaPatchResourceDisposition Disposition { get => throw null; init { } }
        public string? StatePath { get => throw null; init { } }
        public string? AdapterId { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchResourceRule? left, Lunil.Hosting.LuaPatchResourceRule? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchResourceRule? left, Lunil.Hosting.LuaPatchResourceRule? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchResourceRule? other) => throw null;
    }

    public sealed class LuaPatchRingCommitResult : System.IEquatable<Lunil.Hosting.LuaPatchRingCommitResult>
    {
        public string RolloutId { get => throw null; init { } }
        public string RingName { get => throw null; init { } }
        public string TransactionId { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchRingCommitStatus Status { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchTargetCommitResult> Targets { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaPatchRingCommitResult(string RolloutId, string RingName, string TransactionId, Lunil.Hosting.LuaPatchRingCommitStatus Status, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchTargetCommitResult> Targets, string? Message) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchRingCommitResult? left, Lunil.Hosting.LuaPatchRingCommitResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchRingCommitResult? left, Lunil.Hosting.LuaPatchRingCommitResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchRingCommitResult? other) => throw null;
        public void Deconstruct(out string RolloutId, out string RingName, out string TransactionId, out Lunil.Hosting.LuaPatchRingCommitStatus Status, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchTargetCommitResult> Targets, out string? Message) => throw null;
    }

    public enum LuaPatchRingCommitStatus
    {
        Committed = 0,
        Deferred = 1,
        Cancelled = 2,
        PrepareFailed = 3,
        PublishFailed = 4,
        HealthRejected = 5,
        JournalFailed = 6
    }

    public delegate Lunil.Hosting.LuaPatchRingHealthDecision LuaPatchRingHealthCallback(Lunil.Hosting.LuaPatchRingHealthContext context);

    public sealed class LuaPatchRingHealthContext : System.IEquatable<Lunil.Hosting.LuaPatchRingHealthContext>
    {
        public string RolloutId { get => throw null; init { } }
        public string RingName { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchTargetCommitResult> Targets { get => throw null; init { } }
        public LuaPatchRingHealthContext(string RolloutId, string RingName, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchTargetCommitResult> Targets) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchRingHealthContext? left, Lunil.Hosting.LuaPatchRingHealthContext? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchRingHealthContext? left, Lunil.Hosting.LuaPatchRingHealthContext? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchRingHealthContext? other) => throw null;
        public void Deconstruct(out string RolloutId, out string RingName, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchTargetCommitResult> Targets) => throw null;
    }

    public enum LuaPatchRingHealthDecision
    {
        Accept = 0,
        Rollback = 1
    }

    public sealed class LuaPatchRolloutPlan : System.IEquatable<Lunil.Hosting.LuaPatchRolloutPlan>
    {
        public required string RolloutId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchRolloutRing> Rings { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchRolloutPlan? left, Lunil.Hosting.LuaPatchRolloutPlan? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchRolloutPlan? left, Lunil.Hosting.LuaPatchRolloutPlan? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchRolloutPlan? other) => throw null;
    }

    public sealed class LuaPatchRolloutResult : System.IEquatable<Lunil.Hosting.LuaPatchRolloutResult>
    {
        public string RolloutId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchRingCommitResult> Rings { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaPatchRolloutResult(string RolloutId, System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchRingCommitResult> Rings) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchRolloutResult? left, Lunil.Hosting.LuaPatchRolloutResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchRolloutResult? left, Lunil.Hosting.LuaPatchRolloutResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchRolloutResult? other) => throw null;
        public void Deconstruct(out string RolloutId, out System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchRingCommitResult> Rings) => throw null;
    }

    public sealed class LuaPatchRolloutRing : System.IEquatable<Lunil.Hosting.LuaPatchRolloutRing>
    {
        public required string Name { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPatchDeploymentTarget> Targets { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchRolloutRing? left, Lunil.Hosting.LuaPatchRolloutRing? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchRolloutRing? left, Lunil.Hosting.LuaPatchRolloutRing? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchRolloutRing? other) => throw null;
    }

    public sealed class LuaPatchSignature : System.IEquatable<Lunil.Hosting.LuaPatchSignature>
    {
        public string Algorithm { get => throw null; init { } }
        public string KeyId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<byte> Value { get => throw null; init { } }
        public LuaPatchSignature(string Algorithm, string KeyId, System.Collections.Immutable.ImmutableArray<byte> Value) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchSignature? left, Lunil.Hosting.LuaPatchSignature? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchSignature? left, Lunil.Hosting.LuaPatchSignature? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchSignature? other) => throw null;
        public void Deconstruct(out string Algorithm, out string KeyId, out System.Collections.Immutable.ImmutableArray<byte> Value) => throw null;
    }

    public sealed class LuaPatchStateMigrationContext : System.IEquatable<Lunil.Hosting.LuaPatchStateMigrationContext>
    {
        public string ModuleName { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchStateRule Rule { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue PreviousValue { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue CandidateValue { get => throw null; init { } }
        public LuaPatchStateMigrationContext(string ModuleName, Lunil.Hosting.LuaPatchStateRule Rule, Lunil.Runtime.Values.LuaValue PreviousValue, Lunil.Runtime.Values.LuaValue CandidateValue) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchStateMigrationContext? left, Lunil.Hosting.LuaPatchStateMigrationContext? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchStateMigrationContext? left, Lunil.Hosting.LuaPatchStateMigrationContext? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchStateMigrationContext? other) => throw null;
        public void Deconstruct(out string ModuleName, out Lunil.Hosting.LuaPatchStateRule Rule, out Lunil.Runtime.Values.LuaValue PreviousValue, out Lunil.Runtime.Values.LuaValue CandidateValue) => throw null;
    }

    public sealed class LuaPatchStateRule : System.IEquatable<Lunil.Hosting.LuaPatchStateRule>
    {
        public required string TargetPath { get => throw null; init { } }
        public string? SourcePath { get => throw null; init { } }
        public required Lunil.Hosting.LuaPatchStateRuleKind Kind { get => throw null; init { } }
        public string? AdapterId { get => throw null; init { } }
        public bool Required { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchStateRule? left, Lunil.Hosting.LuaPatchStateRule? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchStateRule? left, Lunil.Hosting.LuaPatchStateRule? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchStateRule? other) => throw null;
    }

    [System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<Lunil.Hosting.LuaPatchStateRuleKind>))]
    public enum LuaPatchStateRuleKind
    {
        Preserve = 0,
        Drop = 1,
        HostAdapter = 2
    }

    public sealed class LuaPatchTargetCommitResult : System.IEquatable<Lunil.Hosting.LuaPatchTargetCommitResult>
    {
        public string TargetId { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchCommitResult Commit { get => throw null; init { } }
        public LuaPatchTargetCommitResult(string TargetId, Lunil.Hosting.LuaPatchCommitResult Commit) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchTargetCommitResult? left, Lunil.Hosting.LuaPatchTargetCommitResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchTargetCommitResult? left, Lunil.Hosting.LuaPatchTargetCommitResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchTargetCommitResult? other) => throw null;
        public void Deconstruct(out string TargetId, out Lunil.Hosting.LuaPatchCommitResult Commit) => throw null;
    }

    public static class LuaPatchTelemetry
    {
        public const string ActivitySourceName = "Lunil.Hosting.HotUpdate";
        public const string MeterName = "Lunil.Hosting.HotUpdate";
    }

    public sealed class LuaPatchTrustedEcdsaKey : System.IEquatable<Lunil.Hosting.LuaPatchTrustedEcdsaKey>
    {
        public string KeyId { get => throw null; init { } }
        public System.ReadOnlyMemory<byte> SubjectPublicKeyInfo { get => throw null; init { } }
        public LuaPatchTrustedEcdsaKey(string KeyId, System.ReadOnlyMemory<byte> SubjectPublicKeyInfo) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchTrustedEcdsaKey? left, Lunil.Hosting.LuaPatchTrustedEcdsaKey? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchTrustedEcdsaKey? left, Lunil.Hosting.LuaPatchTrustedEcdsaKey? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchTrustedEcdsaKey? other) => throw null;
        public void Deconstruct(out string KeyId, out System.ReadOnlyMemory<byte> SubjectPublicKeyInfo) => throw null;
    }

    public sealed class LuaPatchUpdateWindow : System.IDisposable
    {
        public System.TimeSpan MaximumDuration { get => throw null; }
        public System.TimeSpan Elapsed { get => throw null; }
        public bool IsActive { get => throw null; }
        public void Dispose() { }
    }

    public sealed class LuaPatchUpdateWindowOptions : System.IEquatable<Lunil.Hosting.LuaPatchUpdateWindowOptions>
    {
        public static Lunil.Hosting.LuaPatchUpdateWindowOptions Default { get => throw null; }
        public System.TimeSpan WaitTimeout { get => throw null; init { } }
        public System.TimeSpan MaximumDuration { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchUpdateWindowOptions? left, Lunil.Hosting.LuaPatchUpdateWindowOptions? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchUpdateWindowOptions? left, Lunil.Hosting.LuaPatchUpdateWindowOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchUpdateWindowOptions? other) => throw null;
    }

    public sealed class LuaPatchUpdateWindowResult : System.IEquatable<Lunil.Hosting.LuaPatchUpdateWindowResult>
    {
        public Lunil.Hosting.LuaPatchUpdateWindowStatus Status { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchUpdateWindow? Window { get => throw null; init { } }
        public string? Message { get => throw null; init { } }
        public bool Succeeded { get => throw null; }
        public LuaPatchUpdateWindowResult(Lunil.Hosting.LuaPatchUpdateWindowStatus Status, Lunil.Hosting.LuaPatchUpdateWindow? Window, string? Message) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPatchUpdateWindowResult? left, Lunil.Hosting.LuaPatchUpdateWindowResult? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPatchUpdateWindowResult? left, Lunil.Hosting.LuaPatchUpdateWindowResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPatchUpdateWindowResult? other) => throw null;
        public void Deconstruct(out Lunil.Hosting.LuaPatchUpdateWindowStatus Status, out Lunil.Hosting.LuaPatchUpdateWindow? Window, out string? Message) => throw null;
    }

    public enum LuaPatchUpdateWindowStatus
    {
        Opened = 0,
        Deferred = 1,
        Cancelled = 2
    }

    public sealed class LuaPreparedPatch
    {
        public Lunil.Hosting.LuaPatchManifest Manifest { get => throw null; }
        public Lunil.Hosting.LuaPatchDependencyPlan DependencyPlan { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.Hosting.LuaPreparedPatchModule> Modules { get => throw null; }
        public Lunil.Hosting.LuaPatchMigrationSchema? MigrationSchema { get => throw null; }
        public string? ExpectedStateSchemaVersion { get => throw null; }
    }

    public sealed class LuaPreparedPatchModule : System.IEquatable<Lunil.Hosting.LuaPreparedPatchModule>
    {
        public string ModuleName { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchEntryKind Kind { get => throw null; init { } }
        public long ExpectedRevision { get => throw null; init { } }
        public Lunil.IR.Canonical.LuaIrModule Module { get => throw null; init { } }
        public Lunil.Compiler.LuaCompilationResult? Compilation { get => throw null; init { } }
        public Lunil.Hosting.LuaModuleReloadOptions ReloadOptions { get => throw null; init { } }
        public Lunil.Hosting.LuaPatchModuleMigrationSchema? MigrationSchema { get => throw null; init { } }
        public LuaPreparedPatchModule(string ModuleName, Lunil.Hosting.LuaPatchEntryKind Kind, long ExpectedRevision, Lunil.IR.Canonical.LuaIrModule Module, Lunil.Compiler.LuaCompilationResult? Compilation, Lunil.Hosting.LuaModuleReloadOptions ReloadOptions, Lunil.Hosting.LuaPatchModuleMigrationSchema? MigrationSchema) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Hosting.LuaPreparedPatchModule? left, Lunil.Hosting.LuaPreparedPatchModule? right) => throw null;
        public static bool operator ==(Lunil.Hosting.LuaPreparedPatchModule? left, Lunil.Hosting.LuaPreparedPatchModule? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Hosting.LuaPreparedPatchModule? other) => throw null;
        public void Deconstruct(out string ModuleName, out Lunil.Hosting.LuaPatchEntryKind Kind, out long ExpectedRevision, out Lunil.IR.Canonical.LuaIrModule Module, out Lunil.Compiler.LuaCompilationResult? Compilation, out Lunil.Hosting.LuaModuleReloadOptions ReloadOptions, out Lunil.Hosting.LuaPatchModuleMigrationSchema? MigrationSchema) => throw null;
    }
}
