using System.Collections.Immutable;
using Lunil.Core.Text;

namespace Lunil.Analysis;

public enum LuaTypeDeclarationKind : byte
{
    Class,
    Alias,
    Enum,
}

public abstract record LuaTypeDeclaration(
    string Name,
    LuaTypeDeclarationKind Kind,
    TextSpan Span);

public sealed record LuaClassDeclaration(
    string ClassName,
    ImmutableArray<LuaGenericParameterType> TypeParameters,
    ImmutableArray<LuaType> BaseTypes,
    ImmutableArray<LuaTableField> Fields,
    ImmutableArray<LuaFunctionType> CallSignatures,
    ImmutableDictionary<string, LuaFunctionType> Operators,
    TextSpan DeclaringSpan) : LuaTypeDeclaration(
        ClassName,
        LuaTypeDeclarationKind.Class,
        DeclaringSpan);

public sealed record LuaAliasDeclaration(
    string AliasName,
    ImmutableArray<LuaGenericParameterType> TypeParameters,
    LuaType Target,
    TextSpan DeclaringSpan) : LuaTypeDeclaration(
        AliasName,
        LuaTypeDeclarationKind.Alias,
        DeclaringSpan);

public sealed record LuaEnumDeclaration(
    string EnumName,
    LuaType UnderlyingType,
    ImmutableArray<LuaLiteralType> Members,
    TextSpan DeclaringSpan) : LuaTypeDeclaration(
        EnumName,
        LuaTypeDeclarationKind.Enum,
        DeclaringSpan);
