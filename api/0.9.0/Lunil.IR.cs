// Target Frameworks: net10.0
#nullable enable

namespace Lunil.IR.Canonical
{
    public readonly struct LuaIrBasicBlock : System.IEquatable<Lunil.IR.Canonical.LuaIrBasicBlock>
    {
        public int Start { get => throw null; init { } }
        public int Length { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> Successors { get => throw null; init { } }
        public int End { get => throw null; }
        public LuaIrBasicBlock(int Start, int Length, System.Collections.Immutable.ImmutableArray<int> Successors) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Canonical.LuaIrBasicBlock left, Lunil.IR.Canonical.LuaIrBasicBlock right) => throw null;
        public static bool operator ==(Lunil.IR.Canonical.LuaIrBasicBlock left, Lunil.IR.Canonical.LuaIrBasicBlock right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Canonical.LuaIrBasicBlock other) => throw null;
        public void Deconstruct(out int Start, out int Length, out System.Collections.Immutable.ImmutableArray<int> Successors) => throw null;
    }

    public enum LuaIrBinaryOperator
    {
        Add = 0,
        Subtract = 1,
        Multiply = 2,
        Divide = 3,
        FloorDivide = 4,
        Modulo = 5,
        Power = 6,
        Concatenate = 7,
        Equal = 8,
        NotEqual = 9,
        LessThan = 10,
        LessThanOrEqual = 11,
        GreaterThan = 12,
        GreaterThanOrEqual = 13,
        BitwiseAnd = 14,
        BitwiseOr = 15,
        BitwiseXor = 16,
        ShiftLeft = 17,
        ShiftRight = 18
    }

    public enum LuaIrCallKind
    {
        Regular = 0,
        ForIterator = 1
    }

    public readonly struct LuaIrConstant : System.IEquatable<Lunil.IR.Canonical.LuaIrConstant>
    {
        public Lunil.IR.Canonical.LuaIrConstantKind Kind { get => throw null; }
        public long Integer { get => throw null; }
        public double Float { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<byte> Bytes { get => throw null; }
        public bool Boolean { get => throw null; }
        public static Lunil.IR.Canonical.LuaIrConstant Nil { get => throw null; }
        public static Lunil.IR.Canonical.LuaIrConstant FromBoolean(bool value) => throw null;
        public static Lunil.IR.Canonical.LuaIrConstant FromInteger(long value) => throw null;
        public static Lunil.IR.Canonical.LuaIrConstant FromFloat(double value) => throw null;
        public static Lunil.IR.Canonical.LuaIrConstant FromString(System.ReadOnlySpan<byte> value) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Canonical.LuaIrConstant left, Lunil.IR.Canonical.LuaIrConstant right) => throw null;
        public static bool operator ==(Lunil.IR.Canonical.LuaIrConstant left, Lunil.IR.Canonical.LuaIrConstant right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Canonical.LuaIrConstant other) => throw null;
    }

    public enum LuaIrConstantKind
    {
        Nil = 0,
        Boolean = 1,
        Integer = 2,
        Float = 3,
        String = 4
    }

    public static class LuaIrControlFlow
    {
        public static System.Collections.Immutable.ImmutableArray<Lunil.IR.Canonical.LuaIrBasicBlock> Build(System.Collections.Immutable.ImmutableArray<Lunil.IR.Canonical.LuaIrInstruction> instructions) => throw null;
    }

