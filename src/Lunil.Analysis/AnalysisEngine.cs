using System.Collections.Immutable;
using System.Text;
using System.Runtime.CompilerServices;
using System.Diagnostics.CodeAnalysis;
using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis;

internal sealed partial class AnalysisEngine
{
    private readonly LuaSemanticModel _semantics;
    private readonly LuaAnnotationDocument _annotations;
    private readonly LuaAnalysisEnvironment _environment;
    private readonly ImmutableDictionary<string, LuaType> _hostGlobalTypes;
    private readonly ImmutableDictionary<string, LuaType> _hostModuleTypes;
    private readonly AnnotationTypeEnvironment _types;
    private readonly LuaTypeRelations _relations;
    private readonly ImmutableArray<LuaControlFlowGraph> _graphs;
    private readonly LuaAnalysisContext _context;
    private readonly Dictionary<TextSpan, LuaNameReference> _references;
    private readonly Dictionary<TextSpan, LuaSymbol> _declarations;
    private readonly Dictionary<int, FunctionSyntax> _functionSyntax;
    private readonly Dictionary<TextSpan, int> _functionIdsByOwnerSpan;
    private readonly Dictionary<TextSpan, ImmutableArray<LuaAnnotationSyntax>> _attachedAnnotations;
    private readonly Dictionary<int, LuaFunctionInfo> _functionsById;
    private readonly Dictionary<int, LuaSymbol> _symbolsById;
    private readonly Dictionary<int, LuaControlFlowGraph> _graphsById;
    private readonly LuaFunctionInfo[] _functionsInPreOrder;
    private readonly int[] _functionParentsInPreOrder;
    private readonly Dictionary<TextSpan, string> _functionNamesByOwnerSpan = [];
    private readonly Dictionary<VariableKey, LuaType> _declaredTypes = [];
    private readonly Dictionary<int, LuaType> _symbolInferences = [];
    private readonly Dictionary<TextSpan, LuaType> _expressionInferences = [];
    private readonly VersionedGlobalTypeTable _globalTypes = new();
    private readonly Dictionary<int, LuaType> _functionValueTypes = [];
    private readonly Dictionary<int, LuaFunctionAnalysis> _functionAnalyses = [];
    private readonly Dictionary<int, string> _functionCaptureSignatures = [];
    private readonly Dictionary<string, LuaPrototypeType> _latestPrototypes = new(StringComparer.Ordinal);
    private readonly HashSet<int> _functionsInProgress = [];
    private readonly HashSet<string> _reportedUnknownGlobals = new(StringComparer.Ordinal);
    private readonly HashSet<TextSpan> _countedExpressionTypes = [];
    private readonly HashSet<int> _definitelyAssignedSymbols = [];
    private readonly List<LuaMetatableFact> _metatableFacts = [];
    private readonly List<LuaHostEffectFact> _hostEffects = [];
    private readonly List<LuaCallbackRegistrationFact> _callbackRegistrations = [];
    private readonly List<LuaPersistenceAccessFact> _persistenceAccesses = [];
    private readonly List<LuaNilPathFact> _nilPaths = [];
    private readonly Dictionary<int, UpvalueCellState> _upvalueCells = [];
    private const int MaximumMetatableLookupDepth = 16;

    /// <summary>Named-member growth limit for one structural table shape; later
    /// member writes absorb into the map key/value unions instead.</summary>
    private const int MaximumStructuralTableFieldGrowth = 4_096;

    /// <summary>Nodes reachable from global values committed during this analysis.</summary>
    private readonly HashSet<LuaType> _publishedGlobalTypeNodes = new(LunilReferenceEqualityComparer.Instance);

    /// <summary>Seed dictionaries installed by <see cref="InstallBuiltIns"/>; their node
    /// closures are cached per dictionary identity so concurrent document analyses
    /// walk a shared library universe at most once per generation.</summary>
    private ImmutableArray<ImmutableDictionary<string, LuaType>> _globalSeedDictionaries;
    private HashSet<LuaType>[]? _globalSeedNodeSets;

    private static readonly ConditionalWeakTable<
        ImmutableDictionary<string, LuaType>,
        HashSet<LuaType>> GlobalSeedNodeCache = new();

    private static readonly HashSet<LuaType> EmptyGlobalSeedNodes = new(LunilReferenceEqualityComparer.Instance);

    private FunctionAnalysisContext? _currentFunction;

