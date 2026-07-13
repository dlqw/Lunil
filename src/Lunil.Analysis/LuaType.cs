using System.Collections.Immutable;
using System.Text;

namespace Lunil.Analysis;

#pragma warning disable CA1720 // Integer, float, and string are exact Lua domain terms.

public enum LuaTypeKind : byte
{
    Any,
    Unknown,
    Never,
    Nil,
    Boolean,
    Integer,
    Float,
    Number,
    String,
    Table,
    Function,
    Thread,
    Userdata,
    Literal,
    Union,
    Intersection,
    Array,
    Map,
    StructuralTable,
    Tuple,
    TypePack,
    GenericParameter,
    GenericInstance,
    Class,
    Alias,
    Enum,
    Overload,
    Callable,
}

/// <summary>Base immutable semantic type produced by static analysis.</summary>
public abstract record LuaType(LuaTypeKind Kind)
{
    public virtual string DisplayName => Kind.ToString().ToLowerInvariant();

    public override string ToString() => DisplayName;
}

public sealed record LuaPrimitiveType(LuaTypeKind PrimitiveKind, string Name) : LuaType(PrimitiveKind)
{
    public override string DisplayName => Name;
}

public enum LuaLiteralKind : byte
{
    Boolean,
    Integer,
    Float,
    String,
}

public abstract record LuaLiteralType(LuaLiteralKind LiteralKind) : LuaType(LuaTypeKind.Literal);

public sealed record LuaBooleanLiteralType(bool Value) : LuaLiteralType(LuaLiteralKind.Boolean)
{
    public override string DisplayName => Value ? "true" : "false";
}

public sealed record LuaIntegerLiteralType(long Value) : LuaLiteralType(LuaLiteralKind.Integer)
{
    public override string DisplayName => Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record LuaFloatLiteralType(double Value) : LuaLiteralType(LuaLiteralKind.Float)
{
    public override string DisplayName => Value.ToString("R", System.Globalization.CultureInfo.InvariantCulture);
}

public sealed record LuaStringLiteralType(ImmutableArray<byte> Value) : LuaLiteralType(LuaLiteralKind.String)
{
    public override string DisplayName => $"'{Escape(Value.AsSpan())}'";

