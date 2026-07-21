using System.Collections.Immutable;
using System.Text;
using Lunil.Analysis;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.IR.Canonical;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Compiler;

/// <summary>
/// Public source compiler that owns the bounded front end, lexical binding, canonical lowering,
/// source identity, and independent IR verification boundary.
/// </summary>
public sealed class LuaCompiler
{
    public LuaCompiler(LuaCompilerOptions? options = null)
    {
        Options = options ?? LuaCompilerOptions.Default;
        ArgumentNullException.ThrowIfNull(Options.Lexer);
        ArgumentNullException.ThrowIfNull(Options.Annotations);
        ArgumentNullException.ThrowIfNull(Options.Analysis);
        ArgumentNullException.ThrowIfNull(Options.Parser);
        ArgumentNullException.ThrowIfNull(Options.Binder);
        ArgumentNullException.ThrowIfNull(Options.Verifier);
        if (!LuaLanguageVersions.IsKnown(Options.LanguageVersion))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                Options.LanguageVersion,
                "The compiler language version is invalid.");
        }
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
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(analysisEnvironment);
        cancellationToken.ThrowIfCancellationRequested();

        var lexing = LuaLexer.Lex(source.Text, Options.Lexer with
        {
            LanguageVersion = Options.LanguageVersion,
        });
        cancellationToken.ThrowIfCancellationRequested();

        var annotations = LuaAnnotationParser.Parse(lexing, Options.Annotations);
        cancellationToken.ThrowIfCancellationRequested();

        var syntax = LuaParser.Parse(lexing, Options.Parser with
        {
            LanguageVersion = Options.LanguageVersion,
        });
        cancellationToken.ThrowIfCancellationRequested();

        var semantics = LuaBinder.Bind(syntax, Options.Binder with
        {
            LanguageVersion = Options.LanguageVersion,
        });
        cancellationToken.ThrowIfCancellationRequested();

        var analysis = LuaTypeAnalyzer.Analyze(
            semantics,
            annotations,
            analysisEnvironment,
            Options.Analysis,
            cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();

        var lowering = LuaLowerer.Lower(semantics);
        cancellationToken.ThrowIfCancellationRequested();

        var diagnostics = ImmutableArray.CreateBuilder<LuaCompilationDiagnostic>();
        if (!LuaVersionFeatureTable.Get(Options.LanguageVersion).IsImplemented)
        {
            diagnostics.Add(new LuaCompilationDiagnostic(
                LuaCompilationPhase.Configuration,
                new Diagnostic(
                    "LUA0001",
                    DiagnosticSeverity.Error,
                    default,
                    $"{LuaLanguageVersions.GetDisplayName(Options.LanguageVersion)} source " +
                    "semantics are not implemented in this build; Lunil will not silently " +
                    "apply another language version's semantics.")));
        }

        var observedDiagnostics = new HashSet<Diagnostic>();
        AddDiagnostics(
            lexing.Diagnostics,
            LuaCompilationPhase.Lexing,
            observedDiagnostics,
            diagnostics);
        AddDiagnostics(
            annotations.Diagnostics,
            LuaCompilationPhase.Annotation,
            observedDiagnostics,
            diagnostics);
        AddDiagnostics(
            syntax.Diagnostics,
            LuaCompilationPhase.Parsing,
            observedDiagnostics,
            diagnostics);
        AddDiagnostics(
            semantics.Diagnostics,
            LuaCompilationPhase.Binding,
            observedDiagnostics,
            diagnostics);
        AddDiagnostics(
            analysis.Diagnostics,
            LuaCompilationPhase.Analysis,
            observedDiagnostics,
            diagnostics);
        foreach (var diagnostic in lowering.Diagnostics)
        {
            if (observedDiagnostics.Add(diagnostic))
            {
                diagnostics.Add(new LuaCompilationDiagnostic(
                    diagnostic.Code == "LUA4002"
                        ? LuaCompilationPhase.Verification
                        : LuaCompilationPhase.Lowering,
                    diagnostic));
            }
        }

        var module = lowering.Module;
        if (diagnostics.Any(static diagnostic =>
                diagnostic.Severity == DiagnosticSeverity.Error))
        {
            module = null;
        }
        else if (module is not null)
        {
            module = ApplySourceName(module, source.SourceName);
            var verificationErrors = LuaIrVerifier.Verify(module, Options.Verifier);
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var error in verificationErrors)
            {
                diagnostics.Add(new LuaCompilationDiagnostic(
                    LuaCompilationPhase.Verification,
                    new Diagnostic(
                        "LUA4003",
                        DiagnosticSeverity.Error,
                        GetVerificationSpan(module, error),
                        error.Message)));
            }

            if (!verificationErrors.IsEmpty)
            {
                module = null;
            }
        }

        return new LuaCompilationResult(
            source,
            syntax,
            annotations,
            semantics,
            analysis,
            module,
            diagnostics.ToImmutable());
    }

    private static void AddDiagnostics(
        ImmutableArray<Diagnostic> source,
        LuaCompilationPhase phase,
        HashSet<Diagnostic> observed,
        ImmutableArray<LuaCompilationDiagnostic>.Builder destination)
    {
        foreach (var diagnostic in source)
        {
            if (observed.Add(diagnostic))
            {
                destination.Add(new LuaCompilationDiagnostic(phase, diagnostic));
            }
        }
    }

    private static LuaIrModule ApplySourceName(LuaIrModule module, string? sourceName)
    {
        if (sourceName is null)
        {
            return module;
        }

        var bytes = Encoding.UTF8.GetBytes(sourceName).ToImmutableArray();
        return module with
        {
            Functions =
            [
                .. module.Functions.Select(function => function with { SourceName = bytes }),
            ],
        };
    }

    private static TextSpan GetVerificationSpan(
        LuaIrModule module,
        LuaIrVerificationError error)
    {
        if ((uint)error.FunctionId >= (uint)module.Functions.Length)
        {
            return default;
        }

        var function = module.Functions[error.FunctionId];
        return (uint)error.ProgramCounter < (uint)function.Instructions.Length
            ? function.Instructions[error.ProgramCounter].Span
            : function.Span;
    }
}