    public AnalysisEngine(
        LuaSemanticModel semantics,
        LuaAnnotationDocument annotations,
        LuaAnalysisEnvironment environment,
        AnnotationTypeEnvironment types,
        ImmutableArray<LuaControlFlowGraph> graphs,
        LuaAnalysisContext context)
    {
        _semantics = semantics;
        _annotations = annotations;
        _environment = environment;
        _hostGlobalTypes = environment.HostContract?.CreateGlobalTypes() ??
            ImmutableDictionary<string, LuaType>.Empty.WithComparers(StringComparer.Ordinal);
        _hostModuleTypes = environment.HostContract?.CreateModuleTypes() ??
            ImmutableDictionary<string, LuaType>.Empty.WithComparers(StringComparer.Ordinal);
        _types = types;
        _relations = types.Relations;
        _graphs = graphs;
        _context = context;
        _references = new Dictionary<TextSpan, LuaNameReference>(semantics.References.Length);
        foreach (var reference in semantics.References)
        {
            _references[reference.Span] = reference;
        }

        _declarations = new Dictionary<TextSpan, LuaSymbol>(semantics.Symbols.Length);
        foreach (var symbol in semantics.Symbols)
        {
            _declarations[symbol.DeclaringSpan] = symbol;
        }

        _functionsById = new Dictionary<int, LuaFunctionInfo>(semantics.Functions.Length);
        foreach (var function in semantics.Functions)
        {
            _functionsById.Add(function.Id, function);
        }

        _graphsById = new Dictionary<int, LuaControlFlowGraph>(graphs.Length);
        foreach (var graph in graphs)
        {
            _graphsById.Add(graph.FunctionId, graph);
        }

        _symbolsById = new Dictionary<int, LuaSymbol>(semantics.Symbols.Length);
        foreach (var symbol in semantics.Symbols)
        {
            _symbolsById.Add(symbol.Id, symbol);
        }

        (_functionsInPreOrder, _functionParentsInPreOrder) = BuildFunctionPreOrderIndex();
        (_functionSyntax, _functionIdsByOwnerSpan) = BuildFunctionIndex();
        BuildFunctionTargetIndex();
        _attachedAnnotations = AttachAnnotations();
        InstallBuiltIns();
    }

    private (LuaFunctionInfo[] Functions, int[] Parents) BuildFunctionPreOrderIndex()
    {
        // Function spans either nest or are disjoint, so ordering by start and then by
        // descending end yields a pre-order walk whose parent links can be resolved
        // with a stack. GetContainingFunctionId then answers containment queries in
        // O(log n + nesting depth) instead of scanning every function.
        var functions = _semantics.Functions.OrderBy(static item => item.Span.Start)
            .ThenByDescending(static item => item.Span.End)
            .ToArray();
        var parents = new int[functions.Length];
        var stack = new int[functions.Length];
        var depth = 0;
        for (var index = 0; index < functions.Length; index++)
        {
            var span = functions[index].Span;
            while (depth > 0 && functions[stack[depth - 1]].Span.End < span.End)
            {
                depth--;
            }

            parents[index] = depth > 0 ? stack[depth - 1] : -1;
            stack[depth++] = index;
        }

        return (functions, parents);
    }

    /// <summary>
    /// Creates a state whose global bindings read through to the versioned global
    /// table as of the current version. Cloning such a state copies only the
    /// function-local overlay, so straight-line flow analysis no longer scales with
    /// global count and state creation never copies the global environment.
    /// </summary>
    private FlowState CreateRootState() => new(_globalTypes, _globalTypes.Version);

    private void SetGlobalType(string name, LuaType type)
    {
        _globalTypes.Set(name, type);
        CollectTypeNodesInto(type, _publishedGlobalTypeNodes);
    }

    /// <summary>
    /// Whether any value committed to the versioned global table — during this
    /// analysis or through the installed seed globals — can reach the given type
    /// object. Only then can a global base entry embed it, so table-mutation
    /// propagation may skip the base scan otherwise.
    /// </summary>
    private bool IsGloballyPublishedType(LuaType type)
    {
        if (_publishedGlobalTypeNodes.Contains(type))
        {
            return true;
        }

        if (_globalSeedNodeSets is null)
        {
            var sets = new List<HashSet<LuaType>>(_globalSeedDictionaries.Length);
            foreach (var dictionary in _globalSeedDictionaries)
            {
                sets.Add(GetGlobalSeedNodes(dictionary));
            }

            _globalSeedNodeSets = [.. sets];
        }

        foreach (var set in _globalSeedNodeSets)
        {
            if (set.Contains(type))
            {
                return true;
            }
        }

        return false;
    }

    private static HashSet<LuaType> GetGlobalSeedNodes(ImmutableDictionary<string, LuaType> dictionary)
    {
        if (dictionary.IsEmpty)
        {
            return EmptyGlobalSeedNodes;
        }

        lock (dictionary)
        {
            if (!GlobalSeedNodeCache.TryGetValue(dictionary, out var nodes))
            {
                nodes = new HashSet<LuaType>(LunilReferenceEqualityComparer.Instance);
                foreach (var pair in dictionary)
                {
                    CollectTypeNodesInto(pair.Value, nodes);
                }

                GlobalSeedNodeCache.AddOrUpdate(dictionary, nodes);
            }

            return nodes;
        }
    }

