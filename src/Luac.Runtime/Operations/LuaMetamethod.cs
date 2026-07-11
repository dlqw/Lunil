namespace Luac.Runtime.Operations;

internal enum LuaMetamethod : byte
{
    Index,
    NewIndex,
    Call,
    Add,
    Subtract,
    Multiply,
    Divide,
    FloorDivide,
    Modulo,
    Power,
    UnaryMinus,
    BitwiseNot,
    BitwiseAnd,
    BitwiseOr,
    BitwiseXor,
    ShiftLeft,
    ShiftRight,
    Concatenate,
    Length,
    Equal,
    LessThan,
    LessThanOrEqual,
    Close,
    GarbageCollect,
    Mode,
}

internal static class LuaMetamethodFacts
{
    public static ReadOnlySpan<byte> GetName(LuaMetamethod metamethod) => metamethod switch
    {
        LuaMetamethod.Index => "__index"u8,
        LuaMetamethod.NewIndex => "__newindex"u8,
        LuaMetamethod.Call => "__call"u8,
        LuaMetamethod.Add => "__add"u8,
        LuaMetamethod.Subtract => "__sub"u8,
        LuaMetamethod.Multiply => "__mul"u8,
        LuaMetamethod.Divide => "__div"u8,
        LuaMetamethod.FloorDivide => "__idiv"u8,
        LuaMetamethod.Modulo => "__mod"u8,
        LuaMetamethod.Power => "__pow"u8,
        LuaMetamethod.UnaryMinus => "__unm"u8,
        LuaMetamethod.BitwiseNot => "__bnot"u8,
        LuaMetamethod.BitwiseAnd => "__band"u8,
        LuaMetamethod.BitwiseOr => "__bor"u8,
        LuaMetamethod.BitwiseXor => "__bxor"u8,
        LuaMetamethod.ShiftLeft => "__shl"u8,
        LuaMetamethod.ShiftRight => "__shr"u8,
        LuaMetamethod.Concatenate => "__concat"u8,
        LuaMetamethod.Length => "__len"u8,
        LuaMetamethod.Equal => "__eq"u8,
        LuaMetamethod.LessThan => "__lt"u8,
        LuaMetamethod.LessThanOrEqual => "__le"u8,
        LuaMetamethod.Close => "__close"u8,
        LuaMetamethod.GarbageCollect => "__gc"u8,
        LuaMetamethod.Mode => "__mode"u8,
        _ => throw new ArgumentOutOfRangeException(nameof(metamethod)),
    };
}
