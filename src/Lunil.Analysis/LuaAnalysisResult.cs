using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;

namespace Lunil.Analysis;

public sealed record LuaSymbolTypeInfo(
    LuaSymbol Symbol,
    LuaType DeclaredType,
    LuaType InferredType,
    bool IsDefinitelyAssigned);

public sealed record LuaExpressionTypeInfo(TextSpan Span, LuaType Type);

public sealed record LuaFunctionAnalysis(
    int FunctionId,
    LuaFunctionType Type,
    LuaTypePack InferredReturns,
    LuaControlFlowGraph ControlFlowGraph,
    int FlowIterationCount,
    bool WasWidened);

public sealed record LuaAnalysisBudgetUsage(
    int TypeCount,
    int ConstraintCount,
    int ControlFlowBlockCount,
    int GenericInstantiationCount,
    int MaximumObservedTypeDepth,
    bool WasExceeded);

/// <summary>Immutable annotation-aware type and control-flow analysis result.</summary>
public sealed record LuaAnalysisResult(
    LuaSemanticModel SemanticModel,
    LuaAnnotationDocument Annotations,
    ImmutableArray<LuaTypeDeclaration> TypeDeclarations,
    ImmutableArray<LuaSymbolTypeInfo> Symbols,
    ImmutableArray<LuaExpressionTypeInfo> Expressions,
    ImmutableArray<LuaFunctionAnalysis> Functions,
    ImmutableArray<Diagnostic> Diagnostics,
    LuaAnalysisBudgetUsage BudgetUsage)
{
    /// <summary>Gets the typed call graph for this analysis snapshot.</summary>
    public LuaCallGraph CallGraph { get; init; } = LuaCallGraph.Empty;

    public static LuaAnalysisResult Empty(
        LuaSemanticModel semanticModel,
        LuaAnnotationDocument annotations)
    {
        ArgumentNullException.ThrowIfNull(semanticModel);
        ArgumentNullException.ThrowIfNull(annotations);
        return new LuaAnalysisResult(
            semanticModel,
            annotations,
            [],
            [],
            [],
            [],
            [],
            new LuaAnalysisBudgetUsage(0, 0, 0, 0, 0, false));
    }
}
