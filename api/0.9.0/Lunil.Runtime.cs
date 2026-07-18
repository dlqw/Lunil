// Target Frameworks: net10.0
#nullable enable

namespace Lunil.Runtime
{
    public static class LuaDebugApi
    {
        public static void SetHook(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Values.LuaValue hook, Lunil.Runtime.LuaDebugHookMask mask, int count) { }
        public static System.ValueTuple<Lunil.Runtime.Values.LuaValue, Lunil.Runtime.LuaDebugHookMask, int> GetHook(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread) => throw null;
        public static bool TryGetHookSubject(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, out Lunil.Runtime.Values.LuaValue function) => throw null;
        public static bool TryGetHookTransferRange(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, out int start, out int count) => throw null;
        public static Lunil.Runtime.LuaDebugLocal? GetHookTransfer(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, int index) => throw null;
        public static Lunil.Runtime.Execution.LuaFrame? GetFrame(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, int level) => throw null;
        public static Lunil.Runtime.Values.LuaValue GetFunction(Lunil.Runtime.Execution.LuaFrame frame) => throw null;
        public static int GetCurrentLine(Lunil.Runtime.Execution.LuaFrame frame) => throw null;
        public static int GetCurrentLine(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame) => throw null;
        public static Lunil.Runtime.LuaDebugLocal? GetLocal(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int index) => throw null;
        public static string? GetLocalName(Lunil.Runtime.Execution.LuaClosure closure, int index) => throw null;
        public static string? SetLocal(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int index, Lunil.Runtime.Values.LuaValue value) => throw null;
        public static System.Collections.Generic.IEnumerable<int> GetActiveLines(Lunil.Runtime.Execution.LuaClosure closure) => throw null;
    }

    [System.Flags]
    public enum LuaDebugHookMask
    {
        None = 0,
        Call = 1,
        Return = 2,
        Line = 4,
        Count = 8
    }

    public readonly struct LuaDebugLocal : System.IEquatable<Lunil.Runtime.LuaDebugLocal>
    {
        public string Name { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue Value { get => throw null; init { } }
        public LuaDebugLocal(string Name, Lunil.Runtime.Values.LuaValue Value) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Runtime.LuaDebugLocal left, Lunil.Runtime.LuaDebugLocal right) => throw null;
        public static bool operator ==(Lunil.Runtime.LuaDebugLocal left, Lunil.Runtime.LuaDebugLocal right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object obj) => throw null;
        public bool Equals(Lunil.Runtime.LuaDebugLocal other) => throw null;
        public void Deconstruct(out string Name, out Lunil.Runtime.Values.LuaValue Value) => throw null;
    }

    public enum LuaModuleLoaderKind
    {
        Preload = 0,
        LuaFile = 1,
        CustomSearcher = 2
    }

    public sealed class LuaModuleRecord : System.IEquatable<Lunil.Runtime.LuaModuleRecord>
    {
        public string Name { get => throw null; init { } }
        public Lunil.Runtime.LuaModuleLoaderKind LoaderKind { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue Loader { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue LoaderData { get => throw null; init { } }
        public Lunil.Runtime.Values.LuaValue CachedValue { get => throw null; init { } }
        public Lunil.IR.Canonical.LuaIrModule? Module { get => throw null; init { } }
        public long Revision { get => throw null; init { } }
        public LuaModuleRecord(string Name, Lunil.Runtime.LuaModuleLoaderKind LoaderKind, Lunil.Runtime.Values.LuaValue Loader, Lunil.Runtime.Values.LuaValue LoaderData, Lunil.Runtime.Values.LuaValue CachedValue, Lunil.IR.Canonical.LuaIrModule? Module, long Revision) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Runtime.LuaModuleRecord? left, Lunil.Runtime.LuaModuleRecord? right) => throw null;
        public static bool operator ==(Lunil.Runtime.LuaModuleRecord? left, Lunil.Runtime.LuaModuleRecord? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Runtime.LuaModuleRecord? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.Runtime.LuaModuleLoaderKind LoaderKind, out Lunil.Runtime.Values.LuaValue Loader, out Lunil.Runtime.Values.LuaValue LoaderData, out Lunil.Runtime.Values.LuaValue CachedValue, out Lunil.IR.Canonical.LuaIrModule? Module, out long Revision) => throw null;
    }

    public sealed class LuaRuntimeException : System.Exception
    {
        public bool HasErrorValue { get => throw null; }
        public Lunil.Runtime.Values.LuaValue ErrorValue { get => throw null; }
        public LuaRuntimeException(string message) { }
        public LuaRuntimeException(Lunil.Runtime.Values.LuaValue errorValue) { }
        public LuaRuntimeException(string message, System.Exception innerException) { }
    }

