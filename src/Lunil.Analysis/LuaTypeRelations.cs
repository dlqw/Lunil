using System.Collections.Immutable;

namespace Lunil.Analysis;

/// <summary>Normalization, substitution, narrowing, and structural assignability operations.</summary>
public sealed class LuaTypeRelations
{
    private readonly ImmutableDictionary<string, LuaTypeDeclaration> _declarations;
    private readonly int _maximumUnionMemberCount;

    public LuaTypeRelations(
        IEnumerable<LuaTypeDeclaration>? declarations = null,
        int maximumUnionMemberCount = 32)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumUnionMemberCount);
        _maximumUnionMemberCount = maximumUnionMemberCount;
        _declarations = (declarations ?? [])
            .GroupBy(static declaration => declaration.Name, StringComparer.Ordinal)
            .ToImmutableDictionary(
                static group => group.Key,
                static group => group.Last(),
                StringComparer.Ordinal);
    }

    public LuaType Union(params ReadOnlySpan<LuaType> types) => Union((IEnumerable<LuaType>)types.ToArray());

    public LuaType Union(IEnumerable<LuaType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var result = new List<LuaType>();
        foreach (var type in types)
        {
            AddUnionMember(type, result);
            if (result.Any(static candidate => candidate.Kind == LuaTypeKind.Any))
            {
                return LuaTypes.Any;
            }
        }

        RemoveSubsumed(result);
        if (result.Count == 0)
        {
            return LuaTypes.Never;
        }

        if (result.Count == 1)
        {
            return result[0];
        }

        return result.Count > _maximumUnionMemberCount
            ? Widen(result)
            : new LuaUnionType(result
                .OrderBy(static type => type.DisplayName, StringComparer.Ordinal)
                .ToImmutableArray());
    }

    public static LuaType Intersection(IEnumerable<LuaType> types)
    {
        ArgumentNullException.ThrowIfNull(types);
        var result = new List<LuaType>();
        foreach (var type in types)
        {
            if (type.Kind == LuaTypeKind.Never)
            {
                return LuaTypes.Never;
            }

            if (type.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown)
            {
                continue;
            }

            if (type is LuaIntersectionType intersection)
            {
                foreach (var member in intersection.Types)
                {
                    AddUnique(member, result);
                }
            }
            else
            {
                AddUnique(type, result);
            }
        }

        return result.Count switch
        {
            0 => LuaTypes.Unknown,
            1 => result[0],
            _ => new LuaIntersectionType(result.ToImmutableArray()),
        };
    }

    public bool IsAssignable(LuaType source, LuaType target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);
        return IsAssignable(source, target, new HashSet<(LuaType Source, LuaType Target)>());
    }

    public LuaType Exclude(LuaType source, LuaType excluded)
    {
        if (source is LuaUnionType union)
        {
            return Union(union.Types.Where(member => !IsAssignable(member, excluded)));
        }

        return IsAssignable(source, excluded) ? LuaTypes.Never : source;
    }

    public LuaType NarrowTo(LuaType source, LuaType target)
    {
        if (source.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown)
        {
            return target;
        }

        if (source is LuaUnionType union)
        {
            return Union(union.Types.Where(member =>
                IsAssignable(member, target) || IsAssignable(target, member)));
        }

        return IsAssignable(source, target)
            ? source
            : IsAssignable(target, source)
                ? target
                : LuaTypes.Never;
    }

    public LuaType RemoveNil(LuaType source) => Exclude(source, LuaTypes.Nil);

    public LuaType TruthyPart(LuaType source) =>
        Exclude(Exclude(source, LuaTypes.Nil), new LuaBooleanLiteralType(false));

    public LuaType FalsyPart(LuaType source) =>
        NarrowTo(source, Union(LuaTypes.Nil, new LuaBooleanLiteralType(false)));

    public LuaType Substitute(
        LuaType type,
        IReadOnlyDictionary<string, LuaType> substitutions)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(substitutions);
        return type switch
        {
            LuaGenericParameterType parameter when substitutions.TryGetValue(parameter.Name, out var value) => value,
            LuaUnionType union => Union(union.Types.Select(member => Substitute(member, substitutions))),
            LuaIntersectionType intersection => Intersection(
                intersection.Types.Select(member => Substitute(member, substitutions))),
            LuaArrayType array => new LuaArrayType(Substitute(array.ElementType, substitutions)),
            LuaMapType map => new LuaMapType(
                Substitute(map.KeyType, substitutions),
                Substitute(map.ValueType, substitutions)),
            LuaStructuralTableType table => table with
            {
                Fields = [.. table.Fields.Select(field => field with
                {
                    KeyType = field.KeyType is null ? null : Substitute(field.KeyType, substitutions),
                    ValueType = Substitute(field.ValueType, substitutions),
                })],
                ArrayElementType = table.ArrayElementType is null
                    ? null
                    : Substitute(table.ArrayElementType, substitutions),
                MapKeyType = table.MapKeyType is null ? null : Substitute(table.MapKeyType, substitutions),
                MapValueType = table.MapValueType is null ? null : Substitute(table.MapValueType, substitutions),
            },
            LuaTupleType tuple => new LuaTupleType(
                [.. tuple.Elements.Select(element => Substitute(element, substitutions))]),
            LuaTypePack pack => new LuaTypePack(
                [.. pack.Head.Select(element => Substitute(element, substitutions))],
                pack.VariadicType is null ? null : Substitute(pack.VariadicType, substitutions)),
            LuaFunctionType function => function with
            {
                Parameters = [.. function.Parameters.Select(parameter => parameter with
                {
                    Type = Substitute(parameter.Type, substitutions),
                })],
                Returns = (LuaTypePack)Substitute(function.Returns, substitutions),
            },
            LuaOverloadType overload => new LuaOverloadType(
                [.. overload.Signatures.Select(signature =>
                    (LuaFunctionType)Substitute(signature, substitutions))]),
            LuaCallableType callable => new LuaCallableType(
                Substitute(callable.ReceiverType, substitutions),
                [.. callable.Signatures.Select(signature =>
                    (LuaFunctionType)Substitute(signature, substitutions))]),
            LuaClassType @class => new LuaClassType(
                @class.Name,
                [.. @class.TypeArguments.Select(argument => Substitute(argument, substitutions))]),
            LuaAliasType alias => new LuaAliasType(alias.Name, Substitute(alias.Target, substitutions)),
            LuaEnumType @enum => new LuaEnumType(
                @enum.Name,
                Substitute(@enum.UnderlyingType, substitutions),
                @enum.Members),
            _ => type,
        };
    }

    public LuaTableField? FindField(LuaType type, string name)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(name);
        return FindField(type, name, new HashSet<string>(StringComparer.Ordinal));
    }

    private bool IsAssignable(
        LuaType source,
        LuaType target,
        HashSet<(LuaType Source, LuaType Target)> visiting)
    {
        if (ReferenceEquals(source, target) || Equals(source, target) ||
            source.Kind is LuaTypeKind.Never or LuaTypeKind.Any ||
            target.Kind is LuaTypeKind.Any or LuaTypeKind.Unknown)
        {
            return true;
        }

        if (source.Kind == LuaTypeKind.Unknown)
        {
            return false;
        }

        if (!visiting.Add((source, target)))
        {
            return true;
        }

        try
        {
            if (source is LuaAliasType sourceAlias)
            {
                return IsAssignable(sourceAlias.Target, target, visiting);
            }

            if (target is LuaAliasType targetAlias)
            {
                return IsAssignable(source, targetAlias.Target, visiting);
            }

            if (source is LuaUnionType sourceUnion)
            {
                return sourceUnion.Types.All(member => IsAssignable(member, target, visiting));
            }

            if (target is LuaUnionType targetUnion)
            {
                return targetUnion.Types.Any(member => IsAssignable(source, member, visiting));
            }

            if (source is LuaIntersectionType sourceIntersection)
            {
                return sourceIntersection.Types.Any(member => IsAssignable(member, target, visiting));
            }

            if (target is LuaIntersectionType targetIntersection)
            {
                return targetIntersection.Types.All(member => IsAssignable(source, member, visiting));
            }

            if (source is LuaLiteralType literal && target is LuaLiteralType targetLiteral)
            {
                return Equivalent(literal, targetLiteral);
            }

            if (source is LuaLiteralType sourceLiteral)
            {
                var baseKind = LiteralBaseKind(sourceLiteral);
                return baseKind == target.Kind ||
                    baseKind is LuaTypeKind.Integer or LuaTypeKind.Float &&
                    target.Kind == LuaTypeKind.Number;
            }

            if (source.Kind is LuaTypeKind.Integer or LuaTypeKind.Float && target.Kind == LuaTypeKind.Number)
            {
                return true;
            }

            if (source is LuaArrayType sourceArray)
            {
                return target.Kind == LuaTypeKind.Table ||
                    target is LuaArrayType targetArray &&
                    IsAssignable(sourceArray.ElementType, targetArray.ElementType, visiting) ||
                    target is LuaMapType targetMap &&
                    IsAssignable(LuaTypes.Integer, targetMap.KeyType, visiting) &&
                    IsAssignable(sourceArray.ElementType, targetMap.ValueType, visiting);
            }

            if (source is LuaMapType sourceMap)
            {
                return target.Kind == LuaTypeKind.Table ||
                    target is LuaMapType targetMap &&
                    IsAssignable(sourceMap.KeyType, targetMap.KeyType, visiting) &&
                    IsAssignable(sourceMap.ValueType, targetMap.ValueType, visiting);
            }

            if (source is LuaStructuralTableType sourceTable)
            {
                if (target.Kind == LuaTypeKind.Table)
                {
                    return true;
                }

                if (target is LuaStructuralTableType targetTable)
                {
                    return IsStructuralTableAssignable(sourceTable, targetTable, visiting);
                }
            }

            if (source is LuaClassType sourceClass)
            {
                if (target.Kind == LuaTypeKind.Table)
                {
                    return true;
                }

                if (target is LuaClassType targetClass)
                {
                    return IsClassAssignable(sourceClass, targetClass, visiting);
                }

                if (target is LuaStructuralTableType structural)
                {
                    return IsClassStructurallyAssignable(sourceClass, structural, visiting);
                }
            }

            if (source is LuaEnumType sourceEnum)
            {
                return target is LuaEnumType targetEnum && sourceEnum.Name == targetEnum.Name ||
                    IsAssignable(sourceEnum.UnderlyingType, target, visiting);
            }

            if (source is LuaFunctionType sourceFunction && target is LuaFunctionType targetFunction)
            {
                return IsFunctionAssignable(sourceFunction, targetFunction, visiting);
            }

            if (source is LuaOverloadType sourceOverload)
            {
                return sourceOverload.Signatures.Any(signature => IsAssignable(signature, target, visiting));
            }

            if (target is LuaOverloadType targetOverload)
            {
                return targetOverload.Signatures.All(signature => IsAssignable(source, signature, visiting));
            }

            if (source is LuaCallableType sourceCallable)
            {
                return sourceCallable.Signatures.Any(signature => IsAssignable(signature, target, visiting));
            }

            return source.Kind == target.Kind;
        }
        finally
        {
            visiting.Remove((source, target));
        }
    }

    private bool IsFunctionAssignable(
        LuaFunctionType source,
        LuaFunctionType target,
        HashSet<(LuaType Source, LuaType Target)> visiting)
    {
        var requiredTarget = target.Parameters.Count(static parameter => !parameter.IsOptional && !parameter.IsVararg);
        var requiredSource = source.Parameters.Count(static parameter => !parameter.IsOptional && !parameter.IsVararg);
        if (requiredSource > requiredTarget ||
            !source.Parameters.Any(static parameter => parameter.IsVararg) &&
            source.Parameters.Length < target.Parameters.Length)
        {
            return false;
        }

        for (var index = 0; index < target.Parameters.Length; index++)
        {
            var sourceParameter = index < source.Parameters.Length
                ? source.Parameters[index]
                : source.Parameters.LastOrDefault(static parameter => parameter.IsVararg);
            if (sourceParameter is null ||
                !IsAssignable(target.Parameters[index].Type, sourceParameter.Type, visiting))
            {
                return false;
            }
        }

        for (var index = 0; index < target.Returns.Head.Length; index++)
        {
            if (!IsAssignable(source.Returns.GetElementOrNil(index), target.Returns.Head[index], visiting))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsStructuralTableAssignable(
        LuaStructuralTableType source,
        LuaStructuralTableType target,
        HashSet<(LuaType Source, LuaType Target)> visiting)
    {
        foreach (var targetField in target.Fields.Where(static field => field.Name is not null))
        {
            var sourceField = source.Fields.LastOrDefault(field =>
                string.Equals(field.Name, targetField.Name, StringComparison.Ordinal));
            if (sourceField is null)
            {
                if (!targetField.IsOptional)
                {
                    return false;
                }

                continue;
            }

            if (!IsAssignable(sourceField.ValueType, targetField.ValueType, visiting))
            {
                return false;
            }
        }

        return true;
    }

    private bool IsClassAssignable(
        LuaClassType source,
        LuaClassType target,
        HashSet<(LuaType Source, LuaType Target)> visiting)
    {
        if (string.Equals(source.Name, target.Name, StringComparison.Ordinal))
        {
            return source.TypeArguments.Length == target.TypeArguments.Length &&
                source.TypeArguments.Zip(target.TypeArguments)
                    .All(pair => IsAssignable(pair.First, pair.Second, visiting));
        }

        if (!_declarations.TryGetValue(source.Name, out var declaration) ||
            declaration is not LuaClassDeclaration sourceDeclaration)
        {
            return false;
        }

        var substitutions = sourceDeclaration.TypeParameters
            .Select((parameter, index) => (parameter.Name, Type: index < source.TypeArguments.Length
                ? source.TypeArguments[index]
                : (LuaType)parameter))
            .ToDictionary(static pair => pair.Name, static pair => pair.Type, StringComparer.Ordinal);
        return sourceDeclaration.BaseTypes.Any(baseType =>
            IsAssignable(Substitute(baseType, substitutions), target, visiting));
    }

    private bool IsClassStructurallyAssignable(
        LuaClassType source,
        LuaStructuralTableType target,
        HashSet<(LuaType Source, LuaType Target)> visiting)
    {
        if (!_declarations.TryGetValue(source.Name, out var declaration) ||
            declaration is not LuaClassDeclaration @class)
        {
            return false;
        }

        return IsStructuralTableAssignable(new LuaStructuralTableType(@class.Fields), target, visiting);
    }

    private LuaTableField? FindField(LuaType type, string name, HashSet<string> visiting)
    {
        if (type is LuaAliasType alias)
        {
            return FindField(alias.Target, name, visiting);
        }

        if (type is LuaStructuralTableType table)
        {
            return table.Fields.LastOrDefault(field =>
                string.Equals(field.Name, name, StringComparison.Ordinal));
        }

        if (type is LuaUnionType union)
        {
            var fields = union.Types
                .Select(member => FindField(member, name, visiting))
                .Where(static field => field is not null)
                .Cast<LuaTableField>()
                .ToArray();
            if (fields.Length == 0)
            {
                return null;
            }

            return new LuaTableField(
                name,
                null,
                Union(fields.Select(static field => field.ValueType)),
                fields.Length != union.Types.Length || fields.Any(static field => field.IsOptional));
        }

        if (type is LuaClassType @class && visiting.Add(@class.Name) &&
            _declarations.TryGetValue(@class.Name, out var declaration) &&
            declaration is LuaClassDeclaration classDeclaration)
        {
            var substitutions = classDeclaration.TypeParameters
                .Select((parameter, index) => (parameter.Name, Type: index < @class.TypeArguments.Length
                    ? @class.TypeArguments[index]
                    : (LuaType)parameter))
                .ToDictionary(static pair => pair.Name, static pair => pair.Type, StringComparer.Ordinal);
            var field = classDeclaration.Fields.LastOrDefault(candidate =>
                string.Equals(candidate.Name, name, StringComparison.Ordinal));
            if (field is not null)
            {
                return field with { ValueType = Substitute(field.ValueType, substitutions) };
            }

            foreach (var baseType in classDeclaration.BaseTypes)
            {
                field = FindField(Substitute(baseType, substitutions), name, visiting);
                if (field is not null)
                {
                    return field;
                }
            }
        }

        return null;
    }

    private static void AddUnionMember(LuaType type, List<LuaType> result)
    {
        if (type.Kind == LuaTypeKind.Never)
        {
            return;
        }

        if (type is LuaUnionType union)
        {
            foreach (var member in union.Types)
            {
                AddUnionMember(member, result);
            }

            return;
        }

        AddUnique(type, result);
    }

    private static void AddUnique(LuaType type, List<LuaType> result)
    {
        if (!result.Any(candidate => Equivalent(candidate, type)))
        {
            result.Add(type);
        }
    }

    private void RemoveSubsumed(List<LuaType> result)
    {
        for (var index = result.Count - 1; index >= 0; index--)
        {
            for (var other = 0; other < result.Count; other++)
            {
                if (index != other && IsAssignable(result[index], result[other]))
                {
                    result.RemoveAt(index);
                    break;
                }
            }
        }
    }

    private static LuaPrimitiveType Widen(IEnumerable<LuaType> types)
    {
        var kinds = types.Select(static type => type switch
        {
            LuaBooleanLiteralType => LuaTypeKind.Boolean,
            LuaIntegerLiteralType => LuaTypeKind.Integer,
            LuaFloatLiteralType => LuaTypeKind.Float,
            LuaStringLiteralType => LuaTypeKind.String,
            _ => type.Kind,
        }).Distinct().ToArray();
        if (kinds.All(static kind => kind is LuaTypeKind.Integer or LuaTypeKind.Float or LuaTypeKind.Number))
        {
            return LuaTypes.Number;
        }

        if (kinds.Length == 1)
        {
            return kinds[0] switch
            {
                LuaTypeKind.Boolean => LuaTypes.Boolean,
                LuaTypeKind.Integer => LuaTypes.Integer,
                LuaTypeKind.Float => LuaTypes.Float,
                LuaTypeKind.String => LuaTypes.String,
                LuaTypeKind.Table or LuaTypeKind.Array or LuaTypeKind.Map or
                    LuaTypeKind.StructuralTable or LuaTypeKind.Class => LuaTypes.Table,
                LuaTypeKind.Function or LuaTypeKind.Overload or LuaTypeKind.Callable => LuaTypes.Function,
                _ => LuaTypes.Unknown,
            };
        }

        return LuaTypes.Unknown;
    }

    private static LuaTypeKind LiteralBaseKind(LuaLiteralType literal) => literal.LiteralKind switch
    {
        LuaLiteralKind.Boolean => LuaTypeKind.Boolean,
        LuaLiteralKind.Integer => LuaTypeKind.Integer,
        LuaLiteralKind.Float => LuaTypeKind.Float,
        LuaLiteralKind.String => LuaTypeKind.String,
        _ => LuaTypeKind.Unknown,
    };

    private static bool Equivalent(LuaType left, LuaType right)
    {
        if (Equals(left, right))
        {
            return true;
        }

        return left is LuaStringLiteralType leftString && right is LuaStringLiteralType rightString &&
            leftString.Value.AsSpan().SequenceEqual(rightString.Value.AsSpan());
    }
}
