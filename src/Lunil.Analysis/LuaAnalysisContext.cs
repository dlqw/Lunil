using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;

namespace Lunil.Analysis;

internal sealed class LuaAnalysisContext
{
    private readonly ImmutableArray<Diagnostic>.Builder _diagnostics =
        ImmutableArray.CreateBuilder<Diagnostic>();
    private readonly HashSet<(string Code, TextSpan Span, string Message)> _diagnosticKeys = [];
    private bool _reportedBudget;

    public LuaAnalysisContext(
        LuaAnalysisOptions options,
        CancellationToken cancellationToken = default)
    {
        Options = options;
        CancellationToken = cancellationToken;
    }

    public LuaAnalysisOptions Options { get; }

    public CancellationToken CancellationToken { get; }

    public int TypeCount { get; private set; }

    public int ConstraintCount { get; private set; }

    public int ControlFlowBlockCount { get; private set; }

    public int GenericInstantiationCount { get; private set; }

    public int MaximumObservedTypeDepth { get; private set; }

    public bool WasBudgetExceeded { get; private set; }

    public bool TryCreateType(TextSpan span, int depth)
    {
        CancellationToken.ThrowIfCancellationRequested();
        MaximumObservedTypeDepth = Math.Max(MaximumObservedTypeDepth, depth);
        if (depth > Options.MaximumTypeDepth || TypeCount >= Options.MaximumTypeCount)
        {
            ReportBudget(span, "type/depth");
            return false;
        }

        TypeCount++;
        return true;
    }

    public bool TryAddConstraint(TextSpan span)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (ConstraintCount >= Options.MaximumConstraintCount)
        {
            ReportBudget(span, "constraint");
            return false;
        }

        ConstraintCount++;
        return true;
    }

    public bool TryAddControlFlowBlock(TextSpan span)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (ControlFlowBlockCount >= Options.MaximumControlFlowBlockCount)
        {
            ReportBudget(span, "control-flow block");
            return false;
        }

        ControlFlowBlockCount++;
        return true;
    }

    public bool TryInstantiateGeneric(TextSpan span)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (GenericInstantiationCount >= Options.MaximumGenericInstantiationCount)
        {
            ReportBudget(span, "generic instantiation");
            return false;
        }

        GenericInstantiationCount++;
        return true;
    }

    public void AddDiagnostic(string code, TextSpan span, string message)
    {
        CancellationToken.ThrowIfCancellationRequested();
        if (Options.SuppressedDiagnosticCodes.Contains(code) ||
            _diagnostics.Count >= Options.MaximumDiagnosticCount ||
            !_diagnosticKeys.Add((code, span, message)))
        {
            return;
        }

        _diagnostics.Add(new Diagnostic(code, Options.DiagnosticSeverity, span, message));
    }

    public ImmutableArray<Diagnostic> GetDiagnostics() => _diagnostics
        .OrderBy(static diagnostic => diagnostic.Span.Start)
        .ThenBy(static diagnostic => diagnostic.Code, StringComparer.Ordinal)
        .ToImmutableArray();

    public LuaAnalysisBudgetUsage GetBudgetUsage() => new(
        TypeCount,
        ConstraintCount,
        ControlFlowBlockCount,
        GenericInstantiationCount,
        MaximumObservedTypeDepth,
        WasBudgetExceeded);

    private void ReportBudget(TextSpan span, string budget)
    {
        WasBudgetExceeded = true;
        if (_reportedBudget)
        {
            return;
        }

        _reportedBudget = true;
        AddDiagnostic(
            "LUA6010",
            span,
            $"Static analysis exceeded the configured {budget} budget and widened remaining values to unknown.");
    }
}