    /// <summary>
    /// Adds every composite node reachable through the edges table-mutation
    /// propagation descends. The persistent set doubles as the visited set, so
    /// repeated commits of shared graphs only touch newly published nodes.
    /// </summary>
    private static void CollectTypeNodesInto(LuaType type, HashSet<LuaType> nodes)
    {
        if (!nodes.Add(type))
        {
            return;
        }

        switch (type)
        {
            case LuaMetatableType metatable:
                CollectTypeNodesInto(metatable.BaseType, nodes);
                CollectTypeNodesInto(metatable.MetatableType, nodes);
                break;
            case LuaPrototypeType prototype:
                CollectTypeNodesInto(prototype.Shape, nodes);
                foreach (var baseType in prototype.BaseTypes)
                {
                    CollectTypeNodesInto(baseType, nodes);
                }

                break;
            case LuaUnionType union:
                foreach (var member in union.Types)
                {
                    CollectTypeNodesInto(member, nodes);
                }

                break;
            case LuaStructuralTableType table:
                foreach (var field in table.Fields)
                {
                    if (field.KeyType is not null)
                    {
                        CollectTypeNodesInto(field.KeyType, nodes);
                    }

                    CollectTypeNodesInto(field.ValueType, nodes);
                }

                break;
            case LuaFunctionType function:
                foreach (var parameter in function.Parameters)
                {
                    CollectTypeNodesInto(parameter.Type, nodes);
                }

                CollectTypeNodesInto(function.Returns, nodes);
                break;
            case LuaTypePack pack:
                foreach (var item in pack.Head)
                {
                    CollectTypeNodesInto(item, nodes);
                }

                if (pack.VariadicType is not null)
                {
                    CollectTypeNodesInto(pack.VariadicType, nodes);
                }

                break;
            case LuaOverloadType overload:
                foreach (var signature in overload.Signatures)
                {
                    CollectTypeNodesInto(signature, nodes);
                }

                break;
        }
    }

    /// <summary>
    /// Append-only global environment. Each write records the table version it became
    /// visible at, so a flow state created at version v reads exactly the bindings a
    /// full copy would have captured, without ever materializing that copy.
    /// </summary>
    internal sealed class VersionedGlobalTypeTable
    {
        private readonly Dictionary<string, List<(int Version, LuaType Type)>> _entries =
            new(StringComparer.Ordinal);

        public int Version { get; private set; }

        public void Set(string name, LuaType type)
        {
            if (!_entries.TryGetValue(name, out var history))
            {
                history = [];
                _entries.Add(name, history);
            }

            history.Add((Version, type));
            Version++;
        }

        public bool TryGet(string name, int version, out LuaType type)
        {
            if (!_entries.TryGetValue(name, out var history))
            {
                type = null!;
                return false;
            }

            // Histories are appended in ascending version order; find the newest
            // entry that was already visible at the requested version.
            var low = 0;
            var high = history.Count - 1;
            var index = -1;
            while (low <= high)
            {
                var middle = low + (high - low) / 2;
                if (history[middle].Version <= version)
                {
                    index = middle;
                    low = middle + 1;
                }
                else
                {
                    high = middle - 1;
                }
            }

            if (index < 0)
            {
                type = null!;
                return false;
            }

            type = history[index].Type;
            return true;
        }

        public bool TryGetLatest(string name, out LuaType type)
        {
            if (_entries.TryGetValue(name, out var history))
            {
                type = history[^1].Type;
                return true;
            }

            type = null!;
            return false;
        }

        public IEnumerable<string> EnumerateNames(int version)
        {
            foreach (var pair in _entries)
            {
                if (pair.Value[0].Version <= version)
                {
                    yield return pair.Key;
                }
            }
        }
    }

    public LuaAnalysisResult Analyze()
    {
        AnalyzeFunction(0, []);
        foreach (var function in _semantics.Functions.OrderBy(static item => item.Id))
        {
            AnalyzeFunction(function.Id, GetAnnotations(_functionSyntax[function.Id].Owner));
        }

        ReportUnreachableCode();
        CompleteCallSiteProjection();
        var symbols = _semantics.Symbols
            .Where(static symbol => symbol.Kind != LuaSymbolKind.Environment)
            .Select(symbol =>
            {
                var key = VariableKey.Local(symbol.Id);
                var declared = _declaredTypes.GetValueOrDefault(key, LuaTypes.Any);
                var inferred = _symbolInferences.GetValueOrDefault(symbol.Id, declared);
                return new LuaSymbolTypeInfo(
                    symbol,
                    declared,
                    inferred,
                    _definitelyAssignedSymbols.Contains(symbol.Id));
            })
            .OrderBy(static item => item.Symbol.Id)
            .ToImmutableArray();
        var expressions = _expressionInferences
            .OrderBy(static pair => pair.Key.Start)
            .ThenBy(static pair => pair.Key.Length)
            .Select(static pair => new LuaExpressionTypeInfo(pair.Key, pair.Value))
            .ToImmutableArray();
        var functions = _functionAnalyses.Values
            .OrderBy(static function => function.FunctionId)
            .ToImmutableArray();
        return new LuaAnalysisResult(
            _semantics,
            _annotations,
            _types.Declarations,
            symbols,
            expressions,
            functions,
            AnalysisDiagnosticFilter.Apply(
                _annotations,
                _context.GetDiagnostics(),
                _context.Options),
            _context.GetBudgetUsage())
        {
            CallGraph = BuildCallGraph(),
            MetatableFacts = _metatableFacts
                .OrderBy(static fact => fact.Span.Start)
                .ThenBy(static fact => fact.Span.Length)
                .ToImmutableArray(),
            ObjectModels = symbols
                .Where(static item => item.InferredType is LuaPrototypeType &&
                    item.Symbol.Kind is LuaSymbolKind.Local or LuaSymbolKind.Global)
                .Select(item => CreateObjectModelFact(
                    item.Symbol,
                    (LuaPrototypeType)item.InferredType))
                .OrderBy(static item => item.DeclaringSpan.Start)
                .ToImmutableArray(),
            HostEffects = [.. _hostEffects.OrderBy(static item => item.Span.Start)],
            CallbackRegistrations = [.. _callbackRegistrations.OrderBy(static item => item.Span.Start)],
            PersistenceAccesses = [.. _persistenceAccesses.OrderBy(static item => item.Span.Start)],
            UpvalueCells = [.. _upvalueCells.Values
                .OrderBy(static item => item.Symbol.Id)
                .Select(static item => new LuaUpvalueCellFact(
                    item.Symbol,
                    item.Type,
                    [.. item.Readers.OrderBy(static value => value)],
                    [.. item.Writers.OrderBy(static value => value)],
                    item.Escapes,
                    item.Symbol.Kind is LuaSymbolKind.NumericForVariable or
                        LuaSymbolKind.GenericForVariable))],
            NilPaths = [.. _nilPaths.OrderBy(static item => item.Span.Start)],
        };
    }

