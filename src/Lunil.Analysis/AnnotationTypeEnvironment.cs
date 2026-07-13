using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using Lunil.Core.Text;
using Lunil.EmmyLua;

namespace Lunil.Analysis;

internal sealed class AnnotationTypeEnvironment
{
    private readonly LuaAnnotationDocument _document;
    private readonly LuaAnalysisContext _context;
    private readonly Dictionary<string, RawDeclaration> _rawDeclarations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, LuaTypeDeclaration> _declarations =
        new(StringComparer.Ordinal);
    private readonly HashSet<string> _resolving = new(StringComparer.Ordinal);

    public AnnotationTypeEnvironment(
        LuaAnnotationDocument document,
        LuaAnalysisContext context)
    {
        _document = document;
        _context = context;
        CollectRawDeclarations();
        foreach (var name in _rawDeclarations.Keys.Order(StringComparer.Ordinal))
        {
            ResolveDeclaration(name);
        }

        Declarations = _declarations.Values
            .OrderBy(static declaration => declaration.Span.Start)
            .ThenBy(static declaration => declaration.Name, StringComparer.Ordinal)
            .ToImmutableArray();
        Relations = new LuaTypeRelations(Declarations, context.Options.MaximumUnionMemberCount);
    }

    public ImmutableArray<LuaTypeDeclaration> Declarations { get; }

    public LuaTypeRelations Relations { get; }

    public LuaType Resolve(
        LuaTypeSyntax syntax,
        IReadOnlyDictionary<string, LuaType>? typeParameters = null,
        string? selfClass = null) => Resolve(syntax, typeParameters, selfClass, depth: 1);

    public LuaType? ResolveNamedDeclaration(string name)
    {
        if (!_declarations.TryGetValue(name, out var declaration))
        {
            declaration = ResolveDeclaration(name);
        }

        return declaration switch
        {
            LuaClassDeclaration @class => new LuaClassType(@class.Name, []),
            LuaAliasDeclaration alias => new LuaAliasType(alias.Name, alias.Target),
            LuaEnumDeclaration @enum => new LuaEnumType(
                @enum.Name,
                @enum.UnderlyingType,
                @enum.Members),
            _ => null,
        };
    }

    private LuaType Resolve(
        LuaTypeSyntax syntax,
        IReadOnlyDictionary<string, LuaType>? typeParameters,
        string? selfClass,
        int depth)
    {
        if (!_context.TryCreateType(syntax.Span, depth))
        {
            return LuaTypes.Unknown;
        }

        return syntax switch
        {
            LuaNamedTypeSyntax named => ResolveNamed(named, typeParameters, selfClass, depth),
            LuaLiteralTypeSyntax literal => ResolveLiteral(literal),
            LuaUnionTypeSyntax union => new LuaTypeRelations(
                _declarations.Values,
                _context.Options.MaximumUnionMemberCount).Union(
                    union.Types.Select(member => Resolve(member, typeParameters, selfClass, depth + 1))),
            LuaIntersectionTypeSyntax intersection => LuaTypeRelations.Intersection(
                    intersection.Types.Select(member => Resolve(member, typeParameters, selfClass, depth + 1))),
            LuaNullableTypeSyntax nullable => new LuaTypeRelations(
                _declarations.Values,
                _context.Options.MaximumUnionMemberCount).Union(
                    Resolve(nullable.Type, typeParameters, selfClass, depth + 1),
                    LuaTypes.Nil),
            LuaArrayTypeSyntax array => new LuaArrayType(
                Resolve(array.ElementType, typeParameters, selfClass, depth + 1)),
            LuaTupleTypeSyntax tuple => new LuaTupleType(
                [.. tuple.Elements.Select(element => Resolve(
                    element,
                    typeParameters,
                    selfClass,
                    depth + 1))]),
            LuaVarargTypeSyntax vararg => new LuaTypePack(
                [],
                vararg.ElementType is null
                    ? LuaTypes.Any
                    : Resolve(vararg.ElementType, typeParameters, selfClass, depth + 1)),
            LuaFunctionTypeSyntax function => ResolveFunction(
                function,
                typeParameters,
                selfClass,
                depth + 1),
            LuaTableTypeSyntax table => ResolveTable(
                table,
                typeParameters,
                selfClass,
                depth + 1),
            _ => LuaTypes.Unknown,
        };
    }

