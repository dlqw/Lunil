using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Semantics.Binding;

/// <summary>
/// Stable, serialized identity for a declaration in one logical Lua module. The value is suitable
/// for persistence and intentionally does not contain source offsets or compilation-local IDs.
/// </summary>
public readonly record struct LuaSymbolKey
{
    private const string Prefix = "lunil-symbol-v1|";

    public LuaSymbolKey(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!IsValid(value))
        {
            throw new ArgumentException("The symbol key has an unsupported format.", nameof(value));
        }

        Value = value;
    }

    /// <summary>Gets the canonical serialized key.</summary>
    public string Value { get; }

    /// <summary>Parses a previously serialized symbol key.</summary>
    public static bool TryParse(string? value, out LuaSymbolKey key)
    {
        if (value is not null && IsValid(value))
        {
            key = new LuaSymbolKey(value);
            return true;
        }

        key = default;
        return false;
    }

    /// <summary>Creates a declaration key from logical module, owner, name, and ambiguity data.</summary>
    public static LuaSymbolKey CreateSymbol(
        string moduleIdentity,
        LuaSymbolKind declarationKind,
        string lexicalOwner,
        string normalizedName,
        int ambiguityOrdinal = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(lexicalOwner);
        return Create(
            "symbol",
            moduleIdentity,
            declarationKind.ToString(),
            lexicalOwner,
            normalizedName,
            ambiguityOrdinal);
    }

    /// <summary>Creates a stable function key.</summary>
    public static LuaSymbolKey CreateFunction(
        string moduleIdentity,
        string declarationKind,
        string lexicalOwner,
        string normalizedName,
        int ambiguityOrdinal = 0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationKind);
        ArgumentNullException.ThrowIfNull(lexicalOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedName);
        if (declarationKind == "main")
        {
            if (lexicalOwner.Length != 0 || normalizedName != "main" || ambiguityOrdinal != 0)
            {
                throw new ArgumentException(
                    "The main function key must use an empty owner, the name 'main', and ordinal zero.",
                    nameof(lexicalOwner));
            }
        }
        else
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(lexicalOwner);
        }

        return Create(
            "function",
            moduleIdentity,
            declarationKind,
            lexicalOwner,
            normalizedName,
            ambiguityOrdinal);
    }

    /// <summary>Creates a stable EmmyLua/LuaLS class, alias, or enum key.</summary>
    public static LuaSymbolKey CreateAnnotation(
        string moduleIdentity,
        string annotationKind,
        string normalizedName,
        int ambiguityOrdinal = 0)
    {
        if (annotationKind is not ("class" or "alias" or "enum"))
        {
            throw new ArgumentException(
                "The annotation kind must be 'class', 'alias', or 'enum'.",
                nameof(annotationKind));
        }

        return Create(
            "annotation",
            moduleIdentity,
            annotationKind,
            string.Empty,
            normalizedName,
            ambiguityOrdinal);
    }

    /// <inheritdoc />
    public override string ToString() => Value ?? string.Empty;

    private static LuaSymbolKey Create(
        string category,
        string moduleIdentity,
        string declarationKind,
        string lexicalOwner,
        string normalizedName,
        int ambiguityOrdinal)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleIdentity);
        ArgumentException.ThrowIfNullOrWhiteSpace(declarationKind);
        ArgumentNullException.ThrowIfNull(lexicalOwner);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedName);
        ArgumentOutOfRangeException.ThrowIfNegative(ambiguityOrdinal);
        return new LuaSymbolKey(
            string.Join(
                '|',
                Prefix.TrimEnd('|'),
                category,
                Escape(moduleIdentity),
                Escape(declarationKind),
                Escape(lexicalOwner),
                Escape(normalizedName),
                ambiguityOrdinal.ToString(CultureInfo.InvariantCulture)));
    }

    private static string Escape(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Uri.EscapeDataString(value.Normalize());
    }

    private static bool IsValid(string value)
    {
        if (!value.StartsWith(Prefix, StringComparison.Ordinal) ||
            value.Split('|') is not { Length: 7 } parts ||
            parts[1] is not ("symbol" or "function" or "annotation") ||
            !IsCanonicalEscaped(parts[2], allowEmpty: false) ||
            !IsCanonicalEscaped(parts[3], allowEmpty: false) ||
            !IsCanonicalEscaped(parts[4], allowEmpty: true) ||
            !IsCanonicalEscaped(parts[5], allowEmpty: false) ||
            !int.TryParse(
                parts[6],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var ordinal) ||
            ordinal < 0 ||
            !string.Equals(
                ordinal.ToString(CultureInfo.InvariantCulture),
                parts[6],
                StringComparison.Ordinal))
        {
            return false;
        }

        var declarationKind = Uri.UnescapeDataString(parts[3]);
        return parts[1] switch
        {
            "symbol" => !string.IsNullOrEmpty(parts[4]) &&
                Enum.TryParse<LuaSymbolKind>(declarationKind, out var symbolKind) &&
                string.Equals(symbolKind.ToString(), declarationKind, StringComparison.Ordinal),
            "function" => declarationKind == "main"
                ? string.IsNullOrEmpty(parts[4])
                : !string.IsNullOrEmpty(parts[4]),
            "annotation" => string.IsNullOrEmpty(parts[4]) &&
                declarationKind is "class" or "alias" or "enum",
            _ => false,
        };
    }

    private static bool IsCanonicalEscaped(string value, bool allowEmpty)
    {
        if (!allowEmpty && string.IsNullOrEmpty(value))
        {
            return false;
        }

        var decoded = Uri.UnescapeDataString(value);
        return string.Equals(Escape(decoded), value, StringComparison.Ordinal);
    }
}