    private static LuaObjectModelFact CreateObjectModelFact(
        LuaSymbol symbol,
        LuaPrototypeType prototype)
    {
        var methods = GetPrototypeFields(prototype.Shape)
            .Where(static field => field.Name is not null && field.ValueType is
                LuaFunctionType or LuaOverloadType)
            .Select(field => new LuaPrototypeMethodFact(
                field.Name!,
                field.ValueType,
                field.ValueType is LuaFunctionType function && function.HasImplicitSelf ||
                    field.ValueType is LuaOverloadType overload &&
                    overload.Signatures.Any(static signature => signature.HasImplicitSelf)))
            .ToImmutableArray();
        return new LuaObjectModelFact(
            prototype.Name,
            symbol.DeclaringSpan,
            prototype,
            new LuaMetatableType(
                new LuaStructuralTableType([], IsOpen: true),
                prototype,
                prototype.IsPrecise),
            prototype.BaseTypes,
            methods,
            prototype.IsPrecise);
    }

    private static ImmutableArray<LuaTableField> GetPrototypeFields(LuaType shape) => shape switch
    {
        LuaStructuralTableType table => table.Fields,
        LuaMetatableType metatable => GetPrototypeFields(metatable.BaseType),
        LuaPrototypeType prototype => GetPrototypeFields(prototype.Shape),
        _ => [],
    };

    private LuaType AnalyzeFunction(
        int functionId,
        ImmutableArray<LuaAnnotationSyntax> annotations,
        LuaType? implicitSelfType = null)
    {
        var functionInfo = _functionsById[functionId];
        var captureSignature = GetCaptureSignature(functionInfo);
        if (_functionValueTypes.TryGetValue(functionId, out var cached) &&
            !_functionsInProgress.Contains(functionId) &&
            _functionCaptureSignatures.TryGetValue(functionId, out var previousCaptureSignature) &&
            string.Equals(captureSignature, previousCaptureSignature, StringComparison.Ordinal))
        {
            return cached;
        }

        if (!_functionsInProgress.Add(functionId))
        {
            return _functionValueTypes.GetValueOrDefault(functionId, LuaTypes.Function);
        }

        var syntax = _functionSyntax[functionId];
        var specification = BuildFunctionSpecification(
            functionInfo,
            syntax,
            annotations,
            implicitSelfType);
        _functionValueTypes[functionId] = specification.ValueType;
        var previous = _currentFunction;
        var functionContext = new FunctionAnalysisContext(
            functionId,
            specification.Primary,
            specification.ExpectedReturns,
            specification.HasExplicitReturns);
        _currentFunction = functionContext;
        try
        {
            var state = CreateInitialState(functionInfo, specification);
            var result = AnalyzeBlock(syntax.Body, state, insideLoop: false);
            foreach (var symbol in functionInfo.Symbols.Where(symbol =>
                         result.Fallthrough.IsAssigned(VariableKey.Local(symbol.Id))))
            {
                _definitelyAssignedSymbols.Add(symbol.Id);
            }
            if (result.Fallthrough.Reachable)
            {
                functionContext.Returns.Add(LuaTypePack.Empty);
            }

            var inferredReturns = MergeReturnPacks(functionContext.Returns, syntax.Owner.Span);
            var primary = specification.HasExplicitReturns
                ? specification.Primary
                : specification.Primary with { Returns = inferredReturns };
            var valueType = specification.Overloads.IsEmpty
                ? (LuaType)primary
                : new LuaOverloadType([primary, .. specification.Overloads]);
            _functionValueTypes[functionId] = valueType;
            var graph = _graphsById[functionId];
            _functionAnalyses[functionId] = new LuaFunctionAnalysis(
                functionId,
                primary,
                inferredReturns,
                graph,
                functionContext.FlowIterations,
                functionContext.WasWidened);
            _functionCaptureSignatures[functionId] = GetCaptureSignature(functionInfo);
            return valueType;
        }
        finally
        {
            _currentFunction = previous;
            _functionsInProgress.Remove(functionId);
        }
    }

    private string GetCaptureSignature(LuaFunctionInfo function)
    {
        if (function.Captures.IsEmpty)
        {
            return string.Empty;
        }

        var signature = new StringBuilder();
        foreach (var capture in function.Captures.OrderBy(static item => item.Id))
        {
            var type = _upvalueCells.TryGetValue(capture.Id, out var cell)
                ? cell.Type
                : _symbolInferences.GetValueOrDefault(
                    capture.Id,
                    _declaredTypes.GetValueOrDefault(VariableKey.Local(capture.Id), LuaTypes.Any));
            signature.Append(capture.Id).Append(':').Append(type.DisplayName).Append(';');
        }

        return signature.ToString();
    }

