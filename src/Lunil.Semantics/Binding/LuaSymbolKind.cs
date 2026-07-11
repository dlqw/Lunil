namespace Lunil.Semantics.Binding;

public enum LuaSymbolKind : byte
{
    Environment,
    Parameter,
    Local,
    NumericForVariable,
    GenericForVariable,
}
