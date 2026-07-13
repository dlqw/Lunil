using System.Collections.Immutable;
using Lunil.Core.Text;

namespace Lunil.EmmyLua;

/// <summary>Base syntax node for a parsed LuaLS/EmmyLua type expression.</summary>
public abstract record LuaTypeSyntax(TextSpan Span);

public sealed record LuaNamedTypeSyntax(
    string Name,
    ImmutableArray<LuaTypeSyntax> TypeArguments,
    TextSpan Span) : LuaTypeSyntax(Span);

public enum LuaTypeLiteralKind : byte
{
    Nil,
    Boolean,
    Number,
    Text,
}

public sealed record LuaLiteralTypeSyntax(
    LuaTypeLiteralKind Kind,
    string Text,
    TextSpan Span) : LuaTypeSyntax(Span);

public sealed record LuaUnionTypeSyntax(
    ImmutableArray<LuaTypeSyntax> Types,
    TextSpan Span) : LuaTypeSyntax(Span);

public sealed record LuaIntersectionTypeSyntax(
    ImmutableArray<LuaTypeSyntax> Types,
    TextSpan Span) : LuaTypeSyntax(Span);

public sealed record LuaNullableTypeSyntax(
    LuaTypeSyntax Type,
    TextSpan Span) : LuaTypeSyntax(Span);

public sealed record LuaArrayTypeSyntax(
    LuaTypeSyntax ElementType,
    TextSpan Span) : LuaTypeSyntax(Span);

public sealed record LuaTupleTypeSyntax(
    ImmutableArray<LuaTypeSyntax> Elements,
    TextSpan Span) : LuaTypeSyntax(Span);

public sealed record LuaVarargTypeSyntax(
    LuaTypeSyntax? ElementType,
    TextSpan Span) : LuaTypeSyntax(Span);

public sealed record LuaFunctionParameterTypeSyntax(
    string? Name,
    LuaTypeSyntax Type,
    bool IsOptional,
    bool IsVararg,
    TextSpan Span);

public sealed record LuaFunctionTypeSyntax(
    ImmutableArray<LuaFunctionParameterTypeSyntax> Parameters,
    ImmutableArray<LuaTypeSyntax> Returns,
    TextSpan Span) : LuaTypeSyntax(Span);

public sealed record LuaTableFieldTypeSyntax(
    string? Name,
    LuaTypeSyntax? KeyType,
    LuaTypeSyntax ValueType,
    bool IsOptional,
    TextSpan Span);

public sealed record LuaTableTypeSyntax(
    ImmutableArray<LuaTableFieldTypeSyntax> Fields,
    TextSpan Span) : LuaTypeSyntax(Span);