    private static string Escape(ReadOnlySpan<byte> bytes)
    {
        var text = new StringBuilder();
        foreach (var value in bytes)
        {
            if (value is >= 0x20 and <= 0x7e && value is not (byte)'\'' and not (byte)'\\')
            {
                text.Append((char)value);
            }
            else
            {
                text.Append("\\x").Append(value.ToString(
                    "X2",
                    System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return text.ToString();
    }
}

public sealed record LuaUnionType(ImmutableArray<LuaType> Types) : LuaType(LuaTypeKind.Union)
{
    public override string DisplayName => string.Join("|", Types.Select(static type => type.DisplayName));
}

public sealed record LuaIntersectionType(ImmutableArray<LuaType> Types) : LuaType(LuaTypeKind.Intersection)
{
    public override string DisplayName => string.Join("&", Types.Select(static type => type.DisplayName));
}

public sealed record LuaArrayType(LuaType ElementType) : LuaType(LuaTypeKind.Array)
{
    public override string DisplayName => $"{ElementType.DisplayName}[]";
}

public sealed record LuaMapType(LuaType KeyType, LuaType ValueType) : LuaType(LuaTypeKind.Map)
{
    public override string DisplayName => $"table<{KeyType.DisplayName}, {ValueType.DisplayName}>";
}

public sealed record LuaTableField(
    string? Name,
    LuaType? KeyType,
    LuaType ValueType,
    bool IsOptional,
    bool IsReadOnly = false);

public sealed record LuaStructuralTableType(
    ImmutableArray<LuaTableField> Fields,
    LuaType? ArrayElementType = null,
    LuaType? MapKeyType = null,
    LuaType? MapValueType = null,
    bool IsOpen = false) : LuaType(LuaTypeKind.StructuralTable)
{
    public override string DisplayName
    {
        get
        {
            var fields = Fields.Select(item => item.Name is null
                ? $"[{item.KeyType?.DisplayName ?? "unknown"}]: {item.ValueType.DisplayName}"
                : $"{item.Name}{(item.IsOptional ? "?" : string.Empty)}: {item.ValueType.DisplayName}");
            return $"{{{string.Join(", ", fields)}}}";
        }
    }
}

public sealed record LuaTupleType(ImmutableArray<LuaType> Elements) : LuaType(LuaTypeKind.Tuple)
{
    public override string DisplayName => $"({string.Join(", ", Elements.Select(static type => type.DisplayName))})";
}

public sealed record LuaTypePack(
    ImmutableArray<LuaType> Head,
    LuaType? VariadicType = null) : LuaType(LuaTypeKind.TypePack)
{
    public static LuaTypePack Empty { get; } = new([]);

    public override string DisplayName
    {
        get
        {
            var items = Head.Select(static type => type.DisplayName).ToList();
            if (VariadicType is not null)
            {
                items.Add($"{VariadicType.DisplayName}...");
            }

            return string.Join(", ", items);
        }
    }

    public LuaType GetElementOrNil(int index) =>
        (uint)index < (uint)Head.Length
            ? Head[index]
            : VariadicType ?? LuaTypes.Nil;
}

public sealed record LuaGenericParameterType(
    string Name,
    int Ordinal,
    LuaType? Constraint = null) : LuaType(LuaTypeKind.GenericParameter)
{
    public override string DisplayName => Name;
}

public sealed record LuaGenericInstanceType(
    LuaType Definition,
    ImmutableArray<LuaType> TypeArguments) : LuaType(LuaTypeKind.GenericInstance)
{
    public override string DisplayName =>
        $"{Definition.DisplayName}<{string.Join(", ", TypeArguments.Select(static type => type.DisplayName))}>";
}

public sealed record LuaFunctionParameter(
    string? Name,
    LuaType Type,
    bool IsOptional = false,
    bool IsVararg = false);

public sealed record LuaFunctionType(
    ImmutableArray<LuaFunctionParameter> Parameters,
    LuaTypePack Returns,
    ImmutableArray<LuaGenericParameterType> TypeParameters,
    bool HasImplicitSelf = false) : LuaType(LuaTypeKind.Function)
{
    public override string DisplayName =>
        $"fun({string.Join(", ", Parameters.Select(FormatParameter))}): {Returns.DisplayName}";

    private static string FormatParameter(LuaFunctionParameter parameter) =>
        $"{parameter.Name}{(parameter.IsOptional ? "?" : string.Empty)}: " +
        $"{parameter.Type.DisplayName}{(parameter.IsVararg ? "..." : string.Empty)}";
}

public sealed record LuaOverloadType(
    ImmutableArray<LuaFunctionType> Signatures) : LuaType(LuaTypeKind.Overload)
{
    public override string DisplayName => string.Join(" | ", Signatures.Select(static signature => signature.DisplayName));
}

public sealed record LuaCallableType(
    LuaType ReceiverType,
    ImmutableArray<LuaFunctionType> Signatures) : LuaType(LuaTypeKind.Callable)
{
    public override string DisplayName => $"callable<{ReceiverType.DisplayName}>";
}

public sealed record LuaClassType(
    string Name,
    ImmutableArray<LuaType> TypeArguments) : LuaType(LuaTypeKind.Class)
{
    public override string DisplayName => TypeArguments.IsEmpty
        ? Name
        : $"{Name}<{string.Join(", ", TypeArguments.Select(static type => type.DisplayName))}>";
}

public sealed record LuaAliasType(string Name, LuaType Target) : LuaType(LuaTypeKind.Alias)
{
    public override string DisplayName => Name;
}

public sealed record LuaEnumType(
    string Name,
    LuaType UnderlyingType,
    ImmutableArray<LuaLiteralType> Members) : LuaType(LuaTypeKind.Enum)
{
    public override string DisplayName => Name;
}

/// <summary>Canonical built-in semantic type singletons.</summary>
public static class LuaTypes
{
    public static LuaPrimitiveType Any { get; } = new(LuaTypeKind.Any, "any");
    public static LuaPrimitiveType Unknown { get; } = new(LuaTypeKind.Unknown, "unknown");
    public static LuaPrimitiveType Never { get; } = new(LuaTypeKind.Never, "never");
    public static LuaPrimitiveType Nil { get; } = new(LuaTypeKind.Nil, "nil");
    public static LuaPrimitiveType Boolean { get; } = new(LuaTypeKind.Boolean, "boolean");
    public static LuaPrimitiveType Integer { get; } = new(LuaTypeKind.Integer, "integer");
    public static LuaPrimitiveType Float { get; } = new(LuaTypeKind.Float, "float");
    public static LuaPrimitiveType Number { get; } = new(LuaTypeKind.Number, "number");
    public static LuaPrimitiveType String { get; } = new(LuaTypeKind.String, "string");
    public static LuaPrimitiveType Table { get; } = new(LuaTypeKind.Table, "table");
    public static LuaPrimitiveType Function { get; } = new(LuaTypeKind.Function, "function");
    public static LuaPrimitiveType Thread { get; } = new(LuaTypeKind.Thread, "thread");
    public static LuaPrimitiveType Userdata { get; } = new(LuaTypeKind.Userdata, "userdata");
}

#pragma warning restore CA1720
