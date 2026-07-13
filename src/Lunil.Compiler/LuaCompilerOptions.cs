using Lunil.Analysis;
using Lunil.EmmyLua;
using Lunil.IR.Canonical;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Compiler;

/// <summary>Bounded front-end and canonical verification configuration.</summary>
public sealed record LuaCompilerOptions
{
    public static LuaCompilerOptions Default { get; } = new();

    public LuaLexerOptions Lexer { get; init; } = LuaLexerOptions.Default;

    public LuaAnnotationOptions Annotations { get; init; } = LuaAnnotationOptions.Default;

    public LuaAnalysisOptions Analysis { get; init; } = LuaAnalysisOptions.Default;

    public LuaParserOptions Parser { get; init; } = LuaParserOptions.Default;

    public LuaBinderOptions Binder { get; init; } = LuaBinderOptions.Default;

    public LuaIrVerifierOptions Verifier { get; init; } = LuaIrVerifierOptions.Default;
}
