using System.Collections.Immutable;
using Lunil.Core.Text;

namespace Lunil.Workspace;

public enum LuaModuleDependencyKind : byte
{
    Static,
    Dynamic,
}

public sealed record LuaModuleDependency(
    LuaModuleIdentity Source,
    string RequestedName,
    LuaModuleIdentity? Target,
    LuaModuleDependencyKind Kind,
    TextSpan Span);

public sealed record LuaModuleNode(
    LuaModuleIdentity Identity,
    string SourceIdentity,
    string ContentHash,
    ImmutableArray<LuaModuleDependency> Dependencies);

public sealed record LuaModuleStronglyConnectedComponent(
    int Id,
    ImmutableArray<LuaModuleIdentity> Modules,
    bool IsCyclic);

public sealed record LuaModuleGraph(
    ImmutableArray<LuaModuleNode> Nodes,
    ImmutableArray<LuaModuleDependency> Dependencies,
    ImmutableArray<LuaModuleStronglyConnectedComponent> Components)
{
    public static LuaModuleGraph Empty { get; } = new([], [], []);
}
