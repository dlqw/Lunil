using Lunil.Analysis;
using Lunil.Core;
using Lunil.Core.Text;

namespace Lunil.Compiler;

/// <summary>
/// Public source compiler that owns the bounded front end, lexical binding, canonical lowering,
/// source identity, and independent IR verification boundary.
/// </summary>
public sealed class LuaCompiler
{
    private readonly LuaFrontEndSession _frontEnd;

    public LuaCompiler(LuaCompilerOptions? options = null)
    {
        Options = options ?? LuaCompilerOptions.Default;
        ValidateOptions(Options, nameof(options));
        _frontEnd = new LuaFrontEndSession(Options);
    }

    public LuaCompilerOptions Options { get; }

    public LuaCompilationResult CompileUtf8(
        string source,
        string? sourceName = null,
        CancellationToken cancellationToken = default) =>
        Compile(LuaSourceDocument.FromUtf8(source, sourceName), cancellationToken);

    public LuaCompilationResult CompileBytes(
        ReadOnlySpan<byte> source,
        string? sourceName = null,
        CancellationToken cancellationToken = default) =>
        Compile(LuaSourceDocument.FromBytes(source, sourceName), cancellationToken);

    public LuaCompilationResult Compile(
        SourceText source,
        string? sourceName = null,
        CancellationToken cancellationToken = default) =>
        Compile(new LuaSourceDocument(source, sourceName), cancellationToken);

    public LuaCompilationResult Compile(
        LuaSourceDocument source,
        CancellationToken cancellationToken = default) =>
        Compile(source, LuaAnalysisEnvironment.Empty, cancellationToken);

    public LuaCompilationResult Compile(
        LuaSourceDocument source,
        LuaAnalysisEnvironment analysisEnvironment,
        CancellationToken cancellationToken = default)
    {
        LunilGuard.NotNull(source);
        LunilGuard.NotNull(analysisEnvironment);
        var snapshot = _frontEnd.Process(
            source,
            LuaFrontEndStage.Verification,
            analysisEnvironment,
            cancellationToken);
        return _frontEnd.ToCompilationResult(snapshot);
    }

    internal static void ValidateOptions(LuaCompilerOptions options, string parameterName)
    {
        LunilGuard.NotNull(options.Lexer);
        LunilGuard.NotNull(options.Annotations);
        LunilGuard.NotNull(options.Analysis);
        LunilGuard.NotNull(options.Parser);
        LunilGuard.NotNull(options.Binder);
        LunilGuard.NotNull(options.Verifier);
        if (!LuaLanguageVersions.IsKnown(options.LanguageVersion))
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                options.LanguageVersion,
                "The compiler language version is invalid.");
        }
    }
}
