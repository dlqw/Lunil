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

/// <summary>Records raw, metatable, and effective type facts at a setmetatable call.</summary>
public sealed record LuaMetatableFact(
    TextSpan Span,
    LuaType RawType,
    LuaType MetatableType,
    LuaMetatableType EffectiveType,
    bool IsPrecise);

/// <summary>Describes a method recognized on a Lua prototype.</summary>
public sealed record LuaPrototypeMethodFact(
    string Name,
    LuaType Type,
    bool HasImplicitSelf);

/// <summary>Describes a recognized Lua class/prototype and its inferred instance shape.</summary>
public sealed record LuaObjectModelFact(
    string Name,
    TextSpan DeclaringSpan,
    LuaPrototypeType PrototypeType,
    LuaMetatableType InstanceType,
    ImmutableArray<LuaType> BaseTypes,
    ImmutableArray<LuaPrototypeMethodFact> Methods,
    bool IsPrecise);

public sealed record LuaHostEffectFact(
    string FunctionPath,
    TextSpan Span,
    LuaHostEffectKind Effects,
    LuaHostSourceLocation? Source);

public sealed record LuaCallbackRegistrationFact(
    string FunctionPath,
    TextSpan Span,
    TextSpan CallbackSpan,
    int? CallbackFunctionId,
    LuaHostCallbackInvocationKind Invocation,
    LuaHostCallbackCardinality Cardinality,
    LuaHostCallbackRetentionKind Retention,
    string? UnsubscribeFunction,
    bool Escapes);

public sealed record LuaPersistenceAccessFact(
    string FunctionPath,
    TextSpan Span,
    LuaPersistenceOperationKind Operation,
    string? Key,
    bool IsDynamicKey,
    string SchemaId,
    int SchemaVersion,
    LuaType ValueType,
    bool MissingReturnsNil,
    string? MigrationFunction);

public sealed record LuaUpvalueCellFact(
    LuaSymbol Symbol,
    LuaType Type,
    ImmutableArray<int> ReaderFunctionIds,
    ImmutableArray<int> WriterFunctionIds,
    bool Escapes,
    bool IsLoopCaptured);

public sealed record LuaNilPathFact(
    TextSpan Span,
    string Path,
    int HopCount,
    LuaType InputType,
    LuaType ResultType,
    bool WasNarrowed);

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

    /// <summary>Gets flow-sensitive metatable attachments observed in this analysis snapshot.</summary>
    public ImmutableArray<LuaMetatableFact> MetatableFacts { get; init; } = [];

    /// <summary>Gets recognized class/prototype facts in declaration order.</summary>
    public ImmutableArray<LuaObjectModelFact> ObjectModels { get; init; } = [];

    public ImmutableArray<LuaHostEffectFact> HostEffects { get; init; } = [];

    public ImmutableArray<LuaCallbackRegistrationFact> CallbackRegistrations { get; init; } = [];

    public ImmutableArray<LuaPersistenceAccessFact> PersistenceAccesses { get; init; } = [];

    public ImmutableArray<LuaUpvalueCellFact> UpvalueCells { get; init; } = [];

    public ImmutableArray<LuaNilPathFact> NilPaths { get; init; } = [];

    public static LuaAnalysisResult Empty(
        LuaSemanticModel semanticModel,
        LuaAnnotationDocument annotations)
    {
        LunilGuard.NotNull(semanticModel);
        LunilGuard.NotNull(annotations);
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
