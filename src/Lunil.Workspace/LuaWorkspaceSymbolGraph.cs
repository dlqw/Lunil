using System.Collections.Immutable;
using Lunil.Analysis;
using Lunil.Core.Text;

namespace Lunil.Workspace;

public enum LuaWorkspaceExportKind : byte
{
    Module,
    Value,
    Field,
    Function,
    Class,
    Alias,
    Callback,
    Persistence,
    Dynamic,
}

public enum LuaWorkspaceBindingStatus : byte
{
    Resolved,
    Dynamic,
    Unresolved,
}

/// <summary>A stable module-qualified exported symbol available to workspace queries.</summary>
public sealed record LuaWorkspaceExportSymbol(
    string Key,
    string ModuleName,
    string Path,
    string Name,
    LuaWorkspaceExportKind Kind,
    LuaType Type,
    TextSpan DefinitionSpan,
    string? TargetKey,
    bool IsReExport,
    bool IsExternal,
    bool IsDynamic,
    LuaHostSourceLocation? ExternalSource)
{
    /// <summary>The stable semantic function key when this export denotes a Lua function.</summary>
    public string? FunctionKey { get; init; }
}

public sealed record LuaWorkspaceExportEdge(
    string SourceKey,
    string TargetKey,
    string Kind);

public sealed record LuaWorkspaceExportGraph(
    ImmutableArray<LuaWorkspaceExportSymbol> Symbols,
    ImmutableArray<LuaWorkspaceExportEdge> Edges)
{
    public static LuaWorkspaceExportGraph Empty { get; } = new([], []);

    public LuaWorkspaceExportSymbol? Find(string moduleName, string path) => Symbols.FirstOrDefault(symbol =>
        string.Equals(symbol.ModuleName, moduleName, StringComparison.Ordinal) &&
        string.Equals(symbol.Path, path, StringComparison.Ordinal));
}

/// <summary>Workspace-level binding of a call through a required Lua or host module.</summary>
public sealed record LuaWorkspaceModuleCallBinding(
    string SourceModuleName,
    TextSpan Span,
    int ContainingFunctionId,
    string RequestedModuleName,
    string MemberPath,
    string? TargetSymbolKey,
    string? TargetFunctionKey,
    ImmutableArray<string> CandidateKeys,
    LuaWorkspaceBindingStatus Status,
    string? Reason,
    TextSpan? DefinitionSpan,
    LuaHostSourceLocation? ExternalDefinition,
    LuaHostSourceLocation? ExternalImplementation);

public sealed record LuaWorkspaceModuleCallBindings(ImmutableArray<LuaWorkspaceModuleCallBinding> Edges)
{
    public static LuaWorkspaceModuleCallBindings Empty { get; } = new([]);
}
