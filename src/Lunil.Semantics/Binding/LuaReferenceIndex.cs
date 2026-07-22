using System.Collections.Immutable;

namespace Lunil.Semantics.Binding;

public partial record LuaSemanticModel
{
    /// <summary>Finds all reads and writes bound to a symbol in this semantic snapshot.</summary>
    public ImmutableArray<LuaNameReference> FindReferences(LuaSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        if (!Symbols.Any(candidate => ReferenceEquals(candidate, symbol)))
        {
            throw new ArgumentException(
                "The symbol does not belong to this semantic model.",
                nameof(symbol));
        }

        if (symbol.Kind == LuaSymbolKind.Global)
        {
            return FindGlobalReferences(symbol.Name);
        }

        return References
            .Where(reference => ReferenceEquals(reference.Symbol, symbol))
            .OrderBy(static reference => reference.Span.Start)
            .ThenBy(static reference => reference.Span.Length)
            .ToImmutableArray();
    }

    /// <summary>
    /// Finds all references to a global name, including implicit <c>_ENV</c>-backed references.
    /// </summary>
    public ImmutableArray<LuaNameReference> FindGlobalReferences(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return References
            .Where(reference =>
                reference.ResolutionKind == LuaNameResolutionKind.Global &&
                string.Equals(reference.Name, name, StringComparison.Ordinal))
            .OrderBy(static reference => reference.Span.Start)
            .ThenBy(static reference => reference.Span.Length)
            .ToImmutableArray();
    }
}
