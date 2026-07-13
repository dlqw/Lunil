using System.Collections.Immutable;
using System.Text;
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
    private readonly AnnotationTypeEnvironment _types;
    private readonly LuaTypeRelations _relations;
    private readonly ImmutableArray<LuaControlFlowGraph> _graphs;
    private readonly LuaAnalysisContext _context;
    private readonly Dictionary<TextSpan, LuaNameReference> _references;
    private readonly Dictionary<TextSpan, LuaSymbol> _declarations;
    private readonly Dictionary<int, FunctionSyntax> _functionSyntax;
    private readonly Dictionary<TextSpan, int> _functionIdsByOwnerSpan;
    private readonly Dictionary<TextSpan, ImmutableArray<LuaAnnotationSyntax>> _attachedAnnotations;
    private readonly Dictionary<VariableKey, LuaType> _declaredTypes = [];
    private readonly Dictionary<int, LuaType> _symbolInferences = [];
    private readonly Dictionary<TextSpan, LuaType> _expressionInferences = [];
    private readonly Dictionary<string, LuaType> _globalTypes = new(StringComparer.Ordinal);
    private readonly Dictionary<int, LuaType> _functionValueTypes = [];
    private readonly Dictionary<int, LuaFunctionAnalysis> _functionAnalyses = [];
    private readonly HashSet<int> _functionsInProgress = [];
    private readonly HashSet<string> _reportedUnknownGlobals = new(StringComparer.Ordinal);
    private readonly HashSet<TextSpan> _countedExpressionTypes = [];
    private readonly HashSet<int> _definitelyAssignedSymbols = [];
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
        _types = types;
        _relations = types.Relations;
        _graphs = graphs;
        _context = context;
        _references = semantics.References
            .GroupBy(static reference => reference.Span)
            .ToDictionary(static group => group.Key, static group => group.Last());
        _declarations = semantics.Symbols
            .GroupBy(static symbol => symbol.DeclaringSpan)
            .ToDictionary(static group => group.Key, static group => group.Last());
        (_functionSyntax, _functionIdsByOwnerSpan) = BuildFunctionIndex();
        _attachedAnnotations = AttachAnnotations();
        InstallBuiltIns();
    }

    public LuaAnalysisResult Analyze()
    {
        AnalyzeFunction(0, []);
        foreach (var function in _semantics.Functions.OrderBy(static item => item.Id))
        {
            AnalyzeFunction(function.Id, GetAnnotations(_functionSyntax[function.Id].Owner));
        }

        ReportUnreachableCode();
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
            _context.GetBudgetUsage());
    }

    private LuaType AnalyzeFunction(
        int functionId,
        ImmutableArray<LuaAnnotationSyntax> annotations)
    {
        if (_functionValueTypes.TryGetValue(functionId, out var cached) &&
            !_functionsInProgress.Contains(functionId))
        {
            return cached;
        }

        if (!_functionsInProgress.Add(functionId))
        {
            return _functionValueTypes.GetValueOrDefault(functionId, LuaTypes.Function);
        }

        var syntax = _functionSyntax[functionId];
        var functionInfo = _semantics.Functions.Single(item => item.Id == functionId);
        var specification = BuildFunctionSpecification(functionInfo, syntax, annotations);
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
                         result.Fallthrough.Assigned.Contains(VariableKey.Local(symbol.Id))))
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
            var graph = _graphs.Single(item => item.FunctionId == functionId);
            _functionAnalyses[functionId] = new LuaFunctionAnalysis(
                functionId,
                primary,
                inferredReturns,
                graph,
                functionContext.FlowIterations,
                functionContext.WasWidened);
            return valueType;
        }
        finally
        {
            _currentFunction = previous;
            _functionsInProgress.Remove(functionId);
        }
    }

    private FlowState CreateInitialState(
        LuaFunctionInfo function,
        FunctionSpecification specification)
    {
        var state = new FlowState(_globalTypes);
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
            state.Types[key] = type;
            state.Assigned.Add(key);
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
            state.Types[key] = type;
            state.Assigned.Add(key);
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
        bySpan[_semantics.Functions.Single(static function => function.Id == 0).Span] = 0;
        var owners = _semantics.Syntax.Root.DescendantNodes()
            .Where(static node => node.Kind is
                LuaSyntaxKind.FunctionDeclarationStatement or
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
            var target = attachable.FirstOrDefault(node => node.Span.Start >= annotation.Span.End);
            if (target is null)
            {
                continue;
            }

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

    private sealed class FlowState
    {
        public FlowState(IReadOnlyDictionary<string, LuaType> globals)
        {
            foreach (var pair in globals)
            {
                Types[VariableKey.Global(pair.Key)] = pair.Value;
                Assigned.Add(VariableKey.Global(pair.Key));
            }
        }

        private FlowState()
        {
        }

        public Dictionary<VariableKey, LuaType> Types { get; } = [];

        public HashSet<VariableKey> Assigned { get; } = [];

        public bool Reachable { get; set; } = true;

        public FlowState Clone()
        {
            var clone = new FlowState { Reachable = Reachable };
            foreach (var pair in Types)
            {
                clone.Types.Add(pair.Key, pair.Value);
            }

            clone.Assigned.UnionWith(Assigned);
            return clone;
        }
    }

    private readonly record struct BlockResult(FlowState Fallthrough, List<FlowState> Breaks)
    {
        public static BlockResult Next(FlowState state) => new(state, []);
    }
}