    private FlowState CreateInitialState(
        LuaFunctionInfo function,
        FunctionSpecification specification)
    {
        var state = CreateRootState();
        var parameters = function.Symbols
            .Where(static symbol => symbol.Kind == LuaSymbolKind.Parameter)
            .OrderBy(static symbol => symbol.DeclaringSpan.Start)
            .ThenBy(static symbol => symbol.Id)
            .ToArray();
        for (var index = 0; index < parameters.Length; index++)
        {
            var parameter = parameters[index];
            var type = index < specification.Primary.Parameters.Length
                ? specification.Primary.Parameters[index].Type
                : LuaTypes.Any;
            var key = VariableKey.Local(parameter.Id);
            state.SetType(key, type);
            state.MarkAssigned(key);
            _definitelyAssignedSymbols.Add(parameter.Id);
            _declaredTypes[key] = type;
            RecordSymbolInference(parameter, type);
        }

        foreach (var capture in function.Captures)
        {
            var key = VariableKey.Local(capture.Id);
            var type = _symbolInferences.GetValueOrDefault(
                capture.Id,
                _declaredTypes.GetValueOrDefault(key, LuaTypes.Any));
            if (!_upvalueCells.TryGetValue(capture.Id, out var cell))
            {
                cell = new UpvalueCellState(capture, type);
                _upvalueCells.Add(capture.Id, cell);
            }

            type = cell.Type;
            state.SetType(key, type);
            state.MarkAssigned(key);
        }

        return state;
    }

    private BlockResult AnalyzeBlock(
        LuaSyntaxNode block,
        FlowState incoming,
        bool insideLoop)
    {
        if (!incoming.Reachable)
        {
            return BlockResult.Next(incoming.Clone());
        }

        var state = incoming.Clone();
        var breaks = new List<FlowState>();
        foreach (var statement in block.ChildNodes())
        {
            ApplyCasts(statement, state);
            var result = AnalyzeStatement(statement, state, insideLoop);
            state = result.Fallthrough;
            breaks.AddRange(result.Breaks);
        }

        return new BlockResult(state, breaks);
    }

    private BlockResult AnalyzeStatement(
        LuaSyntaxNode statement,
        FlowState state,
        bool insideLoop)
    {
        return statement.Kind switch
        {
            LuaSyntaxKind.EmptyStatement or LuaSyntaxKind.LabelStatement =>
                BlockResult.Next(state),
            LuaSyntaxKind.AssignmentStatement => AnalyzeAssignment(statement, state),
            LuaSyntaxKind.CallStatement => AnalyzeCallStatement(statement, state),
            LuaSyntaxKind.BreakStatement => AnalyzeBreak(statement, state, insideLoop),
            LuaSyntaxKind.GotoStatement => BlockResult.Next(state),
            LuaSyntaxKind.DoStatement => AnalyzeDo(statement, state, insideLoop),
            LuaSyntaxKind.WhileStatement => AnalyzeWhile(statement, state),
            LuaSyntaxKind.RepeatStatement => AnalyzeRepeat(statement, state),
            LuaSyntaxKind.IfStatement => AnalyzeIf(statement, state, insideLoop),
            LuaSyntaxKind.NumericForStatement => AnalyzeNumericFor(statement, state),
            LuaSyntaxKind.GenericForStatement => AnalyzeGenericFor(statement, state),
            LuaSyntaxKind.FunctionDeclarationStatement =>
                AnalyzeFunctionDeclaration(statement, state, local: false),
            LuaSyntaxKind.GlobalDeclarationStatement when
                statement.ChildNodes().Any(static node => node.Kind == LuaSyntaxKind.FunctionBody) =>
                AnalyzeFunctionDeclaration(statement, state, local: false),
            LuaSyntaxKind.GlobalDeclarationStatement => BlockResult.Next(state),
            LuaSyntaxKind.LocalFunctionDeclarationStatement =>
                AnalyzeFunctionDeclaration(statement, state, local: true),
            LuaSyntaxKind.LocalDeclarationStatement => AnalyzeLocalDeclaration(statement, state),
            LuaSyntaxKind.ReturnStatement => AnalyzeReturn(statement, state),
            LuaSyntaxKind.Error => BlockResult.Next(state),
            _ => BlockResult.Next(state),
        };
    }

    private BlockResult AnalyzeDo(
        LuaSyntaxNode statement,
        FlowState state,
        bool insideLoop)
    {
        var body = statement.ChildNodes().Single(static node => node.Kind == LuaSyntaxKind.Block);
        return AnalyzeBlock(body, state, insideLoop);
    }

    private static BlockResult AnalyzeBreak(
        LuaSyntaxNode statement,
        FlowState state,
        bool insideLoop)
    {
        if (!insideLoop)
        {
            return BlockResult.Next(state);
        }

        var breakState = state.Clone();
        var unreachable = state.Clone();
        unreachable.Reachable = false;
        return new BlockResult(unreachable, [breakState]);
    }

