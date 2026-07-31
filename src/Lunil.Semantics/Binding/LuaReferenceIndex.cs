using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using Lunil.Core.Text;

namespace Lunil.Semantics.Binding;

internal sealed class LuaSemanticReferenceIndex
{
    private readonly LuaSemanticModel _model;
    private readonly ImmutableDictionary<LuaSymbol, ImmutableArray<LuaNameReference>> _namesBySymbol;
    private readonly ImmutableDictionary<string, ImmutableArray<LuaNameReference>> _globalsByName;
    private readonly ImmutableDictionary<TextSpan, ImmutableArray<LuaCodeReference>> _codeBySpan;
    private readonly ImmutableDictionary<LuaSymbol, ImmutableArray<LuaCodeReference>> _codeBySymbol;
    private readonly FunctionInterval _functions;

    public LuaSemanticReferenceIndex(LuaSemanticModel model)
    {
        _model = model;
        _namesBySymbol = model.References
            .GroupBy(static reference => reference.Symbol)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.OrderBy(static reference => reference.Span.Start)
                    .ThenBy(static reference => reference.Span.Length)
                    .ToImmutableArray());
        _globalsByName = model.References
            .Where(static reference => reference.ResolutionKind == LuaNameResolutionKind.Global)
            .GroupBy(static reference => reference.Name, StringComparer.Ordinal)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.OrderBy(static reference => reference.Span.Start)
                    .ThenBy(static reference => reference.Span.Length)
                    .ToImmutableArray(),
                StringComparer.Ordinal);
        _codeBySpan = model.UnifiedReferences
            .GroupBy(static reference => reference.Span)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.ToImmutableArray());
        _codeBySymbol = model.UnifiedReferences
            .Where(static reference => reference.LexicalReference is not null)
            .GroupBy(static reference => reference.LexicalReference!.Symbol)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.ToImmutableArray());
        _functions = FunctionInterval.Create(model.Functions);
    }

    public ImmutableArray<LuaNameReference> FindReferences(LuaSymbol symbol)
    {
        EnsureSymbol(symbol);
        return symbol.Kind == LuaSymbolKind.Global
            ? FindGlobalReferences(symbol.Name)
            : _namesBySymbol.GetValueOrDefault(symbol, []);
    }

    public ImmutableArray<LuaNameReference> FindGlobalReferences(string name) =>
        _globalsByName.GetValueOrDefault(name, []);

    public ImmutableArray<LuaCodeReference> FindCodeReferences(LuaSymbol symbol)
    {
        EnsureSymbol(symbol);
        return _codeBySymbol.GetValueOrDefault(symbol, []);
    }

    public ImmutableArray<LuaCodeReference> FindCodeReferences(TextSpan span) =>
        _codeBySpan.GetValueOrDefault(span, []);

    public LuaCodeReference? FindCodeReferenceAt(int bytePosition)
    {
        LunilGuard.NotNegative(bytePosition);
#if NET10_0_OR_GREATER
        ArgumentOutOfRangeException.ThrowIfGreaterThan(bytePosition, _model.Syntax.Source.Length);
#else
        if (bytePosition > _model.Syntax.Source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(bytePosition));
        }
