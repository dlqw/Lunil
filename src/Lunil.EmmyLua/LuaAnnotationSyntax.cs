using System.Collections.Immutable;
using Lunil.Core.Text;

namespace Lunil.EmmyLua;

public enum LuaAnnotationVisibility : byte
{
    Unspecified,
    Public,
    Protected,
    Private,
    Package,
}

public enum LuaCastOperation : byte
{
    Replace,
    Add,
    Remove,
}

public enum LuaDiagnosticAction : byte
{
    Disable,
    DisableNextLine,
    Enable,
}

/// <summary>Base syntax node for a source annotation directive.</summary>
public abstract record LuaAnnotationSyntax(
    string Tag,
    LuaAnnotationDialect Dialect,
    TextSpan Span);

public sealed record LuaClassAnnotationSyntax(
    string Name,
    ImmutableArray<string> TypeParameters,
    ImmutableArray<LuaTypeSyntax> BaseTypes,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("class", Dialect, Span);

public sealed record LuaFieldAnnotationSyntax(
    string Name,
    LuaTypeSyntax Type,
    LuaAnnotationVisibility Visibility,
    bool IsOptional,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("field", Dialect, Span);

public sealed record LuaAliasAnnotationSyntax(
    string Name,
    LuaTypeSyntax? Type,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("alias", Dialect, Span);

public sealed record LuaAliasContinuationAnnotationSyntax(
    LuaTypeSyntax Type,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("|", Dialect, Span);

public sealed record LuaEnumAnnotationSyntax(
    string Name,
    LuaTypeSyntax? KeyType,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("enum", Dialect, Span);

public sealed record LuaTypeAnnotationSyntax(
    ImmutableArray<LuaTypeSyntax> Types,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("type", Dialect, Span);

public sealed record LuaParamAnnotationSyntax(
    string Name,
    LuaTypeSyntax Type,
    bool IsOptional,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("param", Dialect, Span);

public sealed record LuaReturnTypeSyntax(
    LuaTypeSyntax Type,
    string? Name,
    TextSpan Span);

public sealed record LuaReturnAnnotationSyntax(
    ImmutableArray<LuaReturnTypeSyntax> Returns,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("return", Dialect, Span);

public sealed record LuaGenericParameterSyntax(
    string Name,
    LuaTypeSyntax? Constraint,
    TextSpan Span);

public sealed record LuaGenericAnnotationSyntax(
    ImmutableArray<LuaGenericParameterSyntax> Parameters,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("generic", Dialect, Span);

public sealed record LuaOverloadAnnotationSyntax(
    LuaFunctionTypeSyntax Type,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("overload", Dialect, Span);

public sealed record LuaVarargAnnotationSyntax(
    LuaTypeSyntax Type,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("vararg", Dialect, Span);

public sealed record LuaCastAnnotationSyntax(
    string Name,
    LuaTypeSyntax Type,
    LuaCastOperation Operation,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("cast", Dialect, Span);

public sealed record LuaDiagnosticAnnotationSyntax(
    LuaDiagnosticAction Action,
    ImmutableHashSet<string> Codes,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("diagnostic", Dialect, Span);

public sealed record LuaOperatorAnnotationSyntax(
    string Operator,
    LuaTypeSyntax? OperandType,
    LuaTypeSyntax ResultType,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax("operator", Dialect, Span);

public sealed record LuaMarkerAnnotationSyntax(
    string Marker,
    string Arguments,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax(Marker, Dialect, Span);

public sealed record LuaUnknownAnnotationSyntax(
    string UnknownTag,
    string RawText,
    LuaAnnotationDialect Dialect,
    TextSpan Span) : LuaAnnotationSyntax(UnknownTag, Dialect, Span);
