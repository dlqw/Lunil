using System.Collections.Immutable;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.Syntax.Parsing;

public sealed record LuaParseConfiguration(
    LuaLexerOptions Lexer,
    LuaParserOptions Parser);

public sealed record LuaIncrementalParseMetrics(
    bool WasFullReparse,
    string Reason,
    TextSpan ChangedOldSpan,
    TextSpan ReparsedNewSpan,
    int ReusedNodeCount,
    int ReusedTokenCount);

public sealed record LuaParseResult
{
    private readonly LuaSyntaxArena? _arena;
    private LuaSyntaxNode? _root;

    public LuaParseResult(
        SourceText source,
        LuaSyntaxNode root,
        ImmutableArray<Diagnostic> diagnostics)
    {
        LunilGuard.NotNull(source);
        LunilGuard.NotNull(root);
        Source = source;
        _root = root;
        Diagnostics = diagnostics;
    }

    internal LuaParseResult(
        SourceText source,
        LuaSyntaxArena arena,
        ImmutableArray<Diagnostic> diagnostics)
    {
        Source = source;
        _arena = arena;
        Diagnostics = diagnostics;
        CompactMetrics = arena.Metrics;
    }

    public SourceText Source { get; }

    public LuaSyntaxNode Root => _root ??= _arena!.CreateNode(_arena.RootIndex);

    public ImmutableArray<Diagnostic> Diagnostics { get; }

    public LuaLanguageVersion LanguageVersion { get; init; } = LuaLanguageVersions.Default;

    public LuaParseConfiguration Configuration { get; init; } = new(
        LuaLexerOptions.Default,
        LuaParserOptions.Default);

    public LuaCompactSyntaxMetrics? CompactMetrics { get; init; }

    public LuaIncrementalParseMetrics? IncrementalMetrics { get; init; }

    public void Deconstruct(
        out SourceText source,
        out LuaSyntaxNode root,
        out ImmutableArray<Diagnostic> diagnostics)
    {
        source = Source;
        root = Root;
        diagnostics = Diagnostics;
    }
}
