using System.Collections.Immutable;
using System.Diagnostics;
using Lunil.Analysis;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.EmmyLua;
using Lunil.IR.Canonical;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Compiler;

/// <summary>The highest reusable stage materialized by a front-end snapshot.</summary>
public enum LuaFrontEndStage : byte
{
    Syntax,
    Binding,
    Analysis,
    Lowering,
    Verification,
}

/// <summary>One independently measured operation in the source front end.</summary>
public enum LuaFrontEndOperation : byte
{
    Lexing,
    Annotation,
    Parsing,
    Binding,
    Analysis,
    Lowering,
    Verification,
}

/// <summary>Elapsed time and managed allocation observed for one front-end operation.</summary>
public sealed record LuaFrontEndOperationMetrics(
    LuaFrontEndOperation Operation,
    TimeSpan Elapsed,
    long AllocatedBytes);

/// <summary>
/// Immutable reusable source snapshot. Advancing a snapshot preserves its syntax and binding
/// products instead of lexing and parsing the source again.
/// </summary>
public sealed class LuaFrontEndSnapshot
{
    internal LuaFrontEndSnapshot(
        Guid sessionIdentity,
        LuaSourceDocument source,
        LuaLexResult lexing,
        LuaAnnotationDocument annotations,
        LuaParseResult syntax,
        LuaSemanticModel? semanticModel,
        LuaAnalysisResult? analysis,
        LuaAnalysisEnvironment? analysisEnvironment,
        LuaIrModule? loweredModule,
        ImmutableArray<Diagnostic> loweringDiagnostics,
        LuaIrModule? module,
        ImmutableArray<LuaCompilationDiagnostic> diagnostics,
        ImmutableArray<LuaFrontEndOperationMetrics> metrics,
        LuaFrontEndStage stage)
    {
        SessionIdentity = sessionIdentity;
        Source = source;
        Lexing = lexing;
        Annotations = annotations;
        Syntax = syntax;
        SemanticModel = semanticModel;
        Analysis = analysis;
        AnalysisEnvironment = analysisEnvironment;
        LoweredModule = loweredModule;
        LoweringDiagnostics = loweringDiagnostics;
        Module = module;
        Diagnostics = diagnostics;
        Metrics = metrics;
        Stage = stage;
    }

    public LuaSourceDocument Source { get; }

    public LuaLexResult Lexing { get; }

    public LuaAnnotationDocument Annotations { get; }

    public LuaParseResult Syntax { get; }

    public LuaSemanticModel? SemanticModel { get; }

    public LuaAnalysisResult? Analysis { get; }

    /// <summary>
    /// Gets the lowered module at the lowering stage or the verified module at the verification
    /// stage. The value is null when an error prevents module publication.
    /// </summary>
    public LuaIrModule? Module { get; }

    public ImmutableArray<LuaCompilationDiagnostic> Diagnostics { get; }

    public ImmutableArray<LuaFrontEndOperationMetrics> Metrics { get; }

    public LuaFrontEndStage Stage { get; }

    public LuaLanguageVersion LanguageVersion => Syntax.LanguageVersion;

    public bool HasErrors => Diagnostics.Any(static diagnostic =>
        diagnostic.Severity == DiagnosticSeverity.Error);

    internal Guid SessionIdentity { get; }

    internal LuaAnalysisEnvironment? AnalysisEnvironment { get; }

    internal LuaIrModule? LoweredModule { get; }

    internal ImmutableArray<Diagnostic> LoweringDiagnostics { get; }
}

/// <summary>
/// Runs and reuses the bounded Lua source front end. A session owns one immutable option set and
/// rejects snapshots produced by another session so version-specific products cannot be mixed.
/// </summary>
public sealed class LuaFrontEndSession
{
    private readonly Guid _identity = Guid.NewGuid();

    public LuaFrontEndSession(LuaCompilerOptions? options = null)
    {
        Options = options ?? LuaCompilerOptions.Default;
        LuaCompiler.ValidateOptions(Options, nameof(options));
    }

    public LuaCompilerOptions Options { get; }