    public sealed class LuaState
    {
        public Lunil.Runtime.Memory.LuaHeap Heap { get => throw null; }
        public Lunil.Runtime.Values.LuaStringPool Strings { get => throw null; }
        public Lunil.Runtime.Values.LuaTable Globals { get => throw null; }
        public Lunil.Runtime.Values.LuaTable Registry { get => throw null; }
        public Lunil.Runtime.Execution.LuaThread MainThread { get => throw null; }
        public Lunil.Runtime.Execution.LuaThread? RunningThread { get => throw null; }
        public Lunil.Runtime.Values.LuaValue RunningNativeFunction { get => throw null; }
        public bool IsRunningFinalizer { get => throw null; }
        public bool IsIdle { get => throw null; }
        public event System.Action<Lunil.Runtime.Values.LuaValue>? WarningRaised;
        public LuaState(Lunil.Runtime.LuaStateOptions? options = null) { }
        public Lunil.Runtime.Values.LuaTable CreateTable(int arrayCapacity = 0, int hashCapacity = 0) => throw null;
        public Lunil.Runtime.Execution.LuaThread CreateThread(int initialStackCapacity = 128) => throw null;
        public Lunil.Runtime.Values.LuaUserdata CreateUserdata(object? payload = null, int userValueCount = 1, long payloadLogicalSize = 0) => throw null;
        public Lunil.Runtime.Execution.LuaThread CreateThread(Lunil.Runtime.Values.LuaValue entry, int initialStackCapacity = 128) => throw null;
        public Lunil.Runtime.Execution.LuaThread CreateThread(Lunil.Runtime.Execution.LuaClosure entry, int initialStackCapacity = 128) => throw null;
        public Lunil.Runtime.Execution.LuaNativeClosure CreateNativeClosure(Lunil.Runtime.Execution.LuaNativeFunction descriptor, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> captures = null) => throw null;
        public Lunil.Runtime.Memory.LuaHandle CreateHandle(Lunil.Runtime.Values.LuaValue value) => throw null;
        public bool TryGetModule(string name, out Lunil.Runtime.LuaModuleRecord? module) => throw null;
        public System.Collections.Generic.IReadOnlyList<string> GetLoadedModuleNames() => throw null;
        public Lunil.Runtime.Values.LuaTable? GetTypeMetatable(Lunil.Runtime.Values.LuaValueKind kind) => throw null;
        public void SetTypeMetatable(Lunil.Runtime.Values.LuaValueKind kind, Lunil.Runtime.Values.LuaTable? metatable) { }
        public void RaiseWarning(Lunil.Runtime.Values.LuaValue warning) { }
        public void SetGlobal(string name, Lunil.Runtime.Values.LuaValue value) { }
        public void InstallProtectedCallFunctions() { }
        public Lunil.Runtime.Values.LuaTable InstallCoroutineModule() => throw null;
        public Lunil.Runtime.Values.LuaValue GetGlobal(string name) => throw null;
        public Lunil.Runtime.Execution.LuaClosure CreateMainClosure(Lunil.IR.Canonical.LuaIrModule module) => throw null;
        public Lunil.Runtime.Execution.LuaClosure LoadBinaryChunk(System.ReadOnlySpan<byte> binaryChunk, Lunil.IR.Lua54.Lua54ChunkReaderOptions? options = null) => throw null;
        public Lunil.Runtime.Execution.LuaClosure LoadBinaryChunk(Lunil.IR.Lua54.Lua54Chunk chunk) => throw null;
    }

    public sealed class LuaStateOptions : System.IEquatable<Lunil.Runtime.LuaStateOptions>
    {
        public static Lunil.Runtime.LuaStateOptions Default { get => throw null; }
        public Lunil.Runtime.Memory.LuaHeapOptions Heap { get => throw null; init { } }
        public int MainThreadInitialStackCapacity { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Runtime.LuaStateOptions? left, Lunil.Runtime.LuaStateOptions? right) => throw null;
        public static bool operator ==(Lunil.Runtime.LuaStateOptions? left, Lunil.Runtime.LuaStateOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Runtime.LuaStateOptions? other) => throw null;
    }
}
namespace Lunil.Runtime.CodeGen
{
    public static class LuaCodegenAbiV1
    {
        public const int RuntimeAbiVersion = 1;
        public static Lunil.Runtime.Values.LuaValue ReadRegister(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int register) => throw null;
        public static void WriteRegister(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int register, Lunil.Runtime.Values.LuaValue value) { }
        public static void ClearRegisters(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int firstRegister, int count) { }
        public static void SetFrameTop(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int registerCount) { }
        public static Lunil.Runtime.Values.LuaValue ReadUpvalue(Lunil.Runtime.Execution.LuaFrame frame, int upvalue) => throw null;
        public static void WriteUpvalue(Lunil.Runtime.Execution.LuaFrame frame, int upvalue, Lunil.Runtime.Values.LuaValue value) { }
        public static Lunil.Runtime.Values.LuaValue MaterializeConstant(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaFrame frame, int constant) => throw null;
        public static Lunil.Runtime.Values.LuaValue CreateClosure(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaFrame parent, int functionId) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution GetIndex(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.Values.LuaValue key) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution SetIndex(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.Values.LuaValue key, Lunil.Runtime.Values.LuaValue value) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Unary(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.IR.Canonical.LuaIrUnaryOperator operation, Lunil.Runtime.Values.LuaValue operand) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Binary(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.IR.Canonical.LuaIrBinaryOperator operation, Lunil.Runtime.Values.LuaValue left, Lunil.Runtime.Values.LuaValue right) => throw null;
        public static bool IsTruthy(Lunil.Runtime.Values.LuaValue value) => throw null;
        public static bool CanExecuteCompiled(Lunil.Runtime.CodeGen.LuaExecutionContext context) => throw null;
        public static void ObserveCanonicalInstruction(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int programCounter) { }
        public static void ObserveLoopOsrBackedge(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaFrame frame, int programCounter) { }
        public static Lunil.Runtime.CodeGen.LuaCompiledExit ExecuteCanonicalInstruction(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int programCounter) => throw null;
        public static void CommitProgramCounter(Lunil.Runtime.Execution.LuaFrame frame, int programCounter) { }
    }

    public static class LuaCodegenAbiV2
    {
        public const int RuntimeAbiVersion = 2;
        public static bool CanExecuteCompiledFrame(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaFrame frame, int functionId, int registerCount) => throw null;
        public static bool CanEnterLoopOsr(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int functionId, int registerCount, int headerProgramCounter) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExitReason CheckLoopOsrHeader(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame) => throw null;
        public static Lunil.Runtime.Values.LuaValue ReadRegisterUnchecked(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int register) => throw null;
        public static void WriteRegisterUnchecked(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int register, Lunil.Runtime.Values.LuaValue value) { }
        public static void ClearRegistersUnchecked(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int firstRegister, int count) { }
        public static void SetFrameTopUnchecked(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int registerCount) { }
        public static bool ReadTruthyAndSetFrameTopUnchecked(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int register, int registerCount) => throw null;
        public static bool CanSkipClose(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int register) => throw null;
        public static bool CanExecuteUnaryPrimitive(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int operation, int operandRegister) => throw null;
        public static void ExecuteUnaryPrimitive(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int destinationRegister, int operation, int operandRegister) { }
        public static bool CanExecuteBinaryPrimitive(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int operation, int leftRegister, int rightRegister) => throw null;
        public static void ExecuteBinaryPrimitive(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int destinationRegister, int operation, int leftRegister, int rightRegister) { }
        public static void ExecuteNumericForPrepare(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int baseRegister, int exitProgramCounter) { }
        public static void ExecuteVarArg(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int destinationRegister, int resultCount) { }
        public static void ExecuteNumericForLoop(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int baseRegister, int bodyProgramCounter) { }
    }

