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
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(annotations);
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
        var environment = new AnnotationTypeEnvironment(annotations, context);
        cancellationToken.ThrowIfCancellationRequested();
        var graphs = ControlFlowGraphBuilder.BuildAll(semanticModel, context);
        cancellationToken.ThrowIfCancellationRequested();
        var result = new AnalysisEngine(
            semanticModel,
            annotations,
            environment,
            graphs,
            context).Analyze();
        cancellationToken.ThrowIfCancellationRequested();
        return result;
    }

    private static void ValidateOptions(LuaAnalysisOptions options)
    {
        if (!Enum.IsDefined(options.DiagnosticSeverity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The analysis diagnostic severity is invalid.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTypeCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumConstraintCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumControlFlowBlockCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumFlowIterations);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumUnionMemberCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTypeDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumGenericInstantiationCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumReturnPackLength);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDiagnosticCount);
        ArgumentNullException.ThrowIfNull(options.SuppressedDiagnosticCodes);
    }
}
