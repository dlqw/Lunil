namespace Luac.Semantics.Binding;

public enum LuaSymbolKind : byte
{
    Environment,
    Parameter,
    Local,
    NumericForVariable,
    GenericForVariable,
}