    public static class LuaCodegenAbiV3
    {
        public const int RuntimeAbiVersion = 3;
        public static void ExecuteNewTable(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int destinationRegister, int hashCapacityBits, int arrayCapacity) { }
        public static bool ExecuteGetTable(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int destinationRegister, int targetRegister, int keyRegister) => throw null;
        public static bool ExecuteSetTable(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int targetRegister, int keyRegister, int valueRegister) => throw null;
        public static void ExecuteSetList(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int tableRegister, int sourceRegister, int count, int offset) { }
        public static void ExecuteClosure(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int destinationRegister, int functionId) { }
        public static void ExecuteVarArg(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int destinationRegister, int count) { }
        public static Lunil.Runtime.CodeGen.LuaCodegenPicExecutionResult TryExecuteTableGetPic(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, int destinationRegister, int targetRegister, int keyRegister) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCodegenPicExecutionResult TryExecuteTableSetPic(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, int targetRegister, int keyRegister, int valueRegister) => throw null;
        public static bool CanExecuteKnownClosureCall(Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, Lunil.Runtime.CodeGen.LuaCodegenCallSiteCache cache, int functionRegister, int expectedFunctionId) => throw null;
        public static int TryExecuteFramelessCall(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int functionRegister, int argumentCount, int expectedResults) => throw null;
        public static bool CanContinueAfterFramelessCall(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame) => throw null;
        public static bool PollGcSafepoint(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame) => throw null;
        public static void ExecuteKnownClosureCall(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int functionRegister, int argumentCount, int expectedResults) { }
        public static void ExecuteKnownClosureTailCall(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame frame, int functionRegister, int argumentCount) { }
    }

    public static class LuaCodegenAbiV4
    {
        public const int RuntimeAbiVersion = 4;
        public static void SetProgramCounter(Lunil.Runtime.Execution.LuaFrame frame, int programCounter) { }
        public static long GetInstructionsConsumed(Lunil.Runtime.CodeGen.LuaExecutionContext context) => throw null;
        public static bool TryExecuteDirectCompiledCall(Lunil.Runtime.CodeGen.LuaExecutionContext context, Lunil.Runtime.Execution.LuaThread thread, Lunil.Runtime.Execution.LuaFrame caller, Lunil.Runtime.CodeGen.LuaCodegenCallSiteCache cache, int functionRegister, int expectedFunctionId, int argumentCount, int expectedResults) => throw null;
        public static bool CanExecuteBoundDirectCall(Lunil.Runtime.CodeGen.LuaExecutionContext context) => throw null;
        public static bool CanExecuteKnownClosureValue(Lunil.Runtime.Values.LuaValue function, Lunil.Runtime.CodeGen.LuaCodegenCallSiteCache cache, int expectedFunctionId) => throw null;
        public static long Shift(long value, long count, bool left) => throw null;
        public static double FloatingModulo(double dividend, double divisor) => throw null;
        public static bool CompareMixed(long integerValue, double floatingPoint, bool integerOnLeft, int operationValue) => throw null;
    }