    public sealed class LuaIrFunction : System.IEquatable<Lunil.IR.Canonical.LuaIrFunction>
    {
        public required int Id { get => throw null; init { } }
        public int ParentFunctionId { get => throw null; init { } }
        public required Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<byte> SourceName { get => throw null; init { } }
        public int LineDefined { get => throw null; init { } }
        public int LastLineDefined { get => throw null; init { } }
        public int ParameterCount { get => throw null; init { } }
        public bool IsVarArg { get => throw null; init { } }
        public int RegisterCount { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Canonical.LuaIrConstant> Constants { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Canonical.LuaIrUpvalue> Upvalues { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Canonical.LuaIrInstruction> Instructions { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Canonical.LuaIrLocalVariable> LocalVariables { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Canonical.LuaIrBasicBlock> BasicBlocks { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Canonical.LuaIrFunction? left, Lunil.IR.Canonical.LuaIrFunction? right) => throw null;
        public static bool operator ==(Lunil.IR.Canonical.LuaIrFunction? left, Lunil.IR.Canonical.LuaIrFunction? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Canonical.LuaIrFunction? other) => throw null;
    }

    public readonly struct LuaIrInstruction : System.IEquatable<Lunil.IR.Canonical.LuaIrInstruction>
    {
        public Lunil.IR.Canonical.LuaIrOpcode Opcode { get => throw null; init { } }
        public int A { get => throw null; init { } }
        public int B { get => throw null; init { } }
        public int C { get => throw null; init { } }
        public int D { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public int SourceLine { get => throw null; init { } }
        public int LogicalProgramCounter { get => throw null; init { } }
        public Lunil.IR.Canonical.LuaIrInstructionEffects Effects { get => throw null; }
        public LuaIrInstruction(Lunil.IR.Canonical.LuaIrOpcode opcode, int a = 0, int b = 0, int c = 0, int d = 0, Lunil.Core.Text.TextSpan span = null, int sourceLine = 0, int logicalProgramCounter = -1) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Canonical.LuaIrInstruction left, Lunil.IR.Canonical.LuaIrInstruction right) => throw null;
        public static bool operator ==(Lunil.IR.Canonical.LuaIrInstruction left, Lunil.IR.Canonical.LuaIrInstruction right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Canonical.LuaIrInstruction other) => throw null;
    }

    [System.Flags]
    public enum LuaIrInstructionEffects
    {
        None = 0,
        MayAllocate = 1,
        MayCall = 2,
        MayYield = 4,
        MayThrow = 8,
        IsGcSafePoint = 16
    }

    public static class LuaIrInstructionFacts
    {
        public static Lunil.IR.Canonical.LuaIrInstructionEffects GetEffects(Lunil.IR.Canonical.LuaIrInstruction instruction) => throw null;
        public static Lunil.IR.Canonical.LuaIrInstructionEffects GetEffects(Lunil.IR.Canonical.LuaIrOpcode opcode) => throw null;
    }

    public sealed class LuaIrLocalVariable : System.IEquatable<Lunil.IR.Canonical.LuaIrLocalVariable>
    {
        public System.Collections.Immutable.ImmutableArray<byte> Name { get => throw null; init { } }
        public int StartProgramCounter { get => throw null; init { } }
        public int EndProgramCounter { get => throw null; init { } }
        public LuaIrLocalVariable(System.Collections.Immutable.ImmutableArray<byte> Name, int StartProgramCounter, int EndProgramCounter) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Canonical.LuaIrLocalVariable? left, Lunil.IR.Canonical.LuaIrLocalVariable? right) => throw null;
        public static bool operator ==(Lunil.IR.Canonical.LuaIrLocalVariable? left, Lunil.IR.Canonical.LuaIrLocalVariable? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Canonical.LuaIrLocalVariable? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<byte> Name, out int StartProgramCounter, out int EndProgramCounter) => throw null;
    }

    public sealed class LuaIrModule : System.IEquatable<Lunil.IR.Canonical.LuaIrModule>
    {
        public const int CurrentFormatVersion = 3;
        public int FormatVersion { get => throw null; init { } }
        public int MainFunctionId { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Canonical.LuaIrFunction> Functions { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Canonical.LuaIrModule? left, Lunil.IR.Canonical.LuaIrModule? right) => throw null;
        public static bool operator ==(Lunil.IR.Canonical.LuaIrModule? left, Lunil.IR.Canonical.LuaIrModule? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Canonical.LuaIrModule? other) => throw null;
    }

    public enum LuaIrOpcode
    {
        LoadConstant = 0,
        LoadNil = 1,
        Move = 2,
        SetTop = 3,
        GetUpvalue = 4,
        SetUpvalue = 5,
        NewTable = 6,
        GetTable = 7,
        SetTable = 8,
        SetList = 9,
        Closure = 10,
        VarArg = 11,
        Unary = 12,
        Binary = 13,
        Jump = 14,
        JumpIfFalse = 15,
        JumpIfTrue = 16,
        Call = 17,
        TailCall = 18,
        Return = 19,
        Close = 20,
        MarkToBeClosed = 21,
        NumericForPrepare = 22,
        NumericForLoop = 23
    }

    public enum LuaIrUnaryOperator
    {
        Negate = 0,
        BitwiseNot = 1,
        LogicalNot = 2,
        Length = 3
    }

    public sealed class LuaIrUpvalue : System.IEquatable<Lunil.IR.Canonical.LuaIrUpvalue>
    {
        public string Name { get => throw null; init { } }
        public int SymbolId { get => throw null; init { } }
        public Lunil.IR.Canonical.LuaIrUpvalueSourceKind SourceKind { get => throw null; init { } }
        public int SourceIndex { get => throw null; init { } }
        public byte Kind { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<byte> DebugName { get => throw null; init { } }
        public LuaIrUpvalue(string Name, int SymbolId, Lunil.IR.Canonical.LuaIrUpvalueSourceKind SourceKind, int SourceIndex) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Canonical.LuaIrUpvalue? left, Lunil.IR.Canonical.LuaIrUpvalue? right) => throw null;
        public static bool operator ==(Lunil.IR.Canonical.LuaIrUpvalue? left, Lunil.IR.Canonical.LuaIrUpvalue? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Canonical.LuaIrUpvalue? other) => throw null;
        public void Deconstruct(out string Name, out int SymbolId, out Lunil.IR.Canonical.LuaIrUpvalueSourceKind SourceKind, out int SourceIndex) => throw null;
    }

    public enum LuaIrUpvalueSourceKind
    {
        Register = 0,
        Upvalue = 1,
        Environment = 2
    }

    public sealed class LuaIrVerificationError : System.IEquatable<Lunil.IR.Canonical.LuaIrVerificationError>
    {
        public int FunctionId { get => throw null; init { } }
        public int ProgramCounter { get => throw null; init { } }
        public string Message { get => throw null; init { } }
        public LuaIrVerificationError(int FunctionId, int ProgramCounter, string Message) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Canonical.LuaIrVerificationError? left, Lunil.IR.Canonical.LuaIrVerificationError? right) => throw null;
        public static bool operator ==(Lunil.IR.Canonical.LuaIrVerificationError? left, Lunil.IR.Canonical.LuaIrVerificationError? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Canonical.LuaIrVerificationError? other) => throw null;
        public void Deconstruct(out int FunctionId, out int ProgramCounter, out string Message) => throw null;
    }

    public static class LuaIrVerifier
    {
        public static System.Collections.Immutable.ImmutableArray<Lunil.IR.Canonical.LuaIrVerificationError> Verify(Lunil.IR.Canonical.LuaIrModule module, Lunil.IR.Canonical.LuaIrVerifierOptions? options = null) => throw null;
    }

    public sealed class LuaIrVerifierOptions : System.IEquatable<Lunil.IR.Canonical.LuaIrVerifierOptions>
    {
        public static Lunil.IR.Canonical.LuaIrVerifierOptions Default { get => throw null; }
        public int MaximumFunctions { get => throw null; init { } }
        public int MaximumInstructionsPerFunction { get => throw null; init { } }
        public int MaximumRegistersPerFunction { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Canonical.LuaIrVerifierOptions? left, Lunil.IR.Canonical.LuaIrVerifierOptions? right) => throw null;
        public static bool operator ==(Lunil.IR.Canonical.LuaIrVerifierOptions? left, Lunil.IR.Canonical.LuaIrVerifierOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Canonical.LuaIrVerifierOptions? other) => throw null;
    }
}
namespace Lunil.IR.Lua54
{
    public readonly struct Lua54AbsoluteLineInfo : System.IEquatable<Lunil.IR.Lua54.Lua54AbsoluteLineInfo>
    {
        public int ProgramCounter { get => throw null; init { } }
        public int Line { get => throw null; init { } }
        public Lua54AbsoluteLineInfo(int ProgramCounter, int Line) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua54.Lua54AbsoluteLineInfo left, Lunil.IR.Lua54.Lua54AbsoluteLineInfo right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54AbsoluteLineInfo left, Lunil.IR.Lua54.Lua54AbsoluteLineInfo right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua54.Lua54AbsoluteLineInfo other) => throw null;
        public void Deconstruct(out int ProgramCounter, out int Line) => throw null;
    }

    public enum Lua54ByteOrder
    {
        LittleEndian = 0,
        BigEndian = 1
    }

    public static class Lua54CanonicalPrototypeWriter
    {
        public static Lunil.IR.Lua54.Lua54Chunk CreateChunk(Lunil.IR.Canonical.LuaIrModule module, int functionId, Lunil.IR.Lua54.Lua54ChunkTarget? target = null) => throw null;
        public static byte[] Write(Lunil.IR.Canonical.LuaIrModule module, int functionId, bool stripDebugInformation = false, Lunil.IR.Lua54.Lua54ChunkTarget? target = null) => throw null;
    }

    public sealed class Lua54Chunk : System.IEquatable<Lunil.IR.Lua54.Lua54Chunk>
    {
        public Lunil.IR.Lua54.Lua54ChunkTarget Target { get => throw null; init { } }
        public byte MainUpvalueCount { get => throw null; init { } }
        public Lunil.IR.Lua54.Lua54Prototype MainPrototype { get => throw null; init { } }
        public Lua54Chunk(Lunil.IR.Lua54.Lua54ChunkTarget Target, byte MainUpvalueCount, Lunil.IR.Lua54.Lua54Prototype MainPrototype) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua54.Lua54Chunk? left, Lunil.IR.Lua54.Lua54Chunk? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54Chunk? left, Lunil.IR.Lua54.Lua54Chunk? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua54.Lua54Chunk? other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua54.Lua54ChunkTarget Target, out byte MainUpvalueCount, out Lunil.IR.Lua54.Lua54Prototype MainPrototype) => throw null;
    }

    public sealed class Lua54ChunkFormatException : System.FormatException
    {
        public string Reason { get => throw null; }
        public int ByteOffset { get => throw null; }
        public Lua54ChunkFormatException(string reason, int byteOffset) { }
    }

    public static class Lua54ChunkReader
    {
        public static Lunil.IR.Lua54.Lua54Chunk Read(System.ReadOnlySpan<byte> data, Lunil.IR.Lua54.Lua54ChunkReaderOptions? options = null) => throw null;
    }

    public sealed class Lua54ChunkReaderOptions : System.IEquatable<Lunil.IR.Lua54.Lua54ChunkReaderOptions>
    {
        public static Lunil.IR.Lua54.Lua54ChunkReaderOptions Default { get => throw null; }
        public int MaximumChunkBytes { get => throw null; init { } }
        public int MaximumPrototypeDepth { get => throw null; init { } }
        public int MaximumPrototypeCount { get => throw null; init { } }
        public int MaximumInstructionCount { get => throw null; init { } }
        public int MaximumConstantCount { get => throw null; init { } }
        public int MaximumUpvalueCount { get => throw null; init { } }
        public int MaximumStringBytes { get => throw null; init { } }
        public int MaximumDebugEntryCount { get => throw null; init { } }
        public bool AllowTrailingData { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua54.Lua54ChunkReaderOptions? left, Lunil.IR.Lua54.Lua54ChunkReaderOptions? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54ChunkReaderOptions? left, Lunil.IR.Lua54.Lua54ChunkReaderOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua54.Lua54ChunkReaderOptions? other) => throw null;
    }

    public readonly struct Lua54ChunkTarget : System.IEquatable<Lunil.IR.Lua54.Lua54ChunkTarget>
    {
        public Lunil.IR.Lua54.Lua54ByteOrder ByteOrder { get => throw null; }
        public byte InstructionSize { get => throw null; }
        public byte IntegerSize { get => throw null; }
        public byte NumberSize { get => throw null; }
        public static Lunil.IR.Lua54.Lua54ChunkTarget Host { get => throw null; }
        public Lua54ChunkTarget(Lunil.IR.Lua54.Lua54ByteOrder byteOrder, byte instructionSize, byte integerSize, byte numberSize) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua54.Lua54ChunkTarget left, Lunil.IR.Lua54.Lua54ChunkTarget right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54ChunkTarget left, Lunil.IR.Lua54.Lua54ChunkTarget right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua54.Lua54ChunkTarget other) => throw null;
    }