/// <summary>Computes and resolves stable keys for one semantic snapshot.</summary>
internal sealed class LuaStableSymbolIndex
{
    private readonly LuaSemanticModel _model;
    private readonly string _moduleIdentity;
    private readonly ImmutableDictionary<int, FunctionPath> _functionPaths;
    private readonly ImmutableDictionary<int, LuaSymbolKey> _functionKeys;
    private readonly ImmutableDictionary<LuaSymbolKey, int> _functionIdsByKey;
    private readonly ImmutableDictionary<int, LuaSymbolKey> _symbolKeys;
    private readonly ImmutableDictionary<LuaSymbolKey, int> _symbolIdsByKey;
    private readonly ImmutableDictionary<int, LuaFunctionInfo> _functionsById;
    private readonly ImmutableDictionary<int, LuaSymbol> _symbolsById;

    public LuaStableSymbolIndex(LuaSemanticModel model, string moduleIdentity)
    {
        _model = model;
        _moduleIdentity = NormalizeModuleIdentity(moduleIdentity);
        (_functionPaths, _functionKeys) = BuildFunctionPaths(
            model.Syntax.Root,
            model.Syntax.Source,
            _moduleIdentity);
        _symbolKeys = BuildSymbolKeys();
        _functionsById = model.Functions.ToImmutableDictionary(static function => function.Id);
        _symbolsById = model.Symbols.ToImmutableDictionary(static symbol => symbol.Id);
        _functionIdsByKey = _functionKeys.ToImmutableDictionary(
            static pair => pair.Value,
            static pair => pair.Key);
        _symbolIdsByKey = _symbolKeys.ToImmutableDictionary(
            static pair => pair.Value,
            static pair => pair.Key);
    }

    public LuaSymbolKey GetSymbolKey(LuaSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        return _symbolsById.TryGetValue(symbol.Id, out var candidate) &&
            ReferenceEquals(candidate, symbol) &&
            _symbolKeys.TryGetValue(symbol.Id, out var key)
            ? key
            : throw new ArgumentException("The symbol does not belong to this semantic model.", nameof(symbol));
    }

    public LuaSymbolKey GetFunctionKey(LuaFunctionInfo function)
    {
        ArgumentNullException.ThrowIfNull(function);
        return _functionsById.TryGetValue(function.Id, out var candidate) &&
            ReferenceEquals(candidate, function) &&
            _functionKeys.TryGetValue(function.Id, out var key)
            ? key
            : throw new ArgumentException("The function does not belong to this semantic model.", nameof(function));
    }

    public LuaSymbol? ResolveSymbolKey(LuaSymbolKey key)
    {
        return _symbolIdsByKey.TryGetValue(key, out var id) &&
            _symbolsById.TryGetValue(id, out var symbol)
            ? symbol
            : null;
    }

    public LuaFunctionInfo? ResolveFunctionKey(LuaSymbolKey key)
    {
        return _functionIdsByKey.TryGetValue(key, out var id) &&
            _functionsById.TryGetValue(id, out var function)
            ? function
            : null;
    }

    private ImmutableDictionary<int, LuaSymbolKey> BuildSymbolKeys()
    {
        var groups = _model.Symbols
            .GroupBy(static symbol => (symbol.FunctionId, symbol.Kind, symbol.Name, symbol.ScopeDepth))
            .ToDictionary(
                static group => group.Key,
                static group => group.OrderBy(symbol => symbol.DeclaringSpan.Start).ThenBy(symbol => symbol.Id).ToArray());
        var result = ImmutableDictionary.CreateBuilder<int, LuaSymbolKey>();
        foreach (var symbol in _model.Symbols)
        {
            if (!_functionPaths.TryGetValue(symbol.FunctionId, out var functionPath))
            {
                continue;
            }

            var group = groups[(symbol.FunctionId, symbol.Kind, symbol.Name, symbol.ScopeDepth)];
            var ordinal = group.Length > 1
                ? Array.IndexOf(group, symbol)
                : 0;
            result[symbol.Id] = LuaSymbolKey.CreateSymbol(
                _moduleIdentity,
                symbol.Kind,
                functionPath.Value + "/scope:" + symbol.ScopeDepth.ToString(CultureInfo.InvariantCulture),
                symbol.Name,
                ordinal);
        }

        return result.ToImmutable();
    }

