namespace Lunil.Semantics.Binding;

public enum LuaLocalAttributeKind : byte
{
    None,
    Constant,
    ToBeClosed,
    VarArg,
}