    public LuaFrontEndSnapshot Process(
        LuaSourceDocument source,
        LuaFrontEndStage targetStage = LuaFrontEndStage.Analysis,
        LuaAnalysisEnvironment? analysisEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        LunilGuard.NotNull(source);
        ValidateStage(targetStage);
        analysisEnvironment ??= LuaAnalysisEnvironment.Empty;
        cancellationToken.ThrowIfCancellationRequested();

        var metrics = ImmutableArray.CreateBuilder<LuaFrontEndOperationMetrics>();
        var lexing = Measure(
            LuaFrontEndOperation.Lexing,
            metrics,
            () => LuaLexer.Lex(source.Text, Options.Lexer with
            {
                LanguageVersion = Options.LanguageVersion,
            }));
        cancellationToken.ThrowIfCancellationRequested();
        var annotations = Measure(
            LuaFrontEndOperation.Annotation,
            metrics,
            () => LuaAnnotationParser.Parse(lexing, Options.Annotations));
        cancellationToken.ThrowIfCancellationRequested();
        var syntax = Measure(
            LuaFrontEndOperation.Parsing,
            metrics,
            () => LuaParser.Parse(lexing, Options.Parser with
            {
                LanguageVersion = Options.LanguageVersion,
                UseCompactSyntaxArena = targetStage == LuaFrontEndStage.Syntax &&
                    Options.Parser.UseCompactSyntaxArena,
            }, cancellationToken));
        cancellationToken.ThrowIfCancellationRequested();

        var snapshot = new LuaFrontEndSnapshot(
            _identity,
            source,
            lexing,
            annotations,
            syntax,
            semanticModel: null,
            analysis: null,
            analysisEnvironment: null,
            loweredModule: null,
            loweringDiagnostics: [],
            module: null,
            BuildDiagnostics(lexing, annotations, syntax),
            metrics.ToImmutable(),
            LuaFrontEndStage.Syntax);
        return targetStage == LuaFrontEndStage.Syntax
            ? snapshot
            : Advance(snapshot, targetStage, analysisEnvironment, cancellationToken);
    }

