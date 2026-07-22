using System.Collections.Immutable;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;

namespace Lunil.Analysis;

/// <summary>Classifies the syntactic and type-level shape of a call target.</summary>
public enum LuaCallKind : byte
{
    Direct,
    Member,
    Method,
    Callable,
    Dynamic,
}

/// <summary>Describes how completely static analysis resolved a call.</summary>
public enum LuaCallResolutionStatus : byte
{
    Resolved = 0,
    Dynamic = 1,
    Unresolved = 2,
}

/// <summary>Stable reason values used by <see cref="LuaCallSite.UnresolvedReason"/>.</summary>
public static class LuaCallUnresolvedReasons
{
    public const string CalleeSignatureIsDynamic = "callee-signature-is-dynamic";

    public const string ModuleRequestIsDynamic = "module-request-is-dynamic";

    public const string CalleeIsNotCallable = "callee-is-not-callable";

    public const string CallWasNotAnalyzed = "call-was-not-analyzed";
}

/// <summary>Describes the receiver and member selected by a member or method call.</summary>
public sealed record LuaMemberTarget(
    TextSpan ReceiverSpan,
    string Name,
    LuaType ReceiverType);

/// <summary>Immutable typed projection of one call expression.</summary>
public sealed record LuaCallSite(
    TextSpan Span,
    int ContainingFunctionId,
    TextSpan CalleeSpan,
    LuaCallKind Kind,
    LuaSymbol? DirectSymbol,
    string? DirectName,
    LuaType CalleeType,
    LuaMemberTarget? MemberTarget,
    string? ModuleRequest,
    int? TargetFunctionId,
    LuaCallResolutionStatus ResolutionStatus,
    string? UnresolvedReason);

/// <summary>
/// Immutable per-compilation call graph. Every call site is retained as an edge, including
/// dynamic and unresolved calls whose target function is absent.
/// </summary>
public sealed record LuaCallGraph(
    ImmutableArray<int> FunctionIds,
    ImmutableArray<LuaCallSite> Edges)
{
    public static LuaCallGraph Empty { get; } = new([], []);
}
