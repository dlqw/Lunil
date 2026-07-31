using Lunil.Core.Text;

namespace Lunil.Semantics.Binding;

/// <summary>Identifies the syntactic form represented by a unified Lua code reference.</summary>
public enum LuaReferenceKind : byte
{
    Name,
    Member,
    Index,
}

/// <summary>Describes how a reference is used at its source location.</summary>
[Flags]
public enum LuaReferenceAccess : byte
{
    None = 0,
    Read = 1,
    Write = 2,
    Call = 4,
    MethodCall = 8,
}

/// <summary>Describes the strongest binding fact available for a code reference.</summary>
public enum LuaReferenceResolutionKind : byte
{
    LexicalSymbol,
    MemberCandidate,
    LiteralIndexCandidate,
    DynamicIndex,
    Incomplete,
}

/// <summary>
/// A non-lexical member or index reference. Unlike <see cref="LuaNameReference"/>, this record
/// never invents a lexical symbol for a table member.
/// </summary>
public sealed record LuaMemberReference(
    TextSpan Span,
    string? Name,
    LuaReferenceKind Kind,
    LuaReferenceAccess Access,
    TextSpan ReceiverSpan,
    TextSpan? IndexSpan,
    int ContainingFunctionId,
    LuaReferenceResolutionKind ResolutionKind,
    string ResolutionReason);

/// <summary>
/// Ordered projection of lexical names, dot/colon members, and bracket indices used by editor and
/// workspace indexes.
/// </summary>
public sealed record LuaCodeReference(
    TextSpan Span,
    string? Name,
    LuaReferenceKind Kind,
    LuaReferenceAccess Access,
    TextSpan? ReceiverSpan,
    TextSpan? IndexSpan,
    int ContainingFunctionId,
    LuaNameReference? LexicalReference,
    string? CandidateName,
    LuaReferenceResolutionKind ResolutionKind,
    string ResolutionReason);