    public static class Lua54ChunkVerifier
    {
        public static System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua54.Lua54VerificationError> Verify(Lunil.IR.Lua54.Lua54Chunk chunk) => throw null;
        public static void ThrowIfInvalid(Lunil.IR.Lua54.Lua54Chunk chunk) { }
    }

    public static class Lua54ChunkWriter
    {
        public static byte[] Write(Lunil.IR.Lua54.Lua54Chunk chunk, bool stripDebugInformation = false) => throw null;
    }

    public readonly struct Lua54Constant : System.IEquatable<Lunil.IR.Lua54.Lua54Constant>
    {
        public Lunil.IR.Lua54.Lua54ConstantKind Kind { get => throw null; }
        public long IntegerValue { get => throw null; }
        public double FloatValue { get => throw null; }
        public Lunil.IR.Lua54.Lua54String? StringValue { get => throw null; }
        public static Lunil.IR.Lua54.Lua54Constant Nil { get => throw null; }
        public static Lunil.IR.Lua54.Lua54Constant False { get => throw null; }
        public static Lunil.IR.Lua54.Lua54Constant True { get => throw null; }
        public bool GetBooleanValue() => throw null;
        public static Lunil.IR.Lua54.Lua54Constant FromInteger(long value) => throw null;
        public static Lunil.IR.Lua54.Lua54Constant FromFloat(double value) => throw null;
        public static Lunil.IR.Lua54.Lua54Constant FromString(Lunil.IR.Lua54.Lua54String value, bool isShort) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua54.Lua54Constant left, Lunil.IR.Lua54.Lua54Constant right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54Constant left, Lunil.IR.Lua54.Lua54Constant right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua54.Lua54Constant other) => throw null;
    }