    public static class LuaCodegenAbiV5
    {
        public const int RuntimeAbiVersion = 5;
        public static bool TryGetCompilerProvenIntegerTableValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, long key, out Lunil.Runtime.Values.LuaValue value) => throw null;
        public static bool TrySetCompilerProvenIntegerTableValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, long key, Lunil.Runtime.Values.LuaValue value) => throw null;
        public static bool TrySetCompilerProvenIntegerTableIntegerValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, long key, long value) => throw null;
        public static bool TrySetCompilerProvenIntegerTableFloatValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, long key, double value) => throw null;
        public static bool TrySetCompilerProvenIntegerTableBooleanValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, long key, bool value) => throw null;
        public static bool TryGetCompilerProvenStringTableValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, ref Lunil.Runtime.CodeGen.LuaCodegenTableRegionSite regionSite, Lunil.Runtime.Values.LuaValue key, out Lunil.Runtime.Values.LuaValue value) => throw null;
        public static bool TrySetCompilerProvenStringTableValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, ref Lunil.Runtime.CodeGen.LuaCodegenTableRegionSite regionSite, Lunil.Runtime.Values.LuaValue key, Lunil.Runtime.Values.LuaValue value) => throw null;
        public static bool TrySetCompilerProvenStringTableIntegerValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, ref Lunil.Runtime.CodeGen.LuaCodegenTableRegionSite regionSite, Lunil.Runtime.Values.LuaValue key, long value) => throw null;
        public static bool TrySetCompilerProvenStringTableFloatValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, ref Lunil.Runtime.CodeGen.LuaCodegenTableRegionSite regionSite, Lunil.Runtime.Values.LuaValue key, double value) => throw null;
        public static bool TrySetCompilerProvenStringTableBooleanValue(ref Lunil.Runtime.Values.LuaTable? cachedTable, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.CodeGen.LuaCodegenTableSiteCache cache, ref Lunil.Runtime.CodeGen.LuaCodegenTableRegionSite regionSite, Lunil.Runtime.Values.LuaValue key, bool value) => throw null;
    }

    public sealed class LuaCodegenCallSiteCache
    {
    }

    public enum LuaCodegenPicExecutionResult
    {
        GuardFailure = 0,
        InstructionBudget = 1,
        Executed = 2
    }

    public struct LuaCodegenTableRegionSite
    {
    }

    public sealed class LuaCodegenTableSiteCache
    {
    }

    public readonly struct LuaCompiledExit : System.IEquatable<Lunil.Runtime.CodeGen.LuaCompiledExit>
    {
        public Lunil.Runtime.CodeGen.LuaCompiledExitKind Kind { get => throw null; }
        public int ProgramCounter { get => throw null; }
        public long InstructionsConsumed { get => throw null; }
        public Lunil.Runtime.CodeGen.LuaCompiledExitReason Reason { get => throw null; }
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Continue(int programCounter, int instructionsConsumed) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Continue(int programCounter, long instructionsConsumed) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Poll(int programCounter, int instructionsConsumed, Lunil.Runtime.CodeGen.LuaCompiledExitReason reason) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Poll(int programCounter, long instructionsConsumed, Lunil.Runtime.CodeGen.LuaCompiledExitReason reason) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Call(int programCounter, int instructionsConsumed) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Call(int programCounter, long instructionsConsumed) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit TailCall(int programCounter, int instructionsConsumed) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit TailCall(int programCounter, long instructionsConsumed) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Return(int programCounter, int instructionsConsumed) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Return(int programCounter, long instructionsConsumed) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Deopt(int programCounter, int instructionsConsumed, Lunil.Runtime.CodeGen.LuaCompiledExitReason reason) => throw null;
        public static Lunil.Runtime.CodeGen.LuaCompiledExit Deopt(int programCounter, long instructionsConsumed, Lunil.Runtime.CodeGen.LuaCompiledExitReason reason) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.Runtime.CodeGen.LuaCompiledExit left, Lunil.Runtime.CodeGen.LuaCompiledExit right) => throw null;
        public static bool operator ==(Lunil.Runtime.CodeGen.LuaCompiledExit left, Lunil.Runtime.CodeGen.LuaCompiledExit right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.Runtime.CodeGen.LuaCompiledExit other) => throw null;
    }

    public enum LuaCompiledExitKind
    {
        Continue = 0,
        Poll = 1,
        Call = 2,
        TailCall = 3,
        Return = 4,
        Deopt = 5
    }

    public enum LuaCompiledExitReason
    {
        None = 0,
        InstructionBudget = 1,
        DebugModeChanged = 2,
        GarbageCollection = 3,
        BackendInvalidated = 4,
        GuardFailure = 5,
        UnsupportedInstruction = 6
    }

    public sealed class LuaExecutionContext
    {
        public Lunil.Runtime.LuaState State { get => throw null; }
        public Lunil.Runtime.Execution.LuaThread Thread { get => throw null; }
        public long RemainingInstructionCount { get => throw null; }
        public ulong DebugModeVersion { get => throw null; }
        public bool HasExactDebugHooks { get => throw null; }
        public bool TryReserveInstructions(int instructionCount) => throw null;
        public bool IsDebugModeCurrent() => throw null;
        public bool IsBackendGenerationCurrent() => throw null;
    }
}
namespace Lunil.Runtime.Execution
{
    public sealed class LuaClosure : Lunil.Runtime.Memory.LuaGcObject
    {
        public Lunil.IR.Canonical.LuaIrModule Module { get => throw null; }
        public Lunil.IR.Canonical.LuaIrFunction Function { get => throw null; }
        public Lunil.Runtime.Execution.LuaFunctionVersion FunctionVersion { get => throw null; }
        public System.Collections.Generic.IReadOnlyList<Lunil.Runtime.Execution.LuaUpvalue> Upvalues { get => throw null; }
        public Lunil.Runtime.Execution.LuaUpvalue GetUpvalue(int index) => throw null;
        public void JoinUpvalue(int index, Lunil.Runtime.Execution.LuaClosure source, int sourceIndex) { }
    }

    public sealed class LuaExecutionResult : System.IEquatable<Lunil.Runtime.Execution.LuaExecutionResult>
    {
        public Lunil.Runtime.Execution.LuaVmSignal Signal { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Runtime.Values.LuaValue> Values { get => throw null; init { } }
        public LuaExecutionResult(Lunil.Runtime.Execution.LuaVmSignal Signal, System.Collections.Immutable.ImmutableArray<Lunil.Runtime.Values.LuaValue> Values) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Runtime.Execution.LuaExecutionResult? left, Lunil.Runtime.Execution.LuaExecutionResult? right) => throw null;
        public static bool operator ==(Lunil.Runtime.Execution.LuaExecutionResult? left, Lunil.Runtime.Execution.LuaExecutionResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Runtime.Execution.LuaExecutionResult? other) => throw null;
        public void Deconstruct(out Lunil.Runtime.Execution.LuaVmSignal Signal, out System.Collections.Immutable.ImmutableArray<Lunil.Runtime.Values.LuaValue> Values) => throw null;
    }

