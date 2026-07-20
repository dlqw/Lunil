namespace Lunil.IR.Lua55;

/// <summary>Opcodes from the official Lua 5.5 instruction stream.</summary>
public enum Lua55Opcode : byte
{
    Move, LoadInteger, LoadFloat, LoadConstant, LoadConstantExtra, LoadFalse,
    LoadFalseAndSkip, LoadTrue, LoadNil, GetUpvalue, SetUpvalue, GetTableUpvalue,
    GetTable, GetInteger, GetField, SetTableUpvalue, SetTable, SetInteger, SetField,
    NewTable, Self, AddImmediate, AddConstant, SubtractConstant, MultiplyConstant,
    ModuloConstant, PowerConstant, DivideConstant, FloorDivideConstant,
    BitwiseAndConstant, BitwiseOrConstant, BitwiseXorConstant, ShiftLeftImmediate,
    ShiftRightImmediate, Add, Subtract, Multiply, Modulo, Power, Divide, FloorDivide,
    BitwiseAnd, BitwiseOr, BitwiseXor, ShiftLeft, ShiftRight, MetamethodBinary,
    MetamethodBinaryImmediate, MetamethodBinaryConstant, UnaryMinus, BitwiseNot,
    LogicalNot, Length, Concatenate, Close, ToBeClosed, Jump, Equal, LessThan,
    LessOrEqual, EqualConstant, EqualImmediate, LessThanImmediate, LessOrEqualImmediate,
    GreaterThanImmediate, GreaterOrEqualImmediate, Test, TestSet, Call, TailCall, Return,
    ReturnZero, ReturnOne, NumericForLoop, NumericForPrepare, GenericForPrepare,
    GenericForCall, GenericForLoop, SetList, Closure, VarArg, GetVarArg, ErrorIfNotNil,
    VarArgPrepare, ExtraArgument,
}

public readonly record struct Lua55Instruction(uint RawValue)
{
    public const int MaximumA = Lua55GeneratedInstructionCodec.MaximumA;
    public const int MaximumB = Lua55GeneratedInstructionCodec.MaximumB;
    public const int MaximumC = Lua55GeneratedInstructionCodec.MaximumC;
    public const int MaximumBx = Lua55GeneratedInstructionCodec.MaximumBx;
    public const int MaximumAx = Lua55GeneratedInstructionCodec.MaximumAx;
    public const int SignedBxOffset = Lua55GeneratedInstructionCodec.SignedBxOffset;
    public const int SignedJumpOffset = Lua55GeneratedInstructionCodec.SignedJumpOffset;

    public Lua55Opcode Opcode => Lua55GeneratedInstructionCodec.DecodeOpcode(RawValue);
    public int A => Lua55GeneratedInstructionCodec.DecodeA(RawValue);
    public int B => Lua55GeneratedInstructionCodec.DecodeB(RawValue);
    public int C => Lua55GeneratedInstructionCodec.DecodeC(RawValue);
    public int VB => Lua55GeneratedInstructionCodec.DecodeVB(RawValue);
    public int VC => Lua55GeneratedInstructionCodec.DecodeVC(RawValue);
    public bool K => Lua55GeneratedInstructionCodec.DecodeK(RawValue);
    public int Bx => Lua55GeneratedInstructionCodec.DecodeBx(RawValue);
    public int Ax => Lua55GeneratedInstructionCodec.DecodeAx(RawValue);
    public int SignedBx => Lua55GeneratedInstructionCodec.DecodeSignedBx(RawValue);
    public int SignedJump => Lua55GeneratedInstructionCodec.DecodeSignedJump(RawValue);

    public static Lua55Instruction CreateAbc(Lua55Opcode opcode, int a, int b, int c, bool k = false) =>
        new(Lua55GeneratedInstructionCodec.EncodeAbc(opcode, a, b, c, k));

    public static Lua55Instruction CreateABx(Lua55Opcode opcode, int a, int bx) =>
        new(Lua55GeneratedInstructionCodec.EncodeABx(opcode, a, bx));

    public static Lua55Instruction CreateAx(Lua55Opcode opcode, int ax) =>
        new(Lua55GeneratedInstructionCodec.EncodeAx(opcode, ax));
}
