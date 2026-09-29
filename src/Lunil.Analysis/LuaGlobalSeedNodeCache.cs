using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.Core;

namespace Lunil.Analysis;

/// <summary>
/// Memoizes the type-node closure of global seed dictionaries. The dictionaries are
/// immutable and owned outside the analysis pipeline (the embedded builtin library,
/// workspace snapshots, host contracts), so the memo is keyed by dictionary identity and
/// its lifetime follows the dictionary through the conditional weak table. The process
/// default lets concurrent document analyses walk a shared library universe at most once
/// per dictionary generation; hosts that isolate analyses can inject a dedicated cache.
/// </summary>
internal sealed class LuaGlobalSeedNodeCache
{
    internal static readonly LuaGlobalSeedNodeCache Process = new();

    private static readonly HashSet<LuaType> EmptyNodes = new(LunilReferenceEqualityComparer.Instance);

    private readonly ConditionalWeakTable<ImmutableDictionary<string, LuaType>, HashSet<LuaType>> _nodes = new();

    internal HashSet<LuaType> GetOrCreate(ImmutableDictionary<string, LuaType> dictionary)
    {
        if (dictionary.IsEmpty)
        {
            return EmptyNodes;
        }

        lock (dictionary)
        {
            if (!_nodes.TryGetValue(dictionary, out var nodes))
            {
                nodes = new HashSet<LuaType>(LunilReferenceEqualityComparer.Instance);
                foreach (var pair in dictionary)
                {
                    CollectTypeNodesInto(pair.Value, nodes);
                }

                _nodes.AddOrUpdate(dictionary, nodes);
            }

            return nodes;
        }
    }

    /// <summary>
    /// Adds every composite node reachable through the edges table-mutation propagation
    /// descends. The persistent set doubles as the visited set, so repeated commits of
    /// shared graphs only touch newly published nodes.
    /// </summary>
    internal static void CollectTypeNodesInto(LuaType type, HashSet<LuaType> nodes)
    {
        if (!nodes.Add(type))
        {
            return;
        }

        switch (type)
        {
            case LuaMetatableType metatable:
                CollectTypeNodesInto(metatable.MetatableType, nodes);
                break;
            case LuaPrototypeType prototype:
                CollectTypeNodesInto(prototype.Shape, nodes);
                foreach (var baseType in prototype.BaseTypes)
                {
                    CollectTypeNodesInto(baseType, nodes);
                }

                break;
            case LuaUnionType union:
                foreach (var member in union.Types)
                {
                    CollectTypeNodesInto(member, nodes);
                }

                break;
            case LuaStructuralTableType table:
                foreach (var field in table.Fields)
                {
                    if (field.KeyType is not null)
                    {
                        CollectTypeNodesInto(field.KeyType, nodes);
                    }

                    CollectTypeNodesInto(field.ValueType, nodes);
                }

                break;
            case LuaFunctionType function:
                foreach (var parameter in function.Parameters)
                {
                    CollectTypeNodesInto(parameter.Type, nodes);
                }

                CollectTypeNodesInto(function.Returns, nodes);
                break;
            case LuaTypePack pack:
                foreach (var item in pack.Head)
                {
                    CollectTypeNodesInto(item, nodes);
                }

                if (pack.VariadicType is not null)
                {
                    CollectTypeNodesInto(pack.VariadicType, nodes);
                }

                break;
            case LuaOverloadType overload:
                foreach (var signature in overload.Signatures)
                {
                    CollectTypeNodesInto(signature, nodes);
                }

                break;
        }
    }
}
