using Lunil.Core.Text;

namespace Lunil.Semantics.Binding;

public sealed class LuaSymbol
{
    internal LuaSymbol(
        int id,
        string name,
        LuaSymbolKind kind,
        LuaLocalAttributeKind attribute,
        TextSpan declaringSpan,
        int functionId,
        int scopeDepth)
    {
        Id = id;
        Name = name;
        Kind = kind;
        Attribute = attribute;
        DeclaringSpan = declaringSpan;
        FunctionId = functionId;
        ScopeDepth = scopeDepth;
    }

    public int Id { get; }

    public string Name { get; }

    public LuaSymbolKind Kind { get; }

    public LuaLocalAttributeKind Attribute { get; }

    public TextSpan DeclaringSpan { get; }

    public int FunctionId { get; }

    public int ScopeDepth { get; }

    public bool IsReadOnly => Attribute is LuaLocalAttributeKind.Constant or LuaLocalAttributeKind.ToBeClosed;

    public bool IsCaptured { get; internal set; }
}
