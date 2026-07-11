using Luac.Core.Text;

namespace Luac.Semantics.Binding;

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