    private (Dictionary<int, FunctionSyntax>, Dictionary<TextSpan, int>) BuildFunctionIndex()
    {
        var byId = new Dictionary<int, FunctionSyntax>();
        var bySpan = new Dictionary<TextSpan, int>();
        var mainBody = _semantics.Syntax.Root.ChildNodes()
            .Single(static node => node.Kind == LuaSyntaxKind.Block);
        byId[0] = new FunctionSyntax(
            _semantics.Syntax.Root,
            mainBody,
            null,
            false);
        bySpan[_functionsById[0].Span] = 0;
        var owners = _semantics.Syntax.Root.DescendantNodes()
            .Where(static node => node.Kind is
                LuaSyntaxKind.FunctionDeclarationStatement or
                LuaSyntaxKind.GlobalDeclarationStatement or
                LuaSyntaxKind.LocalFunctionDeclarationStatement or
                LuaSyntaxKind.FunctionExpression)
            .ToLookup(static node => node.Span);
        foreach (var function in _semantics.Functions.Where(static item => item.Id != 0))
        {
            var owner = owners[function.Span].First();
            var functionBody = owner.DescendantNodes().First(static node =>
                node.Kind == LuaSyntaxKind.FunctionBody);
            var body = functionBody.ChildNodes().Single(static node =>
                node.Kind == LuaSyntaxKind.Block);
            var parameters = functionBody.ChildNodes().Single(static node =>
                node.Kind == LuaSyntaxKind.ParameterList);
            var hasSelf = owner.Kind == LuaSyntaxKind.FunctionDeclarationStatement &&
                owner.DescendantTokens().Any(static token => token.Kind == LuaTokenKind.Colon);
            byId[function.Id] = new FunctionSyntax(owner, body, parameters, hasSelf);
            bySpan[owner.Span] = function.Id;
        }

        return (byId, bySpan);
    }

    private Dictionary<TextSpan, ImmutableArray<LuaAnnotationSyntax>> AttachAnnotations()
    {
        var attachable = _semantics.Syntax.Root.DescendantNodes()
            .Where(static node => IsStatement(node.Kind))
            .OrderBy(static node => node.Span.Start)
            .ThenBy(static node => node.Span.Length)
            .ToArray();
        var builders = new Dictionary<TextSpan, ImmutableArray<LuaAnnotationSyntax>.Builder>();
        foreach (var annotation in _annotations.Annotations.Where(static item => item is
                     LuaTypeAnnotationSyntax or
                     LuaParamAnnotationSyntax or
                     LuaReturnAnnotationSyntax or
                     LuaGenericAnnotationSyntax or
                     LuaOverloadAnnotationSyntax or
                     LuaVarargAnnotationSyntax or
                     LuaCastAnnotationSyntax))
        {
            // attachable is ordered by span start, so a lower-bound binary search finds
            // the first statement at or after the annotation without scanning the list.
            var low = 0;
            var high = attachable.Length - 1;
            var targetIndex = -1;
            while (low <= high)
            {
                var middle = (int)((uint)low + (uint)high) / 2;
                if (attachable[middle].Span.Start >= annotation.Span.End)
                {
                    targetIndex = middle;
                    high = middle - 1;
                }
                else
                {
                    low = middle + 1;
                }
            }

            if (targetIndex < 0)
            {
                continue;
            }

            var target = attachable[targetIndex];
            if (!builders.TryGetValue(target.Span, out var builder))
            {
                builder = ImmutableArray.CreateBuilder<LuaAnnotationSyntax>();
                builders.Add(target.Span, builder);
            }

            builder.Add(annotation);
        }

        return builders.ToDictionary(
            static pair => pair.Key,
            static pair => pair.Value.OrderBy(static item => item.Span.Start).ToImmutableArray());
    }

    private ImmutableArray<LuaAnnotationSyntax> GetAnnotations(LuaSyntaxNode node) =>
        _attachedAnnotations.GetValueOrDefault(node.Span, []);

    private static bool IsStatement(LuaSyntaxKind kind) => kind is
        LuaSyntaxKind.EmptyStatement or
        LuaSyntaxKind.AssignmentStatement or
        LuaSyntaxKind.CallStatement or
        LuaSyntaxKind.LabelStatement or
        LuaSyntaxKind.BreakStatement or
        LuaSyntaxKind.GotoStatement or
        LuaSyntaxKind.DoStatement or
        LuaSyntaxKind.WhileStatement or
        LuaSyntaxKind.RepeatStatement or
        LuaSyntaxKind.IfStatement or
        LuaSyntaxKind.NumericForStatement or
        LuaSyntaxKind.GenericForStatement or
        LuaSyntaxKind.FunctionDeclarationStatement or
        LuaSyntaxKind.GlobalDeclarationStatement or
        LuaSyntaxKind.LocalFunctionDeclarationStatement or
        LuaSyntaxKind.LocalDeclarationStatement or
        LuaSyntaxKind.ReturnStatement;

    private sealed record FunctionSyntax(
        LuaSyntaxNode Owner,
        LuaSyntaxNode Body,
        LuaSyntaxNode? Parameters,
        bool HasImplicitSelf);

    private sealed class FunctionAnalysisContext(
        int functionId,
        LuaFunctionType type,
        LuaTypePack? expectedReturns,
        bool hasExplicitReturns)
    {
        public int FunctionId { get; } = functionId;

        public LuaFunctionType Type { get; } = type;

        public LuaTypePack? ExpectedReturns { get; } = expectedReturns;

        public bool HasExplicitReturns { get; } = hasExplicitReturns;

        public List<LuaTypePack> Returns { get; } = [];

        public int FlowIterations { get; set; }

        public bool WasWidened { get; set; }
    }