    public enum Lua54ConstantKind
    {
        Nil = 0,
        False = 1,
        True = 2,
        Float = 3,
        Integer = 4,
        ShortString = 5,
        LongString = 6
    }

    public readonly struct Lua54Instruction : System.IEquatable<Lunil.IR.Lua54.Lua54Instruction>
    {
        public const int MaximumA = 255;
        public const int MaximumB = 255;
        public const int MaximumC = 255;
        public const int MaximumBx = 131071;
        public const int MaximumAx = 33554431;
        public const int SignedBxOffset = 65535;
        public const int SignedJumpOffset = 16777215;
        public const int SignedCOffset = 127;
        public uint RawValue { get => throw null; init { } }
        public Lunil.IR.Lua54.Lua54Opcode Opcode { get => throw null; }
        public int A { get => throw null; }
        public bool K { get => throw null; }
        public int B { get => throw null; }
        public int C { get => throw null; }
        public int SignedB { get => throw null; }
        public int SignedC { get => throw null; }
        public int Bx { get => throw null; }
        public int SignedBx { get => throw null; }
        public int Ax { get => throw null; }
        public int SignedJump { get => throw null; }
        public Lunil.IR.Lua54.Lua54InstructionMode Mode { get => throw null; }
        public Lua54Instruction(uint RawValue) { }
        public static Lunil.IR.Lua54.Lua54Instruction CreateAbc(Lunil.IR.Lua54.Lua54Opcode opcode, int a, int b, int c, bool k = false) => throw null;
        public static Lunil.IR.Lua54.Lua54Instruction CreateABx(Lunil.IR.Lua54.Lua54Opcode opcode, int a, int bx) => throw null;
        public static Lunil.IR.Lua54.Lua54Instruction CreateASignedBx(Lunil.IR.Lua54.Lua54Opcode opcode, int a, int signedBx) => throw null;
        public static Lunil.IR.Lua54.Lua54Instruction CreateAx(Lunil.IR.Lua54.Lua54Opcode opcode, int ax) => throw null;
        public static Lunil.IR.Lua54.Lua54Instruction CreateSignedJump(Lunil.IR.Lua54.Lua54Opcode opcode, int signedJump) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua54.Lua54Instruction left, Lunil.IR.Lua54.Lua54Instruction right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54Instruction left, Lunil.IR.Lua54.Lua54Instruction right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua54.Lua54Instruction other) => throw null;
        public void Deconstruct(out uint RawValue) => throw null;
    }

    public enum Lua54InstructionMode
    {
        Abc = 0,
        ABx = 1,
        ASignedBx = 2,
        Ax = 3,
        SignedJump = 4
    }

    public readonly struct Lua54LocalVariable : System.IEquatable<Lunil.IR.Lua54.Lua54LocalVariable>
    {
        public Lunil.IR.Lua54.Lua54String? Name { get => throw null; init { } }
        public int StartProgramCounter { get => throw null; init { } }
        public int EndProgramCounter { get => throw null; init { } }
        public Lua54LocalVariable(Lunil.IR.Lua54.Lua54String? Name, int StartProgramCounter, int EndProgramCounter) { }
        public override string? ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua54.Lua54LocalVariable left, Lunil.IR.Lua54.Lua54LocalVariable right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54LocalVariable left, Lunil.IR.Lua54.Lua54LocalVariable right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua54.Lua54LocalVariable other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua54.Lua54String? Name, out int StartProgramCounter, out int EndProgramCounter) => throw null;
    }

    public enum Lua54Opcode
    {
        Move = 0,
        LoadInteger = 1,
        LoadFloat = 2,
        LoadConstant = 3,
        LoadConstantExtra = 4,
        LoadFalse = 5,
        LoadFalseAndSkip = 6,
        LoadTrue = 7,
        LoadNil = 8,
        GetUpvalue = 9,
        SetUpvalue = 10,
        GetTableUpvalue = 11,
        GetTable = 12,
        GetInteger = 13,
        GetField = 14,
        SetTableUpvalue = 15,
        SetTable = 16,
        SetInteger = 17,
        SetField = 18,
        NewTable = 19,
        Self = 20,
        AddImmediate = 21,
        AddConstant = 22,
        SubtractConstant = 23,
        MultiplyConstant = 24,
        ModuloConstant = 25,
        PowerConstant = 26,
        DivideConstant = 27,
        FloorDivideConstant = 28,
        BitwiseAndConstant = 29,
        BitwiseOrConstant = 30,
        BitwiseXorConstant = 31,
        ShiftRightImmediate = 32,
        ShiftLeftImmediate = 33,
        Add = 34,
        Subtract = 35,
        Multiply = 36,
        Modulo = 37,
        Power = 38,
        Divide = 39,
        FloorDivide = 40,
        BitwiseAnd = 41,
        BitwiseOr = 42,
        BitwiseXor = 43,
        ShiftLeft = 44,
        ShiftRight = 45,
        MetamethodBinary = 46,
        MetamethodBinaryImmediate = 47,
        MetamethodBinaryConstant = 48,
        UnaryMinus = 49,
        BitwiseNot = 50,
        LogicalNot = 51,
        Length = 52,
        Concatenate = 53,
        Close = 54,
        ToBeClosed = 55,
        Jump = 56,
        Equal = 57,
        LessThan = 58,
        LessOrEqual = 59,
        EqualConstant = 60,
        EqualImmediate = 61,
        LessThanImmediate = 62,
        LessOrEqualImmediate = 63,
        GreaterThanImmediate = 64,
        GreaterOrEqualImmediate = 65,
        Test = 66,
        TestSet = 67,
        Call = 68,
        TailCall = 69,
        Return = 70,
        ReturnZero = 71,
        ReturnOne = 72,
        NumericForLoop = 73,
        NumericForPrepare = 74,
        GenericForPrepare = 75,
        GenericForCall = 76,
        GenericForLoop = 77,
        SetList = 78,
        Closure = 79,
        VarArg = 80,
        VarArgPrepare = 81,
        ExtraArgument = 82
    }

    public readonly struct Lua54OpcodeInfo : System.IEquatable<Lunil.IR.Lua54.Lua54OpcodeInfo>
    {
        public Lunil.IR.Lua54.Lua54InstructionMode Mode { get => throw null; init { } }
        public bool SetsRegisterA { get => throw null; init { } }
        public bool IsTest { get => throw null; init { } }
        public bool UsesTop { get => throw null; init { } }
        public bool SetsTop { get => throw null; init { } }
        public bool IsMetamethod { get => throw null; init { } }
        public static int OpcodeCount { get => throw null; }
        public Lua54OpcodeInfo(Lunil.IR.Lua54.Lua54InstructionMode Mode, bool SetsRegisterA = false, bool IsTest = false, bool UsesTop = false, bool SetsTop = false, bool IsMetamethod = false) { }
        public static bool IsDefined(Lunil.IR.Lua54.Lua54Opcode opcode) => throw null;
        public static Lunil.IR.Lua54.Lua54OpcodeInfo Get(Lunil.IR.Lua54.Lua54Opcode opcode) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua54.Lua54OpcodeInfo left, Lunil.IR.Lua54.Lua54OpcodeInfo right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54OpcodeInfo left, Lunil.IR.Lua54.Lua54OpcodeInfo right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua54.Lua54OpcodeInfo other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua54.Lua54InstructionMode Mode, out bool SetsRegisterA, out bool IsTest, out bool UsesTop, out bool SetsTop, out bool IsMetamethod) => throw null;
    }

    public sealed class Lua54Prototype : System.IEquatable<Lunil.IR.Lua54.Lua54Prototype>
    {
        public Lunil.IR.Lua54.Lua54String? Source { get => throw null; init { } }
        public int LineDefined { get => throw null; init { } }
        public int LastLineDefined { get => throw null; init { } }
        public byte ParameterCount { get => throw null; init { } }
        public byte VarArgFlags { get => throw null; init { } }
        public byte MaximumStackSize { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua54.Lua54Instruction> Code { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua54.Lua54Constant> Constants { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua54.Lua54UpvalueDescriptor> Upvalues { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua54.Lua54Prototype> NestedPrototypes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<sbyte> LineInfo { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua54.Lua54AbsoluteLineInfo> AbsoluteLineInfo { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua54.Lua54LocalVariable> LocalVariables { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua54.Lua54String?> UpvalueNames { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua54.Lua54Prototype? left, Lunil.IR.Lua54.Lua54Prototype? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54Prototype? left, Lunil.IR.Lua54.Lua54Prototype? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua54.Lua54Prototype? other) => throw null;
    }

    public static class Lua54PrototypeConverter
    {
        public static Lunil.IR.Canonical.LuaIrModule Convert(System.ReadOnlySpan<byte> binaryChunk, Lunil.IR.Lua54.Lua54ChunkReaderOptions? options = null) => throw null;
        public static Lunil.IR.Canonical.LuaIrModule Convert(Lunil.IR.Lua54.Lua54Chunk chunk) => throw null;
    }

    public sealed class Lua54String : System.IEquatable<Lunil.IR.Lua54.Lua54String>
    {
        public int Length { get => throw null; }
        public Lua54String(System.ReadOnlySpan<byte> bytes) { }
        public System.ReadOnlySpan<byte> AsSpan() => throw null;
        public byte[] ToArray() => throw null;
        public static Lunil.IR.Lua54.Lua54String FromUtf8(string value) => throw null;
        public bool Equals(Lunil.IR.Lua54.Lua54String? other) => throw null;
        public override bool Equals(object? obj) => throw null;
        public override int GetHashCode() => throw null;
        public override string ToString() => throw null;
    }

    public readonly struct Lua54UpvalueDescriptor : System.IEquatable<Lunil.IR.Lua54.Lua54UpvalueDescriptor>
    {
        public byte InStack { get => throw null; init { } }
        public byte Index { get => throw null; init { } }
        public byte Kind { get => throw null; init { } }
        public Lua54UpvalueDescriptor(byte InStack, byte Index, byte Kind) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua54.Lua54UpvalueDescriptor left, Lunil.IR.Lua54.Lua54UpvalueDescriptor right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54UpvalueDescriptor left, Lunil.IR.Lua54.Lua54UpvalueDescriptor right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua54.Lua54UpvalueDescriptor other) => throw null;
        public void Deconstruct(out byte InStack, out byte Index, out byte Kind) => throw null;
    }

    public sealed class Lua54VerificationError : System.IEquatable<Lunil.IR.Lua54.Lua54VerificationError>
    {
        public string PrototypePath { get => throw null; init { } }
        public string Message { get => throw null; init { } }
        public int? ProgramCounter { get => throw null; init { } }
        public Lua54VerificationError(string PrototypePath, string Message, int? ProgramCounter = null) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua54.Lua54VerificationError? left, Lunil.IR.Lua54.Lua54VerificationError? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua54.Lua54VerificationError? left, Lunil.IR.Lua54.Lua54VerificationError? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua54.Lua54VerificationError? other) => throw null;
        public void Deconstruct(out string PrototypePath, out string Message, out int? ProgramCounter) => throw null;
    }
}