    public sealed class LuaExecutor
    {
        public Lunil.Runtime.Execution.LuaExecutorOptions Options { get => throw null; }
        public LuaExecutor(Lunil.Runtime.Execution.LuaExecutorOptions? options = null) { }
        public Lunil.Runtime.Execution.LuaExecutionResult Execute(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaClosure closure, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult ExecuteBinaryChunk(Lunil.Runtime.LuaState state, System.ReadOnlySpan<byte> binaryChunk, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null, Lunil.IR.Lua54.Lua54ChunkReaderOptions? readerOptions = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Start(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Resume(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Close(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread) => throw null;
    }

    public sealed class LuaExecutorOptions : System.IEquatable<Lunil.Runtime.Execution.LuaExecutorOptions>
    {
        public static Lunil.Runtime.Execution.LuaExecutorOptions Default { get => throw null; }
        public Lunil.Runtime.Execution.LuaInterpreterOptions Interpreter { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Runtime.Execution.LuaExecutorOptions? left, Lunil.Runtime.Execution.LuaExecutorOptions? right) => throw null;
        public static bool operator ==(Lunil.Runtime.Execution.LuaExecutorOptions? left, Lunil.Runtime.Execution.LuaExecutorOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Runtime.Execution.LuaExecutorOptions? other) => throw null;
    }

    public sealed class LuaFrame
    {
        public Lunil.Runtime.Execution.LuaClosure Closure { get => throw null; }
        public Lunil.Runtime.Execution.LuaFunctionVersion FunctionVersion { get => throw null; }
        public int Base { get => throw null; }
        public int Top { get => throw null; }
        public int ProgramCounter { get => throw null; }
        public int ReturnBase { get => throw null; }
        public int ExpectedResults { get => throw null; }
        public System.Collections.Generic.IReadOnlyList<Lunil.Runtime.Values.LuaValue> VarArgs { get => throw null; }
        public bool IsDebugHook { get => throw null; }
        public bool IsHidden { get => throw null; }
        public bool IsTailCall { get => throw null; }
        public string? DebugFunctionName { get => throw null; }
        public string? DebugFunctionNameWhat { get => throw null; }
    }

    public static class LuaFunctionIdentity
    {
        public static string GetLogicalKey(Lunil.IR.Canonical.LuaIrModule module, int functionId) => throw null;
        public static string GetUpvalueLayoutFingerprint(Lunil.IR.Canonical.LuaIrFunction function) => throw null;
    }

    public sealed class LuaFunctionVersion
    {
        public Lunil.IR.Canonical.LuaIrModule Module { get => throw null; }
        public Lunil.IR.Canonical.LuaIrFunction Function { get => throw null; }
        public long Generation { get => throw null; }
        public string LogicalKey { get => throw null; }
        public string UpvalueLayoutFingerprint { get => throw null; }
    }

    public sealed class LuaInterpreter
    {
        public LuaInterpreter(Lunil.Runtime.Execution.LuaInterpreterOptions? options = null) { }
        public Lunil.Runtime.Execution.LuaExecutionResult Execute(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaClosure closure, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult ExecuteBinaryChunk(Lunil.Runtime.LuaState state, System.ReadOnlySpan<byte> binaryChunk, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null, Lunil.IR.Lua54.Lua54ChunkReaderOptions? readerOptions = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Start(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Resume(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments = null) => throw null;
        public Lunil.Runtime.Execution.LuaExecutionResult Close(Lunil.Runtime.LuaState state, Lunil.Runtime.Execution.LuaThread thread) => throw null;
    }

    public sealed class LuaInterpreterOptions : System.IEquatable<Lunil.Runtime.Execution.LuaInterpreterOptions>
    {
        public static Lunil.Runtime.Execution.LuaInterpreterOptions Default { get => throw null; }
        public long MaximumInstructionCount { get => throw null; init { } }
        public int MaximumStackSlots { get => throw null; init { } }
        public int MaximumCallDepth { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Runtime.Execution.LuaInterpreterOptions? left, Lunil.Runtime.Execution.LuaInterpreterOptions? right) => throw null;
        public static bool operator ==(Lunil.Runtime.Execution.LuaInterpreterOptions? left, Lunil.Runtime.Execution.LuaInterpreterOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Runtime.Execution.LuaInterpreterOptions? other) => throw null;
    }

    public readonly struct LuaNativeCallContext
    {
        public Lunil.Runtime.LuaState State { get => throw null; }
        public Lunil.Runtime.Execution.LuaThread Thread { get => throw null; }
        public Lunil.Runtime.Execution.LuaNativeClosure? Closure { get => throw null; }
        public System.Collections.Generic.IReadOnlyList<Lunil.Runtime.Values.LuaValue> Captures { get => throw null; }
        public System.Collections.Generic.IReadOnlyList<Lunil.Runtime.Values.LuaValue> InvocationState { get => throw null; }
        public Lunil.Runtime.Execution.LuaNativeCallContext WithInvocationState(System.Collections.Generic.IReadOnlyList<Lunil.Runtime.Values.LuaValue> invocationState) => throw null;
    }

    public sealed class LuaNativeClosure : Lunil.Runtime.Memory.LuaGcObject
    {
        public Lunil.Runtime.Execution.LuaNativeFunction Descriptor { get => throw null; }
        public System.Collections.Generic.IReadOnlyList<Lunil.Runtime.Values.LuaValue> Captures { get => throw null; }
        public int CaptureCount { get => throw null; }
        public Lunil.Runtime.Values.LuaValue GetCapture(int index) => throw null;
        public void SetCapture(int index, Lunil.Runtime.Values.LuaValue value) { }
        public object GetCaptureIdentity(int index) => throw null;
    }

    public sealed class LuaNativeFunction
    {
        public string Name { get => throw null; }
        public LuaNativeFunction(string name, Lunil.Runtime.Execution.LuaNativeFunctionBody body) { }
        public LuaNativeFunction(string name, Lunil.Runtime.Execution.LuaNativeFunctionStepBody stepBody) { }
    }

    public delegate Lunil.Runtime.Values.LuaValue[] LuaNativeFunctionBody(Lunil.Runtime.LuaState state, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments);

    public delegate Lunil.Runtime.Execution.LuaNativeStep LuaNativeFunctionStepBody(Lunil.Runtime.Execution.LuaNativeCallContext context, int continuationId, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> values);

    public readonly struct LuaNativeStep
    {
        public Lunil.Runtime.Execution.LuaNativeStepKind Kind { get => throw null; }
        public Lunil.Runtime.Values.LuaValue Callable { get => throw null; }
        public Lunil.Runtime.Values.LuaValue[] Values { get => throw null; }
        public int ContinuationId { get => throw null; }
        public Lunil.Runtime.Values.LuaValue[] StateValues { get => throw null; }
        public bool CallIsYieldable { get => throw null; }
        public bool CallIsProtected { get => throw null; }
        public static Lunil.Runtime.Execution.LuaNativeStep Completed(params Lunil.Runtime.Values.LuaValue[] values) => throw null;
        public static Lunil.Runtime.Execution.LuaNativeStep CallLua(Lunil.Runtime.Values.LuaValue callable, Lunil.Runtime.Values.LuaValue[] arguments, int continuationId, Lunil.Runtime.Values.LuaValue[]? stateValues = null, bool callIsYieldable = true, bool callIsProtected = false) => throw null;
        public static Lunil.Runtime.Execution.LuaNativeStep Yielded(Lunil.Runtime.Values.LuaValue[] values, int continuationId, Lunil.Runtime.Values.LuaValue[]? stateValues = null) => throw null;
    }

    public enum LuaNativeStepKind
    {
        Completed = 0,
        CallLua = 1,
        Yielded = 2
    }

    public sealed class LuaStack
    {
        public int Capacity { get => throw null; }
        public Lunil.Runtime.Values.LuaValue this[int index] { get => throw null; set { } }
        public void EnsureCapacity(int required) { }
        public void Clear(int start, int length) { }
    }

    public sealed class LuaThread : Lunil.Runtime.Memory.LuaGcObject
    {
        public Lunil.Runtime.Execution.LuaStack Stack { get => throw null; }
        public Lunil.Runtime.Execution.LuaThreadStatus Status { get => throw null; }
        public Lunil.Runtime.Values.LuaValue Entry { get => throw null; }
        public bool Started { get => throw null; }
        public Lunil.Runtime.Values.LuaValue TerminalError { get => throw null; }
        public System.Collections.Generic.IReadOnlyList<Lunil.Runtime.Values.LuaValue> YieldedValues { get => throw null; }
        public System.Collections.Generic.IReadOnlyList<Lunil.Runtime.Values.LuaValue> ResumeValues { get => throw null; }
        public System.Collections.Generic.IReadOnlyList<Lunil.Runtime.Execution.LuaFrame> Frames { get => throw null; }
        public Lunil.Runtime.Values.LuaValue DebugHook { get => throw null; }
        public Lunil.Runtime.LuaDebugHookMask DebugHookMask { get => throw null; }
        public int DebugHookCount { get => throw null; }
    }

    public enum LuaThreadStatus
    {
        New = 0,
        Suspended = 1,
        Running = 2,
        Normal = 3,
        Dead = 4,
        Error = 5
    }

    public sealed class LuaUpvalue : Lunil.Runtime.Memory.LuaGcObject
    {
        public bool IsOpen { get => throw null; }
        public Lunil.Runtime.Values.LuaValue Value { get => throw null; set { } }
    }

    public enum LuaVmSignal
    {
        Completed = 0,
        Yielded = 1,
        Error = 2
    }
}
namespace Lunil.Runtime.Memory
{
    public enum LuaGcAge
    {
        New = 0,
        Survival = 1,
        Old0 = 2,
        Old1 = 3,
        Old = 4
    }

    public enum LuaGcColor
    {
        White = 0,
        Gray = 1,
        Black = 2
    }

    public enum LuaGcCycleKind
    {
        Full = 0,
        Minor = 1
    }

    public enum LuaGcFinalizationState
    {
        None = 0,
        Pending = 1,
        Finalized = 2
    }

    public enum LuaGcMode
    {
        Incremental = 0,
        Generational = 1
    }

    public abstract class LuaGcObject
    {
        public Lunil.Runtime.Memory.LuaHeap Owner { get => throw null; }
        public long ObjectId { get => throw null; }
        public long LogicalSize { get => throw null; }
        public bool IsAlive { get => throw null; }
        public Lunil.Runtime.Memory.LuaGcColor Color { get => throw null; }
        public Lunil.Runtime.Memory.LuaGcAge Age { get => throw null; }
        public Lunil.Runtime.Memory.LuaGcFinalizationState FinalizationState { get => throw null; }
        protected LuaGcObject(Lunil.Runtime.Memory.LuaHeap owner, long logicalSize) { }
    }

    public enum LuaGcPhase
    {
        Paused = 0,
        Propagate = 1,
        Atomic = 2,
        Sweep = 3,
        Finalize = 4
    }

    public sealed class LuaHandle : System.IDisposable
    {
        public Lunil.Runtime.Values.LuaValue Value { get => throw null; set { } }
        public void Dispose() { }
    }

    public sealed class LuaHeap
    {
        public long LogicalBytes { get => throw null; }
        public long MaximumLogicalBytes { get => throw null; }
        public int HashSeed { get => throw null; }
        public int ObjectCount { get => throw null; }
        public int HandleCount { get => throw null; }
        public int RememberedObjectCount { get => throw null; }
        public int PendingFinalizerCount { get => throw null; }
        public long CompletedCycleCount { get => throw null; }
        public long CollectedObjectCount { get => throw null; }
        public Lunil.Runtime.Memory.LuaGcMode Mode { get => throw null; set { } }
        public Lunil.Runtime.Memory.LuaGcPhase Phase { get => throw null; }
        public bool StressEveryAllocation { get => throw null; }
        public bool IsRunning { get => throw null; }
        public int Pause { get => throw null; }
        public int StepMultiplier { get => throw null; }
        public LuaHeap(Lunil.Runtime.Memory.LuaHeapOptions? options = null) { }
        public Lunil.Runtime.Memory.LuaHandle CreateHandle(Lunil.Runtime.Values.LuaValue value) => throw null;
        public void ValidateValue(Lunil.Runtime.Values.LuaValue value) { }
        public void SafePoint() { }
        public void Step(int objectBudget = -1) { }
        public void CollectFull() { }
        public void CollectMinor() { }
        public void Stop() { }
        public void Restart() { }
        public int SetPause(int value) => throw null;
        public int SetStepMultiplier(int value) => throw null;
        public int RunPendingFinalizers(System.Action<Lunil.Runtime.Memory.LuaGcObject, Lunil.Runtime.Values.LuaValue> callback, int maximumCount = 2147483647) => throw null;
        public int RunPendingFinalizers(System.Func<Lunil.Runtime.Memory.LuaGcObject, Lunil.Runtime.Values.LuaValue, bool> callback, int maximumCount = 2147483647) => throw null;
        public void WriteBarrier(Lunil.Runtime.Memory.LuaGcObject owner, Lunil.Runtime.Values.LuaValue value) { }
        public void WriteBarrier(Lunil.Runtime.Memory.LuaGcObject owner, Lunil.Runtime.Memory.LuaGcObject target) { }
        public void WriteBarrierBack(Lunil.Runtime.Memory.LuaGcObject owner, Lunil.Runtime.Values.LuaValue value) { }
    }

    public sealed class LuaHeapOptions : System.IEquatable<Lunil.Runtime.Memory.LuaHeapOptions>
    {
        public static Lunil.Runtime.Memory.LuaHeapOptions Default { get => throw null; }
        public long MaximumLogicalBytes { get => throw null; init { } }
        public long StepSizeBytes { get => throw null; init { } }
        public int StepObjectBudget { get => throw null; init { } }
        public int MinorCyclesBeforeMajor { get => throw null; init { } }
        public Lunil.Runtime.Memory.LuaGcMode InitialMode { get => throw null; init { } }
        public bool StressEveryAllocation { get => throw null; init { } }
        public int? HashSeed { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Runtime.Memory.LuaHeapOptions? left, Lunil.Runtime.Memory.LuaHeapOptions? right) => throw null;
        public static bool operator ==(Lunil.Runtime.Memory.LuaHeapOptions? left, Lunil.Runtime.Memory.LuaHeapOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Runtime.Memory.LuaHeapOptions? other) => throw null;
    }

    [System.Flags]
    public enum LuaWeakMode
    {
        None = 0,
        Keys = 1,
        Values = 2
    }
}
namespace Lunil.Runtime.Operations
{
    public readonly struct LuaOperationResolution : System.IEquatable<Lunil.Runtime.Operations.LuaOperationResolution>
    {
        public bool RequiresCall { get => throw null; }
        public Lunil.Runtime.Values.LuaValue Value { get => throw null; }
        public Lunil.Runtime.Values.LuaValue Callable { get => throw null; }
        public int ArgumentCount { get => throw null; }
        public Lunil.Runtime.Values.LuaValue[] Arguments { get => throw null; }
        public Lunil.Runtime.Operations.LuaResultTransform Transform { get => throw null; }
        public Lunil.Runtime.Values.LuaValue GetArgument(int index) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Immediate(Lunil.Runtime.Values.LuaValue value) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Call(Lunil.Runtime.Values.LuaValue callable, Lunil.Runtime.Values.LuaValue argument, Lunil.Runtime.Operations.LuaResultTransform transform = 0) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Call(Lunil.Runtime.Values.LuaValue callable, Lunil.Runtime.Values.LuaValue argument0, Lunil.Runtime.Values.LuaValue argument1, Lunil.Runtime.Operations.LuaResultTransform transform = 0) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Call(Lunil.Runtime.Values.LuaValue callable, Lunil.Runtime.Values.LuaValue argument0, Lunil.Runtime.Values.LuaValue argument1, Lunil.Runtime.Values.LuaValue argument2, Lunil.Runtime.Operations.LuaResultTransform transform = 0) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Call(Lunil.Runtime.Values.LuaValue callable, Lunil.Runtime.Values.LuaValue[] arguments, Lunil.Runtime.Operations.LuaResultTransform transform = 0) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Call(Lunil.Runtime.Values.LuaValue callable, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments, Lunil.Runtime.Operations.LuaResultTransform transform = 0) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.Runtime.Operations.LuaOperationResolution left, Lunil.Runtime.Operations.LuaOperationResolution right) => throw null;
        public static bool operator ==(Lunil.Runtime.Operations.LuaOperationResolution left, Lunil.Runtime.Operations.LuaOperationResolution right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.Runtime.Operations.LuaOperationResolution other) => throw null;
    }

    public enum LuaResultTransform
    {
        None = 0,
        LogicalNot = 1
    }

    public static class LuaRuntimeOperations
    {
        public static Lunil.Runtime.Operations.LuaOperationResolution GetIndex(Lunil.Runtime.LuaState state, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.Values.LuaValue key) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution SetIndex(Lunil.Runtime.LuaState state, Lunil.Runtime.Values.LuaValue target, Lunil.Runtime.Values.LuaValue key, Lunil.Runtime.Values.LuaValue value) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Unary(Lunil.Runtime.LuaState state, Lunil.IR.Canonical.LuaIrUnaryOperator operation, Lunil.Runtime.Values.LuaValue operand) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution Binary(Lunil.Runtime.LuaState state, Lunil.IR.Canonical.LuaIrBinaryOperator operation, Lunil.Runtime.Values.LuaValue left, Lunil.Runtime.Values.LuaValue right) => throw null;
        public static Lunil.Runtime.Operations.LuaOperationResolution ResolveCall(Lunil.Runtime.LuaState state, Lunil.Runtime.Values.LuaValue callable, System.ReadOnlySpan<Lunil.Runtime.Values.LuaValue> arguments) => throw null;
    }
}
namespace Lunil.Runtime.Values
{
    public sealed class LuaLightUserdata
    {
        public object Identity { get => throw null; }
        public LuaLightUserdata(object identity) { }
    }

    public sealed class LuaString : Lunil.Runtime.Memory.LuaGcObject, System.IEquatable<Lunil.Runtime.Values.LuaString>
    {
        public int Length { get => throw null; }
        public System.ReadOnlySpan<byte> AsSpan() => throw null;
        public byte[] ToArray() => throw null;
        public bool Equals(Lunil.Runtime.Values.LuaString? other) => throw null;
        public override bool Equals(object? obj) => throw null;
        public override int GetHashCode() => throw null;
        public override string ToString() => throw null;
    }

    public sealed class LuaStringPool
    {
        public Lunil.Runtime.Values.LuaString GetOrCreate(System.ReadOnlySpan<byte> bytes) => throw null;
    }

    public sealed class LuaTable : Lunil.Runtime.Memory.LuaGcObject
    {
        public int ArrayCapacity { get => throw null; }
        public int HashCount { get => throw null; }
        public int TombstoneCount { get => throw null; }
        public ulong StorageVersion { get => throw null; }
        public ulong ShapeVersion { get => throw null; }
        public ulong MetatableVersion { get => throw null; }
        public Lunil.Runtime.Values.LuaTable? Metatable { get => throw null; }
        public int ArrayLength { get => throw null; }
        public Lunil.Runtime.Values.LuaValue Get(Lunil.Runtime.Values.LuaValue key) => throw null;
        public void Set(Lunil.Runtime.Values.LuaValue key, Lunil.Runtime.Values.LuaValue value) { }
        public void SetMetatable(Lunil.Runtime.Values.LuaTable? metatable) { }
        public bool Next(Lunil.Runtime.Values.LuaValue key, out Lunil.Runtime.Values.LuaValue nextKey, out Lunil.Runtime.Values.LuaValue nextValue) => throw null;
    }

    public sealed class LuaUserdata : Lunil.Runtime.Memory.LuaGcObject
    {
        public object? Payload { get => throw null; }
        public int UserValueCount { get => throw null; }
        public Lunil.Runtime.Values.LuaTable? Metatable { get => throw null; }
        public T GetPayload<T>() where T : class => throw null;
        public Lunil.Runtime.Values.LuaValue GetUserValue(int index) => throw null;
        public void SetUserValue(int index, Lunil.Runtime.Values.LuaValue value) { }
        public void SetMetatable(Lunil.Runtime.Values.LuaTable? metatable) { }
        public void DisposePayload() { }
    }

    public readonly struct LuaValue : System.IEquatable<Lunil.Runtime.Values.LuaValue>
    {
        public Lunil.Runtime.Values.LuaValueKind Kind { get => throw null; }
        public bool IsNil { get => throw null; }
        public bool IsTruthy { get => throw null; }
        public static Lunil.Runtime.Values.LuaValue Nil { get => throw null; }
        public static Lunil.Runtime.Values.LuaValue FromBoolean(bool value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromInteger(long value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromFloat(double value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromString(Lunil.Runtime.Values.LuaString value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromTable(Lunil.Runtime.Values.LuaTable value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromFunction(Lunil.Runtime.Execution.LuaClosure value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromFunction(Lunil.Runtime.Execution.LuaNativeFunction value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromFunction(Lunil.Runtime.Execution.LuaNativeClosure value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromThread(Lunil.Runtime.Execution.LuaThread value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromUserdata(Lunil.Runtime.Values.LuaUserdata value) => throw null;
        public static Lunil.Runtime.Values.LuaValue FromLightUserdata(Lunil.Runtime.Values.LuaLightUserdata value) => throw null;
        public bool AsBoolean() => throw null;
        public long AsInteger() => throw null;
        public double AsFloat() => throw null;
        public Lunil.Runtime.Values.LuaString AsString() => throw null;
        public Lunil.Runtime.Values.LuaTable AsTable() => throw null;
        public Lunil.Runtime.Execution.LuaClosure? TryGetClosure() => throw null;
        public Lunil.Runtime.Execution.LuaNativeFunction? TryGetNativeFunction() => throw null;
        public Lunil.Runtime.Execution.LuaNativeClosure? TryGetNativeClosure() => throw null;
        public Lunil.Runtime.Execution.LuaThread AsThread() => throw null;
        public Lunil.Runtime.Values.LuaUserdata AsUserdata() => throw null;
        public Lunil.Runtime.Values.LuaLightUserdata AsLightUserdata() => throw null;
        public Lunil.Runtime.Memory.LuaGcObject? TryGetGcObject() => throw null;
        public bool TryGetInteger(out long value) => throw null;
        public bool Equals(Lunil.Runtime.Values.LuaValue other) => throw null;
        public override bool Equals(object? obj) => throw null;
        public static bool operator ==(Lunil.Runtime.Values.LuaValue left, Lunil.Runtime.Values.LuaValue right) => throw null;
        public static bool operator !=(Lunil.Runtime.Values.LuaValue left, Lunil.Runtime.Values.LuaValue right) => throw null;
        public override int GetHashCode() => throw null;
        public override string ToString() => throw null;
    }

    public enum LuaValueKind
    {
        Nil = 0,
        Boolean = 1,
        Integer = 2,
        Float = 3,
        String = 4,
        Table = 5,
        Function = 6,
        Thread = 7,
        Userdata = 8,
        LightUserdata = 9
    }

    public static class LuaValueOperations
    {
        public static string TypeName(Lunil.Runtime.Values.LuaValue value) => throw null;
        public static string BasicTypeName(Lunil.Runtime.Values.LuaValue value) => throw null;
        public static string FormatFloat(double value) => throw null;
        public static bool NumberEquals(Lunil.Runtime.Values.LuaValue left, Lunil.Runtime.Values.LuaValue right) => throw null;
        public static Lunil.Runtime.Values.LuaValue Unary(Lunil.IR.Canonical.LuaIrUnaryOperator operation, Lunil.Runtime.Values.LuaValue operand) => throw null;
        public static Lunil.Runtime.Values.LuaValue Binary(Lunil.Runtime.LuaState state, Lunil.IR.Canonical.LuaIrBinaryOperator operation, Lunil.Runtime.Values.LuaValue left, Lunil.Runtime.Values.LuaValue right) => throw null;
        public static bool TryToNumber(Lunil.Runtime.Values.LuaValue value, out Lunil.Runtime.Values.LuaValue number) => throw null;
    }
}