    private LuaType ResolveNamed(
        LuaNamedTypeSyntax named,
        IReadOnlyDictionary<string, LuaType>? typeParameters,
        string? selfClass,
        int depth)
    {
        if (typeParameters is not null && typeParameters.TryGetValue(named.Name, out var parameter))
        {
            return parameter;
        }

        if (string.Equals(named.Name, "self", StringComparison.OrdinalIgnoreCase) && selfClass is not null)
        {
            return new LuaClassType(selfClass, []);
        }

        var arguments = named.TypeArguments
            .Select(argument => Resolve(argument, typeParameters, selfClass, depth + 1))
            .ToImmutableArray();
        if (selfClass is not null && string.Equals(named.Name, selfClass, StringComparison.Ordinal))
        {
            return new LuaClassType(selfClass, arguments);
        }

        var normalized = named.Name.ToLowerInvariant();
        LuaType? builtIn = normalized switch
        {
            "any" => LuaTypes.Any,
            "unknown" => LuaTypes.Unknown,
            "never" => LuaTypes.Never,
            "nil" => LuaTypes.Nil,
            "bool" or "boolean" => LuaTypes.Boolean,
            "int" or "integer" => LuaTypes.Integer,
            "float" => LuaTypes.Float,
            "number" => LuaTypes.Number,
            "str" or "string" => LuaTypes.String,
            "table" when arguments.Length == 2 => new LuaMapType(arguments[0], arguments[1]),
            "table" => LuaTypes.Table,
            "function" => LuaTypes.Function,
            "thread" => LuaTypes.Thread,
            "userdata" or "lightuserdata" => LuaTypes.Userdata,
            _ => null,
        };
        if (builtIn is not null)
        {
            return builtIn;
        }

        if (!_rawDeclarations.ContainsKey(named.Name))
        {
            _context.AddDiagnostic(
                "LUA6001",
                named.Span,
                $"Unknown annotation type '{named.Name}'.");
            return LuaTypes.Unknown;
        }

        var declaration = ResolveDeclaration(named.Name);
        return declaration switch
        {
            LuaClassDeclaration @class => new LuaClassType(@class.Name, arguments),
            LuaAliasDeclaration alias when arguments.IsEmpty => new LuaAliasType(alias.Name, alias.Target),
            LuaAliasDeclaration alias when alias.TypeParameters.Length == arguments.Length &&
                _context.TryInstantiateGeneric(named.Span) => new LuaAliasType(
                    alias.Name,
                    new LuaTypeRelations(
                        _declarations.Values,
                        _context.Options.MaximumUnionMemberCount).Substitute(
                        alias.Target,
                        alias.TypeParameters.Zip(arguments).ToDictionary(
                            static pair => pair.First.Name,
                            static pair => pair.Second,
                            StringComparer.Ordinal))),
            LuaEnumDeclaration @enum => new LuaEnumType(
                @enum.Name,
                @enum.UnderlyingType,
                @enum.Members),
            _ => LuaTypes.Unknown,
        };
    }

    private static LuaType ResolveLiteral(LuaLiteralTypeSyntax literal) => literal.Kind switch
    {
        LuaTypeLiteralKind.Nil => LuaTypes.Nil,
        LuaTypeLiteralKind.Boolean => new LuaBooleanLiteralType(
            string.Equals(literal.Text, "true", StringComparison.OrdinalIgnoreCase)),
        LuaTypeLiteralKind.Number when long.TryParse(
            literal.Text,
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var integer) => new LuaIntegerLiteralType(integer),
        LuaTypeLiteralKind.Number when double.TryParse(
            literal.Text,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var number) => new LuaFloatLiteralType(number),
        LuaTypeLiteralKind.Number => LuaTypes.Number,
        LuaTypeLiteralKind.Text => new LuaStringLiteralType(
            Encoding.UTF8.GetBytes(literal.Text).ToImmutableArray()),
        _ => LuaTypes.Unknown,
    };

    private LuaFunctionType ResolveFunction(
        LuaFunctionTypeSyntax function,
        IReadOnlyDictionary<string, LuaType>? typeParameters,
        string? selfClass,
        int depth)
    {
        var parameters = function.Parameters.Select(parameter => new LuaFunctionParameter(
            parameter.Name,
            Resolve(parameter.Type, typeParameters, selfClass, depth + 1),
            parameter.IsOptional,
            parameter.IsVararg)).ToImmutableArray();
        var returns = new LuaTypePack(
            [.. function.Returns.Select(type => Resolve(
                type,
                typeParameters,
                selfClass,
                depth + 1))]);
        return new LuaFunctionType(parameters, returns, []);
    }