#endif

        var references = _model.UnifiedReferences;
        var low = 0;
        var high = references.Length - 1;
        var lastStart = -1;
        while (low <= high)
        {
            var middle = low + ((high - low) / 2);
            if (references[middle].Span.Start <= bytePosition)
            {
                lastStart = middle;
                low = middle + 1;
            }
            else
            {
                high = middle - 1;
            }
        }

        LuaCodeReference? best = null;
        for (var index = lastStart; index >= 0; index--)
        {
            var candidate = references[index];
            if (best is not null && candidate.Span.Start < best.Span.Start)
            {
                break;
            }

            var contains = candidate.Span.Length == 0
                ? candidate.Span.Start == bytePosition
                : candidate.Span.Start <= bytePosition && bytePosition < candidate.Span.End;
            if (contains && (best is null || candidate.Span.Length < best.Span.Length))
            {
                best = candidate;
            }
        }

        return best;
    }

    public LuaFunctionInfo GetContainingFunction(TextSpan span) => _functions.Find(span);

    private void EnsureSymbol(LuaSymbol symbol)
    {
        LunilGuard.NotNull(symbol);
        if (!_model.Symbols.Any(candidate => ReferenceEquals(candidate, symbol)))
        {
            throw new ArgumentException(
                "The symbol does not belong to this semantic model.",
                nameof(symbol));
        }
    }

    private sealed class FunctionInterval
    {
        private readonly LuaFunctionInfo _function;
        private ImmutableArray<FunctionInterval> _children;

        private FunctionInterval(LuaFunctionInfo function)
        {
            _function = function;
        }

        public static FunctionInterval Create(ImmutableArray<LuaFunctionInfo> functions)
        {
            var ordered = functions
                .OrderBy(static function => function.Span.Start)
                .ThenByDescending(static function => function.Span.End)
                .ThenBy(static function => function.Id)
                .ToArray();
            if (ordered.Length == 0)
            {
                throw new ArgumentException("A semantic model must contain its main function.", nameof(functions));
            }

            var root = new MutableInterval(ordered[0]);
            var stack = new Stack<MutableInterval>();
            stack.Push(root);
            foreach (var function in ordered.Skip(1))
            {
                while (stack.Count > 1 && !Contains(stack.Peek().Function.Span, function.Span))
                {
                    stack.Pop();
                }

                var parent = stack.Peek();
                var child = new MutableInterval(function);
                parent.Children.Add(child);
                stack.Push(child);
            }

            return Freeze(root);
        }

        public LuaFunctionInfo Find(TextSpan span)
        {
            if (!Contains(_function.Span, span))
            {
                throw new ArgumentOutOfRangeException(nameof(span), "The span is outside the syntax snapshot.");
            }

            var current = this;
            while (!current._children.IsDefaultOrEmpty)
            {
                var low = 0;
                var high = current._children.Length - 1;
                var candidateIndex = -1;
                while (low <= high)
                {
                    var middle = low + ((high - low) / 2);
                    if (current._children[middle]._function.Span.Start <= span.Start)
                    {
                        candidateIndex = middle;
                        low = middle + 1;
                    }
                    else
                    {
                        high = middle - 1;
                    }
                }

                if (candidateIndex < 0 ||
                    !Contains(current._children[candidateIndex]._function.Span, span))
                {
                    break;
                }

                current = current._children[candidateIndex];
            }

            return current._function;
        }

        private static FunctionInterval Freeze(MutableInterval mutable)
        {
            var result = new FunctionInterval(mutable.Function);
            result._children = mutable.Children
                .Select(Freeze)
                .OrderBy(static child => child._function.Span.Start)
                .ThenByDescending(static child => child._function.Span.End)
                .ToImmutableArray();
            return result;
        }

        private static bool Contains(TextSpan outer, TextSpan inner) =>
            outer.Start <= inner.Start && outer.End >= inner.End;

        private sealed class MutableInterval(LuaFunctionInfo function)
        {
            public LuaFunctionInfo Function { get; } = function;

            public List<MutableInterval> Children { get; } = [];
        }
    }
}

internal static class LuaSemanticReferenceIndexCache
{
    private static readonly ConditionalWeakTable<LuaSemanticModel, LuaSemanticReferenceIndex> Cache = new();

    public static LuaSemanticReferenceIndex Get(LuaSemanticModel model) =>
        Cache.GetValue(model, static candidate => new LuaSemanticReferenceIndex(candidate));
}

public partial record LuaSemanticModel
{
    /// <summary>Finds all reads and writes bound to a symbol in this semantic snapshot.</summary>
    public ImmutableArray<LuaNameReference> FindReferences(LuaSymbol symbol) =>
        LuaSemanticReferenceIndexCache.Get(this).FindReferences(symbol);

    /// <summary>Finds all references to a global name, including implicit _ENV-backed references.</summary>
    public ImmutableArray<LuaNameReference> FindGlobalReferences(string name)
    {
        LunilGuard.NotNullOrWhiteSpace(name);
        return LuaSemanticReferenceIndexCache.Get(this).FindGlobalReferences(name);
    }

    /// <summary>Finds unified lexical references bound to a symbol.</summary>
    public ImmutableArray<LuaCodeReference> FindCodeReferences(LuaSymbol symbol) =>
        LuaSemanticReferenceIndexCache.Get(this).FindCodeReferences(symbol);

    /// <summary>Finds unified references with an exact source span.</summary>
    public ImmutableArray<LuaCodeReference> FindCodeReferences(TextSpan span) =>
        LuaSemanticReferenceIndexCache.Get(this).FindCodeReferences(span);

    /// <summary>Finds the narrowest unified reference containing a UTF-8 byte position.</summary>
    public LuaCodeReference? FindCodeReferenceAt(int bytePosition) =>
        LuaSemanticReferenceIndexCache.Get(this).FindCodeReferenceAt(bytePosition);

    /// <summary>Finds the innermost function containing a source span.</summary>
    public LuaFunctionInfo GetContainingFunction(TextSpan span) =>
        LuaSemanticReferenceIndexCache.Get(this).GetContainingFunction(span);
}