    private readonly record struct FunctionSpecification(
        LuaFunctionType Primary,
        ImmutableArray<LuaFunctionType> Overloads,
        LuaType ValueType,
        LuaTypePack? ExpectedReturns,
        bool HasExplicitReturns);

    private readonly record struct VariableKey(int SymbolId, string? GlobalName)
    {
        public bool IsGlobal => GlobalName is not null;

        public static VariableKey Local(int symbolId) => new(symbolId, null);

        public static VariableKey Global(string name) => new(-1, name);
    }

    private readonly record struct AccessPathKey(string Value, int HopCount);

    /// <summary>
    /// Mutable flow-analysis state. Global bindings read through the versioned global
    /// table as of the state's creation version; only function-local entries and
    /// global overrides are stored per state, so cloning stays proportional to the
    /// overlay instead of the whole global environment.
    /// </summary>
    private sealed class FlowState
    {
        private readonly VersionedGlobalTypeTable? _globalTable;
        private readonly int _globalBaseVersion;
        private readonly Dictionary<VariableKey, LuaType> _types = [];
        private readonly HashSet<VariableKey> _assigned = [];
        private readonly HashSet<string> _unassignedGlobals = [];
        private bool _reachable = true;

        public FlowState(VersionedGlobalTypeTable globalTable, int globalBaseVersion)
        {
            _globalTable = globalTable;
            _globalBaseVersion = globalBaseVersion;
        }

        private FlowState(
            VersionedGlobalTypeTable? globalTable,
            int globalBaseVersion,
            bool reachable)
        {
            _globalTable = globalTable;
            _globalBaseVersion = globalBaseVersion;
            _reachable = reachable;
        }

        public bool Reachable
        {
            get => _reachable;
            set => _reachable = value;
        }

        public Dictionary<AccessPathKey, LuaType> PathTypes { get; } = [];

        public bool TryGetType(VariableKey key, [MaybeNullWhen(false)] out LuaType type)
        {
            if (_types.TryGetValue(key, out type))
            {
                return true;
            }

            if (key.IsGlobal && _globalTable is not null &&
                _globalTable.TryGet(key.GlobalName!, _globalBaseVersion, out type))
            {
                return true;
            }

            return false;
        }

        public LuaType TypeOf(VariableKey key, LuaType fallback)
        {
            return TryGetType(key, out var type) ? type : fallback;
        }

        public void SetType(VariableKey key, LuaType value) => _types[key] = value;

        public bool ContainsType(VariableKey key) => TryGetType(key, out _);

        /// <summary>
        /// Enumerates every tracked type key: the per-state overlay first, then the
        /// global names visible at this state's base version. Enumeration order
        /// matches lookup precedence.
        /// </summary>
        public IEnumerable<VariableKey> EnumerateTypeKeys()
        {
            foreach (var pair in _types)
            {
                yield return pair.Key;
            }

            if (_globalTable is not null)
            {
                foreach (var name in _globalTable.EnumerateNames(_globalBaseVersion))
                {
                    yield return VariableKey.Global(name);
                }
            }
        }

        /// <summary>
        /// Global keys visible only through the base version, excluding keys the
        /// overlay already shadows. Table-mutation propagation scans these after
        /// the overlay and only when the mutated table is globally published.
        /// </summary>
        public IEnumerable<VariableKey> EnumerateGlobalBaseKeys()
        {
            if (_globalTable is null)
            {
                yield break;
            }

            foreach (var name in _globalTable.EnumerateNames(_globalBaseVersion))
            {
                var key = VariableKey.Global(name);
                if (!_types.ContainsKey(key))
                {
                    yield return key;
                }
            }
        }

        /// <summary>Entries written into this state, excluding the shared global base.</summary>
        public IEnumerable<KeyValuePair<VariableKey, LuaType>> OverlayTypes => _types;

        public bool IsAssigned(VariableKey key) => key.IsGlobal
            ? _assigned.Contains(key) ||
              (!_unassignedGlobals.Contains(key.GlobalName!) &&
               _globalTable is not null &&
               _globalTable.TryGet(key.GlobalName!, _globalBaseVersion, out _))
            : _assigned.Contains(key);

        public void MarkAssigned(VariableKey key)
        {
            _assigned.Add(key);
            if (key.IsGlobal)
            {
                _unassignedGlobals.Remove(key.GlobalName!);
            }
        }

        public void UnmarkAssigned(VariableKey key)
        {
            _assigned.Remove(key);
            if (key.IsGlobal && _globalTable is not null &&
                _globalTable.TryGet(key.GlobalName!, _globalBaseVersion, out _))
            {
                _unassignedGlobals.Add(key.GlobalName!);
            }
        }