    private LuaStructuralTableType ResolveTable(
        LuaTableTypeSyntax table,
        IReadOnlyDictionary<string, LuaType>? typeParameters,
        string? selfClass,
        int depth)
    {
        var fields = table.Fields.Select(field => new LuaTableField(
            field.Name,
            field.KeyType is null
                ? null
                : Resolve(field.KeyType, typeParameters, selfClass, depth + 1),
            Resolve(field.ValueType, typeParameters, selfClass, depth + 1),
            field.IsOptional)).ToImmutableArray();
        var arrayElements = fields.Where(static field => field.Name is null && field.KeyType is null)
            .Select(static field => field.ValueType)
            .ToArray();
        var mapFields = fields.Where(static field => field.KeyType is not null).ToArray();
        var relations = new LuaTypeRelations(
            _declarations.Values,
            _context.Options.MaximumUnionMemberCount);
        return new LuaStructuralTableType(
            fields,
            arrayElements.Length == 0 ? null : relations.Union(arrayElements),
            mapFields.Length == 0 ? null : relations.Union(mapFields.Select(static field => field.KeyType!)),
            mapFields.Length == 0 ? null : relations.Union(mapFields.Select(static field => field.ValueType)));
    }

    private void CollectRawDeclarations()
    {
        RawDeclaration? current = null;
        var previousEnd = 0;
        foreach (var annotation in _document.Annotations)
        {
            if (current is not null && ContainsCode(previousEnd, annotation.Span.Start))
            {
                current = null;
            }

            switch (annotation)
            {
                case LuaClassAnnotationSyntax @class:
                    current = AddRaw(new RawDeclaration(@class.Name, @class, @class.Span));
                    break;
                case LuaAliasAnnotationSyntax alias:
                    current = AddRaw(new RawDeclaration(alias.Name, alias, alias.Span));
                    break;
                case LuaEnumAnnotationSyntax @enum:
                    current = AddRaw(new RawDeclaration(@enum.Name, @enum, @enum.Span));
                    break;
                case LuaFieldAnnotationSyntax field when current?.Root is LuaClassAnnotationSyntax:
                    current.Extras.Add(field);
                    break;
                case LuaOperatorAnnotationSyntax @operator when current?.Root is LuaClassAnnotationSyntax:
                    current.Extras.Add(@operator);
                    break;
                case LuaOverloadAnnotationSyntax overload when current?.Root is LuaClassAnnotationSyntax:
                    current.Extras.Add(overload);
                    break;
                case LuaAliasContinuationAnnotationSyntax continuation when
                    current?.Root is LuaAliasAnnotationSyntax or LuaEnumAnnotationSyntax:
                    current.Extras.Add(continuation);
                    break;
                case LuaGenericAnnotationSyntax generic when current?.Root is LuaAliasAnnotationSyntax:
                    current.Extras.Add(generic);
                    break;
                case LuaMarkerAnnotationSyntax:
                    break;
                default:
                    current = null;
                    break;
            }

            previousEnd = annotation.Span.End;
        }
    }