    public LuaFrontEndSnapshot Advance(
        LuaFrontEndSnapshot snapshot,
        LuaFrontEndStage targetStage,
        LuaAnalysisEnvironment? analysisEnvironment = null,
        CancellationToken cancellationToken = default)
    {
        LunilGuard.NotNull(snapshot);
        ValidateStage(targetStage);
        if (snapshot.SessionIdentity != _identity)
        {
            throw new ArgumentException(
                "The front-end snapshot belongs to another session.",
                nameof(snapshot));
        }

        analysisEnvironment ??= snapshot.AnalysisEnvironment ?? LuaAnalysisEnvironment.Empty;
        cancellationToken.ThrowIfCancellationRequested();
        if (targetStage <= snapshot.Stage &&
            (targetStage < LuaFrontEndStage.Analysis ||
             Equals(snapshot.AnalysisEnvironment, analysisEnvironment)))
        {
            return snapshot;
        }

        var metrics = snapshot.Metrics.ToBuilder();
        var semantics = snapshot.SemanticModel;
        if (targetStage >= LuaFrontEndStage.Binding && semantics is null)
        {
            semantics = Measure(
                LuaFrontEndOperation.Binding,
                metrics,
                () => LuaBinder.Bind(snapshot.Syntax, Options.Binder with
                {
                    LanguageVersion = Options.LanguageVersion,
                }));
            cancellationToken.ThrowIfCancellationRequested();
        }

        var analysis = snapshot.Analysis;
        var environmentChanged = analysis is not null &&
            !Equals(snapshot.AnalysisEnvironment, analysisEnvironment);
        if (targetStage >= LuaFrontEndStage.Analysis &&
            (analysis is null || environmentChanged))
        {
            analysis = Measure(
                LuaFrontEndOperation.Analysis,
                metrics,
                () => LuaTypeAnalyzer.Analyze(
                    semantics!,
                    snapshot.Annotations,
                    analysisEnvironment,
                    Options.Analysis,
                    cancellationToken));
            cancellationToken.ThrowIfCancellationRequested();
        }

        var loweredModule = environmentChanged ? null : snapshot.LoweredModule;
        var loweringDiagnostics = environmentChanged ? [] : snapshot.LoweringDiagnostics;
        if (targetStage >= LuaFrontEndStage.Lowering &&
            (environmentChanged || snapshot.Stage < LuaFrontEndStage.Lowering))
        {
            var lowering = Measure(
                LuaFrontEndOperation.Lowering,
                metrics,
                () => LuaLowerer.Lower(semantics!));
            loweredModule = lowering.Module is null
                ? null
                : ApplySourceName(lowering.Module, snapshot.Source.SourceName);
            loweringDiagnostics = lowering.Diagnostics;
            cancellationToken.ThrowIfCancellationRequested();
        }

        var diagnostics = BuildDiagnostics(
            snapshot.Lexing,
            snapshot.Annotations,
            snapshot.Syntax,
            semantics,
            analysis,
            loweringDiagnostics);
        var module = targetStage >= LuaFrontEndStage.Lowering &&
            !diagnostics.Any(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
                ? loweredModule
                : null;
        if (targetStage >= LuaFrontEndStage.Verification && module is not null)
        {
            var verification = Measure(
                LuaFrontEndOperation.Verification,
                metrics,
                () => LuaIrVerifier.Verify(module, Options.Verifier));
            cancellationToken.ThrowIfCancellationRequested();
            if (!verification.IsEmpty)
            {
                var builder = diagnostics.ToBuilder();
                foreach (var error in verification)
                {
                    builder.Add(new LuaCompilationDiagnostic(
                        LuaCompilationPhase.Verification,
                        new Diagnostic(
                            "LUA4003",
                            DiagnosticSeverity.Error,
                            GetVerificationSpan(module, error),
                            error.Message)));
                }

                diagnostics = builder.ToImmutable();
                module = null;
            }
        }

        var completedStage = targetStage > snapshot.Stage || environmentChanged
            ? targetStage
            : snapshot.Stage;
        return new LuaFrontEndSnapshot(
            _identity,
            snapshot.Source,
            snapshot.Lexing,
            snapshot.Annotations,
            snapshot.Syntax,
            semantics,
            analysis,
            targetStage >= LuaFrontEndStage.Analysis ? analysisEnvironment : snapshot.AnalysisEnvironment,
            loweredModule,
            loweringDiagnostics,
            module,
            diagnostics,
            metrics.ToImmutable(),
            completedStage);
    }

    public LuaCompilationResult ToCompilationResult(LuaFrontEndSnapshot snapshot)
    {
        LunilGuard.NotNull(snapshot);
        if (snapshot.SessionIdentity != _identity)
        {
            throw new ArgumentException(
                "The front-end snapshot belongs to another session.",
                nameof(snapshot));
        }

        if (snapshot.Stage < LuaFrontEndStage.Verification ||
            snapshot.SemanticModel is null || snapshot.Analysis is null)
        {
            throw new InvalidOperationException(
                "A verified front-end snapshot is required to create a compilation result.");
        }

        return new LuaCompilationResult(
            snapshot.Source,
            snapshot.Syntax,
            snapshot.Annotations,
            snapshot.SemanticModel,
            snapshot.Analysis,
            snapshot.Module,
            snapshot.Diagnostics)
        {
            FrontEndSnapshot = snapshot,
        };
    }

    private ImmutableArray<LuaCompilationDiagnostic> BuildDiagnostics(
        LuaLexResult lexing,
        LuaAnnotationDocument annotations,
        LuaParseResult syntax,
        LuaSemanticModel? semantics = null,
        LuaAnalysisResult? analysis = null,
        ImmutableArray<Diagnostic> loweringDiagnostics = default)
    {
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

        var observed = new HashSet<Diagnostic>();
        AddDiagnostics(lexing.Diagnostics, LuaCompilationPhase.Lexing, observed, diagnostics);
        AddDiagnostics(annotations.Diagnostics, LuaCompilationPhase.Annotation, observed, diagnostics);
        AddDiagnostics(syntax.Diagnostics, LuaCompilationPhase.Parsing, observed, diagnostics);
        if (semantics is not null)
        {
            AddDiagnostics(semantics.Diagnostics, LuaCompilationPhase.Binding, observed, diagnostics);
        }

        if (analysis is not null)
        {
            AddDiagnostics(analysis.Diagnostics, LuaCompilationPhase.Analysis, observed, diagnostics);
        }

        if (!loweringDiagnostics.IsDefaultOrEmpty)
        {
            foreach (var diagnostic in loweringDiagnostics)
            {
                if (observed.Add(diagnostic))
                {
                    diagnostics.Add(new LuaCompilationDiagnostic(
                        diagnostic.Code == "LUA4002"
                            ? LuaCompilationPhase.Verification
                            : LuaCompilationPhase.Lowering,
                        diagnostic));
                }
            }
        }

        return diagnostics.ToImmutable();
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

    private static T Measure<T>(
        LuaFrontEndOperation operation,
        ImmutableArray<LuaFrontEndOperationMetrics>.Builder metrics,
        Func<T> action)
    {
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var stopwatch = Stopwatch.StartNew();
        try
        {
            return action();
        }
        finally
        {
            stopwatch.Stop();
            metrics.Add(new LuaFrontEndOperationMetrics(
                operation,
                stopwatch.Elapsed,
                Math.Max(0, GC.GetAllocatedBytesForCurrentThread() - allocatedBefore)));
        }
    }

    private static LuaIrModule ApplySourceName(LuaIrModule module, string? sourceName)
    {
        if (sourceName is null)
        {
            return module;
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(sourceName).ToImmutableArray();
        return module with
        {
            Functions =
            [
                .. module.Functions.Select(function => function with { SourceName = bytes }),
            ],
        };
    }

    private static Lunil.Core.Text.TextSpan GetVerificationSpan(
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

    private static void ValidateStage(LuaFrontEndStage stage)
    {
        if (!LunilEnum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown front-end stage.");
        }
    }
}