        /// <summary>
        /// Replaces the assigned set with the intersection of the effective assigned
        /// sets of the input states, preserving global tombstones where some state
        /// explicitly unassigned a base global.
        /// </summary>
        public void IntersectAssigned(IReadOnlyList<FlowState> states)
        {
            _assigned.Clear();
            _assigned.UnionWith(states[0]._assigned);
            for (var index = 1; index < states.Count; index++)
            {
                _assigned.IntersectWith(states[index]._assigned);
            }

            var candidates = new HashSet<string>(StringComparer.Ordinal);
            foreach (var state in states)
            {
                foreach (var key in state._assigned)
                {
                    if (key.IsGlobal)
                    {
                        candidates.Add(key.GlobalName!);
                    }
                }

                candidates.UnionWith(state._unassignedGlobals);
            }

            foreach (var name in candidates)
            {
                var all = true;
                foreach (var state in states)
                {
                    if (!state.IsAssigned(VariableKey.Global(name)))
                    {
                        all = false;
                        break;
                    }
                }

                if (all)
                {
                    _unassignedGlobals.Remove(name);
                }
                else
                {
                    _assigned.Remove(VariableKey.Global(name));
                    if (_globalTable is not null &&
                        _globalTable.TryGet(name, _globalBaseVersion, out _))
                    {
                        _unassignedGlobals.Add(name);
                    }
                }
            }
        }

        public bool AssignedSetEquals(FlowState other)
        {
            if (!ReferenceEquals(_globalTable, other._globalTable) ||
                _globalBaseVersion != other._globalBaseVersion)
            {
                return MaterializeAssigned().SetEquals(other.MaterializeAssigned());
            }

            if (!_unassignedGlobals.SetEquals(other._unassignedGlobals))
            {
                return false;
            }

            return NormalizeAssignedOverlay().SetEquals(other.NormalizeAssignedOverlay());
        }

        private HashSet<VariableKey> NormalizeAssignedOverlay()
        {
            if (_globalTable is null)
            {
                return _assigned;
            }

            var normalized = new HashSet<VariableKey>();
            foreach (var key in _assigned)
            {
                if (!key.IsGlobal ||
                    !_globalTable.TryGet(key.GlobalName!, _globalBaseVersion, out _))
                {
                    normalized.Add(key);
                }
            }

            return normalized;
        }

        private HashSet<VariableKey> MaterializeAssigned()
        {
            var effective = new HashSet<VariableKey>(_assigned);
            if (_globalTable is not null)
            {
                foreach (var name in _globalTable.EnumerateNames(_globalBaseVersion))
                {
                    if (!_unassignedGlobals.Contains(name))
                    {
                        effective.Add(VariableKey.Global(name));
                    }
                }
            }

            return effective;
        }

        public FlowState Clone()
        {
            var clone = new FlowState(_globalTable, _globalBaseVersion, _reachable);
            foreach (var pair in _types)
            {
                clone._types.Add(pair.Key, pair.Value);
            }

            clone._assigned.UnionWith(_assigned);
            clone._unassignedGlobals.UnionWith(_unassignedGlobals);
            foreach (var pair in PathTypes)
            {
                clone.PathTypes.Add(pair.Key, pair.Value);
            }

            return clone;
        }

        /// <summary>
        /// Replaces this state's contents with the source state's effective contents.
        /// When the states share one global table version only the overlay is copied;
        /// otherwise the source's base bindings are materialized into the overlay.
        /// </summary>
        public void CopyFrom(FlowState source)
        {
            _reachable = source._reachable;
            _types.Clear();
            _assigned.Clear();
            _unassignedGlobals.Clear();
            PathTypes.Clear();
            if (ReferenceEquals(_globalTable, source._globalTable) &&
                _globalBaseVersion == source._globalBaseVersion)
            {
                foreach (var pair in source._types)
                {
                    _types.Add(pair.Key, pair.Value);
                }

                _assigned.UnionWith(source._assigned);
                _unassignedGlobals.UnionWith(source._unassignedGlobals);
            }
            else
            {
                foreach (var pair in source._types)
                {
                    _types.Add(pair.Key, pair.Value);
                }

                if (source._globalTable is not null)
                {
                    foreach (var name in source._globalTable.EnumerateNames(source._globalBaseVersion))
                    {
                        if (source._globalTable.TryGet(name, source._globalBaseVersion, out var value))
                        {
                            _types[VariableKey.Global(name)] = value;
                            if (!source._unassignedGlobals.Contains(name))
                            {
                                _assigned.Add(VariableKey.Global(name));
                            }
                        }
                    }
                }

                foreach (var key in source._assigned)
                {
                    if (!key.IsGlobal)
                    {
                        _assigned.Add(key);
                    }
                }

                _unassignedGlobals.UnionWith(source._unassignedGlobals);
            }

            foreach (var pair in source.PathTypes)
            {
                PathTypes.Add(pair.Key, pair.Value);
            }
        }
    }

    private sealed class LuaTypeReferenceComparer : IEqualityComparer<LuaType>
    {
        public static LuaTypeReferenceComparer Instance { get; } = new();

        public bool Equals(LuaType? x, LuaType? y) => ReferenceEquals(x, y);

        public int GetHashCode(LuaType obj) => RuntimeHelpers.GetHashCode(obj);
    }

    private sealed class UpvalueCellState(LuaSymbol symbol, LuaType type)
    {
        public LuaSymbol Symbol { get; } = symbol;

        public LuaType Type { get; set; } = type;

        public HashSet<int> Readers { get; } = [];

        public HashSet<int> Writers { get; } = [];

        public bool Escapes { get; set; }
    }

    private readonly record struct BlockResult(FlowState Fallthrough, List<FlowState> Breaks)
    {
        public static BlockResult Next(FlowState state) => new(state, []);
    }
}
