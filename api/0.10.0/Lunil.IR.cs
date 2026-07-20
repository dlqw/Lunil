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
        public const int CurrentFormatVersion = 4;
        public int FormatVersion { get => throw null; init { } }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
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
namespace Lunil.IR.Lua51
{
    public enum Lua51ByteOrder
    {
        LittleEndian = 0,
        BigEndian = 1
    }

    public static class Lua51CanonicalPrototypeWriter
    {
        public static byte[] Write(Lunil.IR.Canonical.LuaIrModule module, int functionId, bool stripDebug = false) => throw null;
        public static Lunil.IR.Lua51.Lua51Chunk CreateChunk(Lunil.IR.Canonical.LuaIrModule module, int functionId, bool stripDebug = false) => throw null;
    }

    public sealed class Lua51Chunk : System.IEquatable<Lunil.IR.Lua51.Lua51Chunk>
    {
        public Lunil.IR.Lua51.Lua51ChunkTarget Target { get => throw null; init { } }
        public Lunil.IR.Lua51.Lua51Prototype MainPrototype { get => throw null; init { } }
        public Lua51Chunk(Lunil.IR.Lua51.Lua51ChunkTarget Target, Lunil.IR.Lua51.Lua51Prototype MainPrototype) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua51.Lua51Chunk? left, Lunil.IR.Lua51.Lua51Chunk? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua51.Lua51Chunk? left, Lunil.IR.Lua51.Lua51Chunk? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua51.Lua51Chunk? other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua51.Lua51ChunkTarget Target, out Lunil.IR.Lua51.Lua51Prototype MainPrototype) => throw null;
    }

    public sealed class Lua51ChunkFormatException : System.FormatException
    {
        public string Reason { get => throw null; }
        public int Offset { get => throw null; }
        public Lua51ChunkFormatException(string reason, int offset = 0) { }
    }

    public static class Lua51ChunkReader
    {
        public static Lunil.IR.Lua51.Lua51Chunk Read(System.ReadOnlySpan<byte> data, Lunil.IR.Lua51.Lua51ChunkReaderOptions? options = null) => throw null;
    }

    public sealed class Lua51ChunkReaderOptions : System.IEquatable<Lunil.IR.Lua51.Lua51ChunkReaderOptions>
    {
        public static Lunil.IR.Lua51.Lua51ChunkReaderOptions Default { get => throw null; }
        public int MaximumChunkBytes { get => throw null; init { } }
        public int MaximumPrototypeDepth { get => throw null; init { } }
        public int MaximumPrototypeCount { get => throw null; init { } }
        public int MaximumInstructionCount { get => throw null; init { } }
        public int MaximumConstantCount { get => throw null; init { } }
        public int MaximumStringBytes { get => throw null; init { } }
        public int MaximumDebugEntryCount { get => throw null; init { } }
        public bool AllowTrailingData { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua51.Lua51ChunkReaderOptions? left, Lunil.IR.Lua51.Lua51ChunkReaderOptions? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua51.Lua51ChunkReaderOptions? left, Lunil.IR.Lua51.Lua51ChunkReaderOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua51.Lua51ChunkReaderOptions? other) => throw null;
    }

    public readonly struct Lua51ChunkTarget : System.IEquatable<Lunil.IR.Lua51.Lua51ChunkTarget>
    {
        public Lunil.IR.Lua51.Lua51ByteOrder ByteOrder { get => throw null; init { } }
        public int SizeOfInt { get => throw null; init { } }
        public int SizeOfSizeT { get => throw null; init { } }
        public int InstructionSize { get => throw null; init { } }
        public int NumberSize { get => throw null; init { } }
        public static Lunil.IR.Lua51.Lua51ChunkTarget Host { get => throw null; }
        public Lua51ChunkTarget(Lunil.IR.Lua51.Lua51ByteOrder ByteOrder, int SizeOfInt, int SizeOfSizeT, int InstructionSize, int NumberSize) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua51.Lua51ChunkTarget left, Lunil.IR.Lua51.Lua51ChunkTarget right) => throw null;
        public static bool operator ==(Lunil.IR.Lua51.Lua51ChunkTarget left, Lunil.IR.Lua51.Lua51ChunkTarget right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua51.Lua51ChunkTarget other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua51.Lua51ByteOrder ByteOrder, out int SizeOfInt, out int SizeOfSizeT, out int InstructionSize, out int NumberSize) => throw null;
    }

    public sealed class Lua51Constant : System.IEquatable<Lunil.IR.Lua51.Lua51Constant>
    {
        public Lunil.IR.Lua51.Lua51ConstantKind Kind { get => throw null; init { } }
        public double NumberValue { get => throw null; init { } }
        public Lunil.IR.Lua51.Lua51String? StringValue { get => throw null; init { } }
        public static Lunil.IR.Lua51.Lua51Constant Nil { get => throw null; }
        public static Lunil.IR.Lua51.Lua51Constant False { get => throw null; }
        public static Lunil.IR.Lua51.Lua51Constant True { get => throw null; }
        public static Lunil.IR.Lua51.Lua51Constant FromBoolean(bool value) => throw null;
        public static Lunil.IR.Lua51.Lua51Constant FromNumber(double value) => throw null;
        public static Lunil.IR.Lua51.Lua51Constant FromString(Lunil.IR.Lua51.Lua51String value) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua51.Lua51Constant? left, Lunil.IR.Lua51.Lua51Constant? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua51.Lua51Constant? left, Lunil.IR.Lua51.Lua51Constant? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua51.Lua51Constant? other) => throw null;
    }

    public enum Lua51ConstantKind
    {
        Nil = 0,
        False = 1,
        True = 2,
        Number = 3,
        String = 4
    }

    public readonly struct Lua51Instruction : System.IEquatable<Lunil.IR.Lua51.Lua51Instruction>
    {
        public const int MaximumA = 255;
        public const int MaximumB = 511;
        public const int MaximumC = 511;
        public const int MaximumBx = 262143;
        public const int SignedBxOffset = 131071;
        public uint RawValue { get => throw null; init { } }
        public Lunil.IR.Lua51.Lua51Opcode Opcode { get => throw null; }
        public int A { get => throw null; }
        public int C { get => throw null; }
        public int B { get => throw null; }
        public int Bx { get => throw null; }
        public int SignedBx { get => throw null; }
        public bool IsConstantB { get => throw null; }
        public bool IsConstantC { get => throw null; }
        public Lua51Instruction(uint RawValue) { }
        public static Lunil.IR.Lua51.Lua51Instruction CreateAbc(Lunil.IR.Lua51.Lua51Opcode opcode, int a, int b, int c) => throw null;
        public static Lunil.IR.Lua51.Lua51Instruction CreateABx(Lunil.IR.Lua51.Lua51Opcode opcode, int a, int bx) => throw null;
        public static Lunil.IR.Lua51.Lua51Instruction CreateASignedBx(Lunil.IR.Lua51.Lua51Opcode opcode, int a, int sbx) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua51.Lua51Instruction left, Lunil.IR.Lua51.Lua51Instruction right) => throw null;
        public static bool operator ==(Lunil.IR.Lua51.Lua51Instruction left, Lunil.IR.Lua51.Lua51Instruction right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua51.Lua51Instruction other) => throw null;
        public void Deconstruct(out uint RawValue) => throw null;
    }

    public sealed class Lua51LocalVariable : System.IEquatable<Lunil.IR.Lua51.Lua51LocalVariable>
    {
        public Lunil.IR.Lua51.Lua51String? Name { get => throw null; init { } }
        public int StartProgramCounter { get => throw null; init { } }
        public int EndProgramCounter { get => throw null; init { } }
        public Lua51LocalVariable(Lunil.IR.Lua51.Lua51String? Name, int StartProgramCounter, int EndProgramCounter) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua51.Lua51LocalVariable? left, Lunil.IR.Lua51.Lua51LocalVariable? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua51.Lua51LocalVariable? left, Lunil.IR.Lua51.Lua51LocalVariable? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua51.Lua51LocalVariable? other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua51.Lua51String? Name, out int StartProgramCounter, out int EndProgramCounter) => throw null;
    }

    public enum Lua51Opcode
    {
        Move = 0,
        LoadConstant = 1,
        LoadBoolean = 2,
        LoadNil = 3,
        GetUpvalue = 4,
        GetGlobal = 5,
        GetTable = 6,
        SetGlobal = 7,
        SetUpvalue = 8,
        SetTable = 9,
        NewTable = 10,
        Self = 11,
        Add = 12,
        Subtract = 13,
        Multiply = 14,
        Divide = 15,
        Modulo = 16,
        Power = 17,
        UnaryMinus = 18,
        LogicalNot = 19,
        Length = 20,
        Concatenate = 21,
        Jump = 22,
        Equal = 23,
        LessThan = 24,
        LessOrEqual = 25,
        Test = 26,
        TestSet = 27,
        Call = 28,
        TailCall = 29,
        Return = 30,
        NumericForLoop = 31,
        NumericForPrepare = 32,
        GenericForLoop = 33,
        SetList = 34,
        Close = 35,
        Closure = 36,
        VarArg = 37
    }

    public sealed class Lua51Prototype : System.IEquatable<Lunil.IR.Lua51.Lua51Prototype>
    {
        public Lunil.IR.Lua51.Lua51String? Source { get => throw null; init { } }
        public int LineDefined { get => throw null; init { } }
        public int LastLineDefined { get => throw null; init { } }
        public byte UpvalueCount { get => throw null; init { } }
        public byte ParameterCount { get => throw null; init { } }
        public byte VarArgFlags { get => throw null; init { } }
        public byte MaximumStackSize { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua51.Lua51Instruction> Code { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua51.Lua51Constant> Constants { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua51.Lua51Prototype> NestedPrototypes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> LineInfo { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua51.Lua51LocalVariable> LocalVariables { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua51.Lua51String?> UpvalueNames { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua51.Lua51Prototype? left, Lunil.IR.Lua51.Lua51Prototype? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua51.Lua51Prototype? left, Lunil.IR.Lua51.Lua51Prototype? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua51.Lua51Prototype? other) => throw null;
    }

    public static class Lua51PrototypeConverter
    {
        public static Lunil.IR.Canonical.LuaIrModule Convert(System.ReadOnlySpan<byte> bytes, Lunil.IR.Lua51.Lua51ChunkReaderOptions? options = null) => throw null;
        public static Lunil.IR.Canonical.LuaIrModule Convert(Lunil.IR.Lua51.Lua51Chunk chunk) => throw null;
    }

    public readonly struct Lua51String : System.IEquatable<Lunil.IR.Lua51.Lua51String>
    {
        public byte[] Bytes { get => throw null; init { } }
        public int Length { get => throw null; }
        public Lua51String(byte[] Bytes) { }
        public byte[] ToArray() => throw null;
        public System.ReadOnlySpan<byte> AsSpan() => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua51.Lua51String left, Lunil.IR.Lua51.Lua51String right) => throw null;
        public static bool operator ==(Lunil.IR.Lua51.Lua51String left, Lunil.IR.Lua51.Lua51String right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object obj) => throw null;
        public bool Equals(Lunil.IR.Lua51.Lua51String other) => throw null;
        public void Deconstruct(out byte[] Bytes) => throw null;
    }
}
namespace Lunil.IR.Lua52
{
    public enum Lua52ByteOrder
    {
        LittleEndian = 0,
        BigEndian = 1
    }

    public static class Lua52CanonicalPrototypeWriter
    {
        public static byte[] Write(Lunil.IR.Canonical.LuaIrModule module, int functionId, bool stripDebug = false) => throw null;
        public static Lunil.IR.Lua52.Lua52Chunk CreateChunk(Lunil.IR.Canonical.LuaIrModule module, int functionId, bool stripDebug = false) => throw null;
    }

    public sealed class Lua52Chunk : System.IEquatable<Lunil.IR.Lua52.Lua52Chunk>
    {
        public Lunil.IR.Lua52.Lua52ChunkTarget Target { get => throw null; init { } }
        public Lunil.IR.Lua52.Lua52Prototype MainPrototype { get => throw null; init { } }
        public Lua52Chunk(Lunil.IR.Lua52.Lua52ChunkTarget Target, Lunil.IR.Lua52.Lua52Prototype MainPrototype) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua52.Lua52Chunk? left, Lunil.IR.Lua52.Lua52Chunk? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua52.Lua52Chunk? left, Lunil.IR.Lua52.Lua52Chunk? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua52.Lua52Chunk? other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua52.Lua52ChunkTarget Target, out Lunil.IR.Lua52.Lua52Prototype MainPrototype) => throw null;
    }

    public sealed class Lua52ChunkFormatException : System.FormatException
    {
        public string Reason { get => throw null; }
        public int Offset { get => throw null; }
        public Lua52ChunkFormatException(string reason, int offset = 0) { }
    }

    public static class Lua52ChunkReader
    {
        public static Lunil.IR.Lua52.Lua52Chunk Read(System.ReadOnlySpan<byte> data, Lunil.IR.Lua52.Lua52ChunkReaderOptions? options = null) => throw null;
    }

    public sealed class Lua52ChunkReaderOptions : System.IEquatable<Lunil.IR.Lua52.Lua52ChunkReaderOptions>
    {
        public static Lunil.IR.Lua52.Lua52ChunkReaderOptions Default { get => throw null; }
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
        public static bool operator !=(Lunil.IR.Lua52.Lua52ChunkReaderOptions? left, Lunil.IR.Lua52.Lua52ChunkReaderOptions? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua52.Lua52ChunkReaderOptions? left, Lunil.IR.Lua52.Lua52ChunkReaderOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua52.Lua52ChunkReaderOptions? other) => throw null;
    }

    public readonly struct Lua52ChunkTarget : System.IEquatable<Lunil.IR.Lua52.Lua52ChunkTarget>
    {
        public Lunil.IR.Lua52.Lua52ByteOrder ByteOrder { get => throw null; init { } }
        public int SizeOfInt { get => throw null; init { } }
        public int SizeOfSizeT { get => throw null; init { } }
        public int InstructionSize { get => throw null; init { } }
        public int NumberSize { get => throw null; init { } }
        public static Lunil.IR.Lua52.Lua52ChunkTarget Host { get => throw null; }
        public Lua52ChunkTarget(Lunil.IR.Lua52.Lua52ByteOrder ByteOrder, int SizeOfInt, int SizeOfSizeT, int InstructionSize, int NumberSize) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua52.Lua52ChunkTarget left, Lunil.IR.Lua52.Lua52ChunkTarget right) => throw null;
        public static bool operator ==(Lunil.IR.Lua52.Lua52ChunkTarget left, Lunil.IR.Lua52.Lua52ChunkTarget right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua52.Lua52ChunkTarget other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua52.Lua52ByteOrder ByteOrder, out int SizeOfInt, out int SizeOfSizeT, out int InstructionSize, out int NumberSize) => throw null;
    }

    public sealed class Lua52Constant : System.IEquatable<Lunil.IR.Lua52.Lua52Constant>
    {
        public Lunil.IR.Lua52.Lua52ConstantKind Kind { get => throw null; init { } }
        public double NumberValue { get => throw null; init { } }
        public Lunil.IR.Lua52.Lua52String? StringValue { get => throw null; init { } }
        public static Lunil.IR.Lua52.Lua52Constant Nil { get => throw null; }
        public static Lunil.IR.Lua52.Lua52Constant False { get => throw null; }
        public static Lunil.IR.Lua52.Lua52Constant True { get => throw null; }
        public static Lunil.IR.Lua52.Lua52Constant FromBoolean(bool value) => throw null;
        public static Lunil.IR.Lua52.Lua52Constant FromNumber(double value) => throw null;
        public static Lunil.IR.Lua52.Lua52Constant FromString(Lunil.IR.Lua52.Lua52String value) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua52.Lua52Constant? left, Lunil.IR.Lua52.Lua52Constant? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua52.Lua52Constant? left, Lunil.IR.Lua52.Lua52Constant? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua52.Lua52Constant? other) => throw null;
    }

    public enum Lua52ConstantKind
    {
        Nil = 0,
        False = 1,
        True = 2,
        Number = 3,
        String = 4
    }

    public readonly struct Lua52Instruction : System.IEquatable<Lunil.IR.Lua52.Lua52Instruction>
    {
        public const int MaximumA = 255;
        public const int MaximumB = 511;
        public const int MaximumC = 511;
        public const int MaximumBx = 262143;
        public const int MaximumAx = 67108863;
        public const int SignedBxOffset = 131071;
        public uint RawValue { get => throw null; init { } }
        public Lunil.IR.Lua52.Lua52Opcode Opcode { get => throw null; }
        public int A { get => throw null; }
        public int C { get => throw null; }
        public int B { get => throw null; }
        public int Bx { get => throw null; }
        public int SignedBx { get => throw null; }
        public int Ax { get => throw null; }
        public bool IsConstantB { get => throw null; }
        public bool IsConstantC { get => throw null; }
        public Lua52Instruction(uint RawValue) { }
        public static Lunil.IR.Lua52.Lua52Instruction CreateAbc(Lunil.IR.Lua52.Lua52Opcode opcode, int a, int b, int c) => throw null;
        public static Lunil.IR.Lua52.Lua52Instruction CreateABx(Lunil.IR.Lua52.Lua52Opcode opcode, int a, int bx) => throw null;
        public static Lunil.IR.Lua52.Lua52Instruction CreateASignedBx(Lunil.IR.Lua52.Lua52Opcode opcode, int a, int signedBx) => throw null;
        public static Lunil.IR.Lua52.Lua52Instruction CreateAx(Lunil.IR.Lua52.Lua52Opcode opcode, int ax) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua52.Lua52Instruction left, Lunil.IR.Lua52.Lua52Instruction right) => throw null;
        public static bool operator ==(Lunil.IR.Lua52.Lua52Instruction left, Lunil.IR.Lua52.Lua52Instruction right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua52.Lua52Instruction other) => throw null;
        public void Deconstruct(out uint RawValue) => throw null;
    }

    public sealed class Lua52LocalVariable : System.IEquatable<Lunil.IR.Lua52.Lua52LocalVariable>
    {
        public Lunil.IR.Lua52.Lua52String? Name { get => throw null; init { } }
        public int StartProgramCounter { get => throw null; init { } }
        public int EndProgramCounter { get => throw null; init { } }
        public Lua52LocalVariable(Lunil.IR.Lua52.Lua52String? Name, int StartProgramCounter, int EndProgramCounter) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua52.Lua52LocalVariable? left, Lunil.IR.Lua52.Lua52LocalVariable? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua52.Lua52LocalVariable? left, Lunil.IR.Lua52.Lua52LocalVariable? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua52.Lua52LocalVariable? other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua52.Lua52String? Name, out int StartProgramCounter, out int EndProgramCounter) => throw null;
    }

    public enum Lua52Opcode
    {
        Move = 0,
        LoadConstant = 1,
        LoadConstantExtra = 2,
        LoadBoolean = 3,
        LoadNil = 4,
        GetUpvalue = 5,
        GetTableUpvalue = 6,
        GetTable = 7,
        SetTableUpvalue = 8,
        SetUpvalue = 9,
        SetTable = 10,
        NewTable = 11,
        Self = 12,
        Add = 13,
        Subtract = 14,
        Multiply = 15,
        Divide = 16,
        Modulo = 17,
        Power = 18,
        UnaryMinus = 19,
        LogicalNot = 20,
        Length = 21,
        Concatenate = 22,
        Jump = 23,
        Equal = 24,
        LessThan = 25,
        LessOrEqual = 26,
        Test = 27,
        TestSet = 28,
        Call = 29,
        TailCall = 30,
        Return = 31,
        NumericForLoop = 32,
        NumericForPrepare = 33,
        GenericForCall = 34,
        GenericForLoop = 35,
        SetList = 36,
        Closure = 37,
        VarArg = 38,
        ExtraArgument = 39
    }

    public sealed class Lua52Prototype : System.IEquatable<Lunil.IR.Lua52.Lua52Prototype>
    {
        public Lunil.IR.Lua52.Lua52String? Source { get => throw null; init { } }
        public int LineDefined { get => throw null; init { } }
        public int LastLineDefined { get => throw null; init { } }
        public byte ParameterCount { get => throw null; init { } }
        public byte VarArgFlags { get => throw null; init { } }
        public byte MaximumStackSize { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua52.Lua52Instruction> Code { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua52.Lua52Constant> Constants { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua52.Lua52UpvalueDescriptor> Upvalues { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua52.Lua52Prototype> NestedPrototypes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> LineInfo { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua52.Lua52LocalVariable> LocalVariables { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua52.Lua52String?> UpvalueNames { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua52.Lua52Prototype? left, Lunil.IR.Lua52.Lua52Prototype? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua52.Lua52Prototype? left, Lunil.IR.Lua52.Lua52Prototype? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua52.Lua52Prototype? other) => throw null;
    }

    public static class Lua52PrototypeConverter
    {
        public static Lunil.IR.Canonical.LuaIrModule Convert(System.ReadOnlySpan<byte> binaryChunk, Lunil.IR.Lua52.Lua52ChunkReaderOptions? options = null) => throw null;
        public static Lunil.IR.Canonical.LuaIrModule Convert(Lunil.IR.Lua52.Lua52Chunk chunk) => throw null;
    }

    public readonly struct Lua52String : System.IEquatable<Lunil.IR.Lua52.Lua52String>
    {
        public byte[] Bytes { get => throw null; init { } }
        public int Length { get => throw null; }
        public Lua52String(byte[] Bytes) { }
        public System.ReadOnlySpan<byte> AsSpan() => throw null;
        public byte[] ToArray() => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua52.Lua52String left, Lunil.IR.Lua52.Lua52String right) => throw null;
        public static bool operator ==(Lunil.IR.Lua52.Lua52String left, Lunil.IR.Lua52.Lua52String right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object obj) => throw null;
        public bool Equals(Lunil.IR.Lua52.Lua52String other) => throw null;
        public void Deconstruct(out byte[] Bytes) => throw null;
    }

    public readonly struct Lua52UpvalueDescriptor : System.IEquatable<Lunil.IR.Lua52.Lua52UpvalueDescriptor>
    {
        public byte InStack { get => throw null; init { } }
        public byte Index { get => throw null; init { } }
        public Lua52UpvalueDescriptor(byte InStack, byte Index) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua52.Lua52UpvalueDescriptor left, Lunil.IR.Lua52.Lua52UpvalueDescriptor right) => throw null;
        public static bool operator ==(Lunil.IR.Lua52.Lua52UpvalueDescriptor left, Lunil.IR.Lua52.Lua52UpvalueDescriptor right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua52.Lua52UpvalueDescriptor other) => throw null;
        public void Deconstruct(out byte InStack, out byte Index) => throw null;
    }
}
namespace Lunil.IR.Lua53
{
    public enum Lua53ByteOrder
    {
        LittleEndian = 0,
        BigEndian = 1
    }

    public static class Lua53CanonicalPrototypeWriter
    {
        public static Lunil.IR.Lua53.Lua53Chunk CreateChunk(Lunil.IR.Canonical.LuaIrModule module, int functionId, Lunil.IR.Lua53.Lua53ChunkTarget? target = null) => throw null;
        public static byte[] Write(Lunil.IR.Canonical.LuaIrModule module, int functionId, bool stripDebugInformation = false, Lunil.IR.Lua53.Lua53ChunkTarget? target = null) => throw null;
    }

    public sealed class Lua53Chunk : System.IEquatable<Lunil.IR.Lua53.Lua53Chunk>
    {
        public Lunil.IR.Lua53.Lua53ChunkTarget Target { get => throw null; init { } }
        public byte MainUpvalueCount { get => throw null; init { } }
        public Lunil.IR.Lua53.Lua53Prototype MainPrototype { get => throw null; init { } }
        public Lua53Chunk(Lunil.IR.Lua53.Lua53ChunkTarget Target, byte MainUpvalueCount, Lunil.IR.Lua53.Lua53Prototype MainPrototype) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua53.Lua53Chunk? left, Lunil.IR.Lua53.Lua53Chunk? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua53.Lua53Chunk? left, Lunil.IR.Lua53.Lua53Chunk? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua53.Lua53Chunk? other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua53.Lua53ChunkTarget Target, out byte MainUpvalueCount, out Lunil.IR.Lua53.Lua53Prototype MainPrototype) => throw null;
    }

    public sealed class Lua53ChunkFormatException : System.FormatException
    {
        public string Reason { get => throw null; }
        public int Offset { get => throw null; }
        public Lua53ChunkFormatException(string reason, int offset = 0) { }
    }

    public static class Lua53ChunkReader
    {
        public static Lunil.IR.Lua53.Lua53Chunk Read(System.ReadOnlySpan<byte> data, Lunil.IR.Lua53.Lua53ChunkReaderOptions? options = null) => throw null;
    }

    public sealed class Lua53ChunkReaderOptions : System.IEquatable<Lunil.IR.Lua53.Lua53ChunkReaderOptions>
    {
        public static Lunil.IR.Lua53.Lua53ChunkReaderOptions Default { get => throw null; }
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
        public static bool operator !=(Lunil.IR.Lua53.Lua53ChunkReaderOptions? left, Lunil.IR.Lua53.Lua53ChunkReaderOptions? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua53.Lua53ChunkReaderOptions? left, Lunil.IR.Lua53.Lua53ChunkReaderOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua53.Lua53ChunkReaderOptions? other) => throw null;
    }

    public readonly struct Lua53ChunkTarget : System.IEquatable<Lunil.IR.Lua53.Lua53ChunkTarget>
    {
        public Lunil.IR.Lua53.Lua53ByteOrder ByteOrder { get => throw null; }
        public byte SizeOfInt { get => throw null; }
        public byte SizeOfSizeT { get => throw null; }
        public byte InstructionSize { get => throw null; }
        public byte IntegerSize { get => throw null; }
        public byte NumberSize { get => throw null; }
        public static Lunil.IR.Lua53.Lua53ChunkTarget Host { get => throw null; }
        public Lua53ChunkTarget(Lunil.IR.Lua53.Lua53ByteOrder byteOrder, byte sizeOfInt, byte sizeOfSizeT, byte instructionSize, byte integerSize, byte numberSize) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua53.Lua53ChunkTarget left, Lunil.IR.Lua53.Lua53ChunkTarget right) => throw null;
        public static bool operator ==(Lunil.IR.Lua53.Lua53ChunkTarget left, Lunil.IR.Lua53.Lua53ChunkTarget right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua53.Lua53ChunkTarget other) => throw null;
    }

    public static class Lua53ChunkWriter
    {
        public static byte[] Write(Lunil.IR.Lua53.Lua53Chunk chunk, bool stripDebugInformation = false) => throw null;
    }

    public sealed class Lua53Constant : System.IEquatable<Lunil.IR.Lua53.Lua53Constant>
    {
        public Lunil.IR.Lua53.Lua53ConstantKind Kind { get => throw null; init { } }
        public long IntegerValue { get => throw null; init { } }
        public double FloatValue { get => throw null; init { } }
        public Lunil.IR.Lua53.Lua53String? StringValue { get => throw null; init { } }
        public static Lunil.IR.Lua53.Lua53Constant Nil { get => throw null; }
        public static Lunil.IR.Lua53.Lua53Constant False { get => throw null; }
        public static Lunil.IR.Lua53.Lua53Constant True { get => throw null; }
        public static Lunil.IR.Lua53.Lua53Constant FromBoolean(bool value) => throw null;
        public static Lunil.IR.Lua53.Lua53Constant FromInteger(long value) => throw null;
        public static Lunil.IR.Lua53.Lua53Constant FromFloat(double value) => throw null;
        public static Lunil.IR.Lua53.Lua53Constant FromString(Lunil.IR.Lua53.Lua53String value, bool isShort) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua53.Lua53Constant? left, Lunil.IR.Lua53.Lua53Constant? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua53.Lua53Constant? left, Lunil.IR.Lua53.Lua53Constant? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua53.Lua53Constant? other) => throw null;
    }

    public enum Lua53ConstantKind
    {
        Nil = 0,
        False = 1,
        True = 2,
        Integer = 3,
        Float = 4,
        ShortString = 5,
        LongString = 6
    }

    public readonly struct Lua53Instruction : System.IEquatable<Lunil.IR.Lua53.Lua53Instruction>
    {
        public const int MaximumA = 255;
        public const int MaximumB = 511;
        public const int MaximumC = 511;
        public const int MaximumBx = 262143;
        public const int MaximumAx = 67108863;
        public const int SignedBxOffset = 131071;
        public uint RawValue { get => throw null; init { } }
        public Lunil.IR.Lua53.Lua53Opcode Opcode { get => throw null; }
        public int A { get => throw null; }
        public int C { get => throw null; }
        public int B { get => throw null; }
        public int Bx { get => throw null; }
        public int SignedBx { get => throw null; }
        public int Ax { get => throw null; }
        public bool IsConstantB { get => throw null; }
        public bool IsConstantC { get => throw null; }
        public int RegisterB { get => throw null; }
        public int RegisterC { get => throw null; }
        public int ConstantB { get => throw null; }
        public int ConstantC { get => throw null; }
        public Lua53Instruction(uint RawValue) { }
        public static Lunil.IR.Lua53.Lua53Instruction CreateAbc(Lunil.IR.Lua53.Lua53Opcode opcode, int a, int b, int c) => throw null;
        public static Lunil.IR.Lua53.Lua53Instruction CreateABx(Lunil.IR.Lua53.Lua53Opcode opcode, int a, int bx) => throw null;
        public static Lunil.IR.Lua53.Lua53Instruction CreateASignedBx(Lunil.IR.Lua53.Lua53Opcode opcode, int a, int signedBx) => throw null;
        public static Lunil.IR.Lua53.Lua53Instruction CreateAx(Lunil.IR.Lua53.Lua53Opcode opcode, int ax) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua53.Lua53Instruction left, Lunil.IR.Lua53.Lua53Instruction right) => throw null;
        public static bool operator ==(Lunil.IR.Lua53.Lua53Instruction left, Lunil.IR.Lua53.Lua53Instruction right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua53.Lua53Instruction other) => throw null;
        public void Deconstruct(out uint RawValue) => throw null;
    }

    public sealed class Lua53LocalVariable : System.IEquatable<Lunil.IR.Lua53.Lua53LocalVariable>
    {
        public Lunil.IR.Lua53.Lua53String? Name { get => throw null; init { } }
        public int StartProgramCounter { get => throw null; init { } }
        public int EndProgramCounter { get => throw null; init { } }
        public Lua53LocalVariable(Lunil.IR.Lua53.Lua53String? Name, int StartProgramCounter, int EndProgramCounter) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua53.Lua53LocalVariable? left, Lunil.IR.Lua53.Lua53LocalVariable? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua53.Lua53LocalVariable? left, Lunil.IR.Lua53.Lua53LocalVariable? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua53.Lua53LocalVariable? other) => throw null;
        public void Deconstruct(out Lunil.IR.Lua53.Lua53String? Name, out int StartProgramCounter, out int EndProgramCounter) => throw null;
    }

    public enum Lua53Opcode
    {
        Move = 0,
        LoadConstant = 1,
        LoadConstantExtra = 2,
        LoadBoolean = 3,
        LoadNil = 4,
        GetUpvalue = 5,
        GetGlobal = 6,
        GetTable = 7,
        SetTableUpvalue = 8,
        SetUpvalue = 9,
        SetTable = 10,
        NewTable = 11,
        Self = 12,
        Add = 13,
        Subtract = 14,
        Multiply = 15,
        Modulo = 16,
        Power = 17,
        Divide = 18,
        FloorDivide = 19,
        BitwiseAnd = 20,
        BitwiseOr = 21,
        BitwiseXor = 22,
        ShiftLeft = 23,
        ShiftRight = 24,
        UnaryMinus = 25,
        BitwiseNot = 26,
        LogicalNot = 27,
        Length = 28,
        Concatenate = 29,
        Jump = 30,
        Equal = 31,
        LessThan = 32,
        LessOrEqual = 33,
        Test = 34,
        TestSet = 35,
        Call = 36,
        TailCall = 37,
        Return = 38,
        NumericForLoop = 39,
        NumericForPrepare = 40,
        GenericForCall = 41,
        GenericForLoop = 42,
        SetList = 43,
        Closure = 44,
        VarArg = 45,
        ExtraArgument = 46
    }

    public sealed class Lua53Prototype : System.IEquatable<Lunil.IR.Lua53.Lua53Prototype>
    {
        public Lunil.IR.Lua53.Lua53String? Source { get => throw null; init { } }
        public int LineDefined { get => throw null; init { } }
        public int LastLineDefined { get => throw null; init { } }
        public byte ParameterCount { get => throw null; init { } }
        public byte VarArgFlags { get => throw null; init { } }
        public byte MaximumStackSize { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua53.Lua53Instruction> Code { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua53.Lua53Constant> Constants { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua53.Lua53UpvalueDescriptor> Upvalues { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua53.Lua53Prototype> NestedPrototypes { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<int> LineInfo { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua53.Lua53LocalVariable> LocalVariables { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.IR.Lua53.Lua53String?> UpvalueNames { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua53.Lua53Prototype? left, Lunil.IR.Lua53.Lua53Prototype? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua53.Lua53Prototype? left, Lunil.IR.Lua53.Lua53Prototype? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua53.Lua53Prototype? other) => throw null;
    }

    public static class Lua53PrototypeConverter
    {
        public static Lunil.IR.Canonical.LuaIrModule Convert(System.ReadOnlySpan<byte> binaryChunk, Lunil.IR.Lua53.Lua53ChunkReaderOptions? options = null) => throw null;
        public static Lunil.IR.Canonical.LuaIrModule Convert(Lunil.IR.Lua53.Lua53Chunk chunk) => throw null;
    }

    public readonly struct Lua53String : System.IEquatable<Lunil.IR.Lua53.Lua53String>
    {
        public byte[] Bytes { get => throw null; init { } }
        public int Length { get => throw null; }
        public Lua53String(byte[] Bytes) { }
        public System.ReadOnlySpan<byte> AsSpan() => throw null;
        public byte[] ToArray() => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua53.Lua53String left, Lunil.IR.Lua53.Lua53String right) => throw null;
        public static bool operator ==(Lunil.IR.Lua53.Lua53String left, Lunil.IR.Lua53.Lua53String right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object obj) => throw null;
        public bool Equals(Lunil.IR.Lua53.Lua53String other) => throw null;
        public void Deconstruct(out byte[] Bytes) => throw null;
    }

    public readonly struct Lua53UpvalueDescriptor : System.IEquatable<Lunil.IR.Lua53.Lua53UpvalueDescriptor>
    {
        public byte InStack { get => throw null; init { } }
        public byte Index { get => throw null; init { } }
        public Lua53UpvalueDescriptor(byte InStack, byte Index) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua53.Lua53UpvalueDescriptor left, Lunil.IR.Lua53.Lua53UpvalueDescriptor right) => throw null;
        public static bool operator ==(Lunil.IR.Lua53.Lua53UpvalueDescriptor left, Lunil.IR.Lua53.Lua53UpvalueDescriptor right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua53.Lua53UpvalueDescriptor other) => throw null;
        public void Deconstruct(out byte InStack, out byte Index) => throw null;
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
namespace Lunil.IR.Lua55
{
    public static class Lua55CanonicalPrototypeWriter
    {
        public static byte[] Write(Lunil.IR.Canonical.LuaIrModule module, int functionId, bool stripDebug = false) => throw null;
    }

    public sealed class Lua55Chunk : System.IEquatable<Lunil.IR.Lua55.Lua55Chunk>
    {
        public byte[] Bytes { get => throw null; init { } }
        public Lua55Chunk(byte[] Bytes) { }
        public byte[] ToArray() => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.IR.Lua55.Lua55Chunk? left, Lunil.IR.Lua55.Lua55Chunk? right) => throw null;
        public static bool operator ==(Lunil.IR.Lua55.Lua55Chunk? left, Lunil.IR.Lua55.Lua55Chunk? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.IR.Lua55.Lua55Chunk? other) => throw null;
        public void Deconstruct(out byte[] Bytes) => throw null;
    }

    public sealed class Lua55ChunkFormatException : System.FormatException
    {
        public string Reason { get => throw null; }
        public int Offset { get => throw null; }
        public Lua55ChunkFormatException(string reason, int offset = 0) { }
    }

    public static class Lua55ChunkReader
    {
        public static Lunil.IR.Lua54.Lua54Chunk Read(System.ReadOnlySpan<byte> data, Lunil.IR.Lua54.Lua54ChunkReaderOptions? options = null) => throw null;
    }

    public static class Lua55ChunkWriter
    {
        public static byte[] Write(Lunil.IR.Lua54.Lua54Chunk chunk, bool stripDebugInformation = false) => throw null;
    }

    public static class Lua55GeneratedInstructionCodec
    {
        public const int MaximumA = 255;
        public const int MaximumB = 255;
        public const int MaximumC = 255;
        public const int MaximumBx = 131071;
        public const int MaximumAx = 33554431;
        public const int SignedBxOffset = 65535;
        public const int SignedJumpOffset = 16777215;
        public static Lunil.IR.Lua55.Lua55Opcode DecodeOpcode(uint raw) => throw null;
        public static int DecodeA(uint raw) => throw null;
        public static int DecodeB(uint raw) => throw null;
        public static int DecodeC(uint raw) => throw null;
        public static int DecodeVB(uint raw) => throw null;
        public static int DecodeVC(uint raw) => throw null;
        public static bool DecodeK(uint raw) => throw null;
        public static int DecodeBx(uint raw) => throw null;
        public static int DecodeAx(uint raw) => throw null;
        public static int DecodeSignedBx(uint raw) => throw null;
        public static int DecodeSignedJump(uint raw) => throw null;
        public static uint EncodeAbc(Lunil.IR.Lua55.Lua55Opcode opcode, int a, int b, int c, bool k = false) => throw null;
        public static uint EncodeVAbc(Lunil.IR.Lua55.Lua55Opcode opcode, int a, int vb, int vc, bool k = false) => throw null;
        public static uint EncodeABx(Lunil.IR.Lua55.Lua55Opcode opcode, int a, int bx) => throw null;
        public static uint EncodeAx(Lunil.IR.Lua55.Lua55Opcode opcode, int ax) => throw null;
    }

    public readonly struct Lua55Instruction : System.IEquatable<Lunil.IR.Lua55.Lua55Instruction>
    {
        public const int MaximumA = 255;
        public const int MaximumB = 255;
        public const int MaximumC = 255;
        public const int MaximumBx = 131071;
        public const int MaximumAx = 33554431;
        public const int SignedBxOffset = 65535;
        public const int SignedJumpOffset = 16777215;
        public uint RawValue { get => throw null; init { } }
        public Lunil.IR.Lua55.Lua55Opcode Opcode { get => throw null; }
        public int A { get => throw null; }
        public int B { get => throw null; }
        public int C { get => throw null; }
        public int VB { get => throw null; }
        public int VC { get => throw null; }
        public bool K { get => throw null; }
        public int Bx { get => throw null; }
        public int Ax { get => throw null; }
        public int SignedBx { get => throw null; }
        public int SignedJump { get => throw null; }
        public Lua55Instruction(uint RawValue) { }
        public static Lunil.IR.Lua55.Lua55Instruction CreateAbc(Lunil.IR.Lua55.Lua55Opcode opcode, int a, int b, int c, bool k = false) => throw null;
        public static Lunil.IR.Lua55.Lua55Instruction CreateABx(Lunil.IR.Lua55.Lua55Opcode opcode, int a, int bx) => throw null;
        public static Lunil.IR.Lua55.Lua55Instruction CreateAx(Lunil.IR.Lua55.Lua55Opcode opcode, int ax) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.IR.Lua55.Lua55Instruction left, Lunil.IR.Lua55.Lua55Instruction right) => throw null;
        public static bool operator ==(Lunil.IR.Lua55.Lua55Instruction left, Lunil.IR.Lua55.Lua55Instruction right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.IR.Lua55.Lua55Instruction other) => throw null;
        public void Deconstruct(out uint RawValue) => throw null;
    }

    public enum Lua55Opcode
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
        ShiftLeftImmediate = 32,
        ShiftRightImmediate = 33,
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
        GetVarArg = 81,
        ErrorIfNotNil = 82,
        VarArgPrepare = 83,
        ExtraArgument = 84
    }

    public static class Lua55PrototypeConverter
    {
        public static Lunil.IR.Canonical.LuaIrModule Convert(System.ReadOnlySpan<byte> bytes, Lunil.IR.Lua54.Lua54ChunkReaderOptions? options = null) => throw null;
    }
}