    private static (ImmutableDictionary<int, FunctionPath>, ImmutableDictionary<int, LuaSymbolKey>)
        BuildFunctionPaths(LuaSyntaxNode root, SourceText source, string moduleIdentity)
    {
        var paths = ImmutableDictionary.CreateBuilder<int, FunctionPath>();
        var keys = ImmutableDictionary.CreateBuilder<int, LuaSymbolKey>();
        var siblingOrdinals = new Dictionary<(string Parent, string Kind, string Name), int>();
        paths[0] = new FunctionPath("main");
        keys[0] = LuaSymbolKey.CreateFunction(moduleIdentity, "main", string.Empty, "main");
        var nextId = 1;

        void Visit(LuaSyntaxNode node, FunctionPath parent)
        {
            if (node.TryGetFunctionDeclaration(out var declaration))
            {
                var kind = node.Kind.ToString();
                var name = GetFunctionName(declaration, source);
                var ordinalKey = (parent.Value, kind, name);
                siblingOrdinals.TryGetValue(ordinalKey, out var ordinal);
                siblingOrdinals[ordinalKey] = ordinal + 1;
                var path = new FunctionPath(
                    parent.Value + "/" + kind + ":" + name + ":" + ordinal.ToString(CultureInfo.InvariantCulture));
                var id = nextId++;
                paths[id] = path;
                keys[id] = LuaSymbolKey.CreateFunction(
                    moduleIdentity,
                    kind,
                    parent.Value,
                    name,
                    ordinal);
                foreach (var child in node.ChildNodes())
                {
                    Visit(child, path);
                }

                return;
            }

            foreach (var child in node.ChildNodes())
            {
                Visit(child, parent);
            }
        }

        Visit(root, paths[0]);
        return (paths.ToImmutable(), keys.ToImmutable());
    }

    private static string GetFunctionName(
        LuaFunctionDeclarationSyntax declaration,
        SourceText source)
    {
        if (declaration.Name is null)
        {
            return "<anonymous>";
        }

        var name = new StringBuilder();
        foreach (var token in declaration.Name.Node.ChildTokens())
        {
            switch (token.Kind)
            {
                case LuaTokenKind.Identifier:
                    name.Append(Encoding.UTF8.GetString(source.GetSpan(token.Span)).Normalize());
                    break;
                case LuaTokenKind.Dot:
                    name.Append('.');
                    break;
                case LuaTokenKind.Colon:
                    name.Append(':');
                    break;
            }
        }

        return name.Length > 0 ? name.ToString() : "<missing>";
    }

    private static string NormalizeModuleIdentity(string moduleIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleIdentity);
        return moduleIdentity.Trim().Replace('\\', '/').Normalize();
    }

    private readonly record struct FunctionPath(string Value);
}

internal static class LuaStableSymbolIndexCache
{
    private static readonly ConditionalWeakTable<
        LuaSemanticModel,
        ConcurrentDictionary<string, LuaStableSymbolIndex>> Cache = new();

    public static LuaStableSymbolIndex Get(LuaSemanticModel model, string moduleIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleIdentity);
        var normalized = moduleIdentity.Trim().Replace('\\', '/').Normalize();
        var byModule = Cache.GetValue(
            model,
            static _ => new ConcurrentDictionary<string, LuaStableSymbolIndex>(StringComparer.Ordinal));
        return byModule.GetOrAdd(
            normalized,
            identity => new LuaStableSymbolIndex(model, identity));
    }
}

public partial record LuaSemanticModel
{
    /// <summary>Gets a stable key for a symbol using a logical module identity.</summary>
    public LuaSymbolKey GetSymbolKey(LuaSymbol symbol, string moduleIdentity) =>
        LuaStableSymbolIndexCache.Get(this, moduleIdentity).GetSymbolKey(symbol);

    /// <summary>Gets a stable key for a function using a logical module identity.</summary>
    public LuaSymbolKey GetFunctionKey(LuaFunctionInfo function, string moduleIdentity) =>
        LuaStableSymbolIndexCache.Get(this, moduleIdentity).GetFunctionKey(function);

    /// <summary>Resolves a serialized symbol key in this snapshot and logical module.</summary>
    public LuaSymbol? ResolveSymbolKey(LuaSymbolKey key, string moduleIdentity) =>
        LuaStableSymbolIndexCache.Get(this, moduleIdentity).ResolveSymbolKey(key);

    /// <summary>Resolves a serialized function key in this snapshot and logical module.</summary>
    public LuaFunctionInfo? ResolveFunctionKey(LuaSymbolKey key, string moduleIdentity) =>
        LuaStableSymbolIndexCache.Get(this, moduleIdentity).ResolveFunctionKey(key);
}
