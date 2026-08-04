using Lunil.EmmyLua;
using Lunil.Semantics.Binding;

namespace Lunil.Analysis;

/// <summary>Public entry point for bounded annotation-aware type and flow analysis.</summary>
public static class LuaTypeAnalyzer
{
    public static LuaAnalysisResult Analyze(
        LuaSemanticModel semanticModel,
        LuaAnnotationDocument annotations,
        LuaAnalysisOptions? options = null,
        CancellationToken cancellationToken = default) =>
        Analyze(
            semanticModel,
            annotations,
            LuaAnalysisEnvironment.Empty,
            options,
            cancellationToken);

    public static LuaAnalysisResult Analyze(
        LuaSemanticModel semanticModel,
        LuaAnnotationDocument annotations,
        LuaAnalysisEnvironment environment,
        LuaAnalysisOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        LunilGuard.NotNull(semanticModel);
        LunilGuard.NotNull(annotations);
        LunilGuard.NotNull(environment);
        LunilGuard.NotNull(environment.ModuleTypes);
        environment.HostContract?.Validate();
        options ??= LuaAnalysisOptions.Default;
        cancellationToken.ThrowIfCancellationRequested();
        ValidateOptions(options);
        if (!options.Enabled)
        {
            return LuaAnalysisResult.Empty(semanticModel, annotations);
        }

        if (!ReferenceEquals(semanticModel.Syntax.Source, annotations.Source) &&
            !semanticModel.Syntax.Source.AsSpan().SequenceEqual(annotations.Source.AsSpan()))
        {
            throw new ArgumentException(
                "The semantic model and annotation document must describe the same source.",
                nameof(annotations));
        }

        var context = new LuaAnalysisContext(options, cancellationToken);
        var typeEnvironment = new AnnotationTypeEnvironment(annotations, environment, context);
        cancellationToken.ThrowIfCancellationRequested();
        var graphs = ControlFlowGraphBuilder.BuildAll(semanticModel, context);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new AnalysisEngine(
            semanticModel,
            annotations,
            environment,
            typeEnvironment,
            graphs,
            context).Analyze();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static void ValidateOptions(LuaAnalysisOptions options)
    {
        if (!LunilEnum.IsDefined(options.DiagnosticSeverity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The analysis diagnostic severity is invalid.");
        }

        LunilGuard.Positive(options.MaximumTypeCount);
        LunilGuard.Positive(options.MaximumConstraintCount);
        LunilGuard.GreaterThanOrEqual(options.MaximumControlFlowBlockCount, 2);
        LunilGuard.Positive(options.MaximumFlowIterations);
        LunilGuard.GreaterThanOrEqual(options.MaximumUnionMemberCount, 2);
        LunilGuard.Positive(options.MaximumTypeDepth);
        LunilGuard.Positive(options.MaximumGenericInstantiationCount);
        LunilGuard.Positive(options.MaximumReturnPackLength);
        LunilGuard.Positive(options.MaximumDiagnosticCount);
        LunilGuard.NotNull(options.SuppressedDiagnosticCodes);
    }
}
