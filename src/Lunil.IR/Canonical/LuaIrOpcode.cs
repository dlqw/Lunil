namespace Lunil.IR.Canonical;

/// <summary>
/// Canonical register-machine operations shared by the interpreter and code generators.
/// Variable result counts use -1 to denote the open interval ending at the frame top.
/// </summary>
public enum LuaIrOpcode : byte
{
    LoadConstant,
    LoadNil,
    Move,
    SetTop,
    GetUpvalue,
    SetUpvalue,
    NewTable,
    GetTable,
    SetTable,
    SetList,
    Closure,
    VarArg,
    Unary,
    Binary,
    Jump,
    JumpIfFalse,
    JumpIfTrue,
    Call,
    TailCall,
    Return,
    Close,
    MarkToBeClosed,
    NumericForPrepare,
    NumericForLoop,
}

public enum LuaIrUnaryOperator : byte
{
    Negate,
    BitwiseNot,
    LogicalNot,
    Length,
}

public enum LuaIrBinaryOperator : byte
{
    Add,
    Subtract,
    Multiply,
    Divide,
    FloorDivide,
    Modulo,
    Power,
    Concatenate,
    Equal,
    NotEqual,
    LessThan,
    LessThanOrEqual,
    GreaterThan,
    GreaterThanOrEqual,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
}

public enum LuaIrCallKind : byte
{
    Regular,
    ForIterator,
}

[Flags]
public enum LuaIrInstructionEffects : byte
{
    None = 0,
    MayAllocate = 1 << 0,
    MayCall = 1 << 1,
    MayYield = 1 << 2,
    MayThrow = 1 << 3,
    IsGcSafePoint = 1 << 4,
}