    private bool ContainsCode(int start, int end)
    {
        var bytes = _document.Source.AsSpan()[start..end];
        foreach (var value in bytes)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return true;
            }
        }

        return false;
    }

    private RawDeclaration AddRaw(RawDeclaration declaration)
    {
        if (_rawDeclarations.TryGetValue(declaration.Name, out var previous))
        {
            _context.AddDiagnostic(
                "LUA6002",
                declaration.Span,
                $"Type '{declaration.Name}' is already declared at byte {previous.Span.Start}.");
            return previous;
        }

        _rawDeclarations.Add(declaration.Name, declaration);
        return declaration;
    }

    private LuaTypeDeclaration? ResolveDeclaration(string name)
    {
        if (_declarations.TryGetValue(name, out var existing))
        {
            return existing;
        }

        if (!_rawDeclarations.TryGetValue(name, out var raw))
        {
            return null;
        }

        if (!_resolving.Add(name))
        {
            _context.AddDiagnostic(
                "LUA6012",
                raw.Span,
                $"Recursive type declaration '{name}' was widened to unknown.");
            return null;
        }

        try
        {
            LuaTypeDeclaration? declaration = raw.Root switch
            {
                LuaClassAnnotationSyntax @class => ResolveClass(@class, raw.Extras),
                LuaAliasAnnotationSyntax alias => ResolveAlias(alias, raw.Extras),
                LuaEnumAnnotationSyntax @enum => ResolveEnum(@enum, raw.Extras),
                _ => null,
            };
            if (declaration is not null)
            {
                _declarations[name] = declaration;
            }

            return declaration;
        }
        finally
        {
            _resolving.Remove(name);
        }
    }

    private LuaClassDeclaration ResolveClass(
        LuaClassAnnotationSyntax syntax,
        List<LuaAnnotationSyntax> extras)
    {
        var parameters = syntax.TypeParameters.Select((name, index) =>
            new LuaGenericParameterType(name, index)).ToImmutableArray();
        var parameterMap = parameters.ToDictionary(
            static parameter => parameter.Name,
            static parameter => (LuaType)parameter,
            StringComparer.Ordinal);
        var bases = syntax.BaseTypes.Select(type => Resolve(
            type,
            parameterMap,
            syntax.Name,
            depth: 1)).ToImmutableArray();
        var fields = extras.OfType<LuaFieldAnnotationSyntax>().Select(field => new LuaTableField(
            field.Name,
            null,
            Resolve(field.Type, parameterMap, syntax.Name, depth: 1),
            field.IsOptional,
            IsReadOnly: false)).ToImmutableArray();
        var calls = extras.OfType<LuaOverloadAnnotationSyntax>()
            .Select(overload => ResolveFunction(
                overload.Type,
                parameterMap,
                syntax.Name,
                depth: 1))
            .ToImmutableArray();
        var operators = extras.OfType<LuaOperatorAnnotationSyntax>()
            .ToImmutableDictionary(
                static item => item.Operator,
                item => new LuaFunctionType(
                    item.OperandType is null
                        ? []
                        : [new LuaFunctionParameter(
                            "other",
                            Resolve(item.OperandType, parameterMap, syntax.Name, depth: 1))],
                    new LuaTypePack([Resolve(item.ResultType, parameterMap, syntax.Name, depth: 1)]),
                    parameters),
                StringComparer.Ordinal);
        return new LuaClassDeclaration(
            syntax.Name,
            parameters,
            bases,
            fields,
            calls,
            operators,
            syntax.Span);
    }

    private LuaAliasDeclaration ResolveAlias(
        LuaAliasAnnotationSyntax syntax,
        List<LuaAnnotationSyntax> extras)
    {
        var generic = extras.OfType<LuaGenericAnnotationSyntax>().LastOrDefault();
        var parameters = generic?.Parameters.Select((parameter, index) =>
            new LuaGenericParameterType(parameter.Name, index)).ToImmutableArray() ?? [];
        var parameterMap = parameters.ToDictionary(
            static parameter => parameter.Name,
            static parameter => (LuaType)parameter,
            StringComparer.Ordinal);
        var members = new List<LuaType>();
        if (syntax.Type is not null)
        {
            members.Add(Resolve(syntax.Type, parameterMap, selfClass: null, depth: 1));
        }

        members.AddRange(extras.OfType<LuaAliasContinuationAnnotationSyntax>()
            .Select(continuation => Resolve(
                continuation.Type,
                parameterMap,
                selfClass: null,
                depth: 1)));
        var target = members.Count == 0
            ? LuaTypes.Unknown
            : new LuaTypeRelations(
                _declarations.Values,
                _context.Options.MaximumUnionMemberCount).Union(members);
        return new LuaAliasDeclaration(syntax.Name, parameters, target, syntax.Span);
    }

    private LuaEnumDeclaration ResolveEnum(
        LuaEnumAnnotationSyntax syntax,
        List<LuaAnnotationSyntax> extras)
    {
        var underlying = syntax.KeyType is null
            ? LuaTypes.Integer
            : Resolve(syntax.KeyType, typeParameters: null, selfClass: null, depth: 1);
        var members = extras.OfType<LuaAliasContinuationAnnotationSyntax>()
            .Select(continuation => Resolve(
                continuation.Type,
                typeParameters: null,
                selfClass: null,
                depth: 1))
            .OfType<LuaLiteralType>()
            .ToImmutableArray();
        return new LuaEnumDeclaration(syntax.Name, underlying, members, syntax.Span);
    }

    private sealed class RawDeclaration(
        string name,
        LuaAnnotationSyntax root,
        TextSpan span)
    {
        public string Name { get; } = name;

        public LuaAnnotationSyntax Root { get; } = root;

        public TextSpan Span { get; } = span;

        public List<LuaAnnotationSyntax> Extras { get; } = [];
    }
}
