using System.Collections.Immutable;

namespace Lunil.Syntax.Lexing;

public abstract record LuaTokenValue;

public sealed record LuaStringTokenValue(ImmutableArray<byte> Bytes) : LuaTokenValue;

#pragma warning disable CA1720 // Integer and float are exact Lua domain terms.
public sealed record LuaIntegerTokenValue(long Integer) : LuaTokenValue;

public sealed record LuaFloatTokenValue(double Float) : LuaTokenValue;
#pragma warning restore CA1720
