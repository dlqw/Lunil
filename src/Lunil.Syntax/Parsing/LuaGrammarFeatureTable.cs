using Lunil.Core;

namespace Lunil.Syntax.Parsing;

/// <summary>Versioned source-grammar capabilities used by the lexer and parser contract.</summary>
public sealed record LuaGrammarFeatures(
    bool SupportsGotoAndLabels,
    bool SupportsHexadecimalFloats,
    bool SupportsHexadecimalStringEscapes,
    bool SupportsWhitespaceEatingStringEscape,
    bool SupportsUnicodeStringEscapes,
    bool SupportsBitwiseOperators,
    bool SupportsFloorDivision,
    bool SupportsLocalAttributes,
    bool SupportsPrefixAttributes,
    bool SupportsGlobalDeclarations,
    bool SupportsNamedVarargs);

/// <summary>Authoritative Lua 5.1–5.5 source-grammar feature matrix.</summary>
public static class LuaGrammarFeatureTable
{
    public static LuaGrammarFeatures Get(LuaLanguageVersion version) => version switch
    {
        LuaLanguageVersion.Lua51 => new(
            false, false, false, false, false, false, false, false, false, false, false),
        LuaLanguageVersion.Lua52 => new(
            true, true, true, true, false, false, false, false, false, false, false),
        LuaLanguageVersion.Lua53 => new(
            true, true, true, true, true, true, true, false, false, false, false),
        LuaLanguageVersion.Lua54 => new(
            true, true, true, true, true, true, true, true, false, false, false),
        LuaLanguageVersion.Lua55 => new(
            true, true, true, true, true, true, true, true, true, true, true),
        _ => throw new ArgumentOutOfRangeException(
            nameof(version),
            version,
            "Unknown Lua language version."),
    };
}
