using Lunil.Core.Text;

namespace Lunil.Semantics.Binding;

public enum LuaNameResolutionKind : byte
{
    Local,
    Upvalue,
    Global,
}

public sealed record LuaNameReference(
    TextSpan Span,
    string Name,
    LuaNameResolutionKind ResolutionKind,
    LuaSymbol Symbol,
    bool IsWrite);
