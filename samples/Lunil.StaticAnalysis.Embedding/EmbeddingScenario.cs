using System.Globalization;
using System.Text;
using Lunil.Analysis;
using Lunil.Compiler;
using Lunil.Core;
using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;
using Lunil.Workspace;

namespace Lunil.StaticAnalysis.Embedding;

public sealed record EmbeddingRun(
    LuaCompilationResult SingleFile,
    LuaWorkspaceResult InitialWorkspace,
    LuaWorkspaceResult CachedWorkspace,
    LuaWorkspaceResult ChangedWorkspace);

public static class EmbeddingScenario
{
    private const string SingleFileSource = """
        ---@class Player
        ---@field name string
        ---@alias PlayerId integer
        ---@enum Team
        local Team = { Red = 1, Blue = 2 }

        ---@param player Player
        ---@return string
        local function greet(player)
            local prefix = "你好，"
            return prefix .. player.name
        end

        local player = { name = "Luna" }
        return greet(player), Team.Red
        """;

    public static async Task<EmbeddingRun> RunAsync(
        TextWriter output,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(output);

        var compilerOptions = LuaCompilerOptions.Default with
        {
            LanguageVersion = LuaLanguageVersion.Lua54,
            Analysis = LuaAnalysisOptions.Default with
            {
                ReportUnknownGlobals = true,
                ReportImplicitAny = true,
                MaximumTypeCount = 20_000,
                MaximumConstraintCount = 40_000,
                MaximumControlFlowBlockCount = 10_000,
            },
        };

        var compilation = new LuaCompiler(compilerOptions).CompileUtf8(
            SingleFileSource,
            "@samples/player.lua",
            cancellationToken);
        PrintCompilation(output, compilation, "sample.single");

        var workspaceOptions = LuaWorkspaceOptions.Default with
        {
            LanguageVersion = LuaLanguageVersion.Lua54,
            Compiler = compilerOptions,
            MaximumModuleCount = 128,
            MaximumDependencyCount = 512,
            MaximumSourceBytes = 4 * 1024 * 1024,
            MaximumParallelism = 4,
            MaximumFixedPointIterations = 8,
            MaximumCacheEntryCount = 512,
            MaximumDiagnosticCount = 1_000,
        };

        using var workspace = new LuaWorkspace(workspaceOptions);
        var initialDocuments = CreateWorkspaceDocuments("42");
        var initial = await workspace.AnalyzeAsync(initialDocuments, cancellationToken)
            .ConfigureAwait(false);
        var cached = await workspace.AnalyzeAsync(initialDocuments.Reverse(), cancellationToken)
            .ConfigureAwait(false);
        var changed = await workspace.AnalyzeAsync(
            CreateWorkspaceDocuments("'changed'"),
            cancellationToken).ConfigureAwait(false);

        PrintWorkspace(output, "initial", initial);
        PrintWorkspace(output, "cached", cached);
        PrintWorkspace(output, "changed", changed);

        return new EmbeddingRun(compilation, initial, cached, changed);
    }

    private static LuaWorkspaceDocument[] CreateWorkspaceDocuments(string serviceExport) =>
    [
        LuaWorkspaceDocument.FromUtf8(
            "game.client",
            "local service = require('game.service')\nreturn 'client'",
            "@game/client.lua"),
        LuaWorkspaceDocument.FromUtf8(
            "game.service",
            $"local client = require('game.client')\nreturn {serviceExport}",
            "@game/service.lua"),
    ];

    private static void PrintCompilation(
        TextWriter output,
        LuaCompilationResult result,
        string moduleIdentity)
    {
        var source = result.Source.Text;
        output.WriteLine("== single-file compilation ==");
        output.WriteLine(
            $"source={result.Source.SourceName} bytes={source.Length} lines={source.LineCount} " +
            $"language={LuaLanguageVersions.GetDisplayName(result.LanguageVersion)} " +
            $"succeeded={result.Succeeded}");

        output.WriteLine("-- annotations and declarations --");
        foreach (var annotation in result.Annotations.Annotations)
        {
            var stableKey = annotation is LuaClassAnnotationSyntax or
                LuaAliasAnnotationSyntax or LuaEnumAnnotationSyntax
                ? result.GetAnnotationKey(annotation, moduleIdentity).Value
                : "-";
            output.WriteLine(
                $"annotation tag={annotation.Tag} span={FormatSpan(source, annotation.Span)} key={stableKey}");
        }

        foreach (var declaration in result.Analysis.TypeDeclarations)
        {
            output.WriteLine(
                $"type-declaration kind={declaration.Kind} name={declaration.Name} " +
                $"span={FormatSpan(source, declaration.Span)}");
        }

        output.WriteLine("-- symbols, references, and types --");
        foreach (var symbol in result.SemanticModel.Symbols)
        {
            var key = result.SemanticModel.GetSymbolKey(symbol, moduleIdentity);
            var type = result.Analysis.Symbols.FirstOrDefault(info => ReferenceEquals(info.Symbol, symbol));
            var indexedReferences = result.SemanticModel.FindReferences(symbol);
            output.WriteLine(
                $"symbol id={symbol.Id} function={symbol.FunctionId} kind={symbol.Kind} name={symbol.Name} " +
                $"span={FormatSpan(source, symbol.DeclaringSpan)} key={key.Value} " +
                $"type={type?.InferredType.DisplayName ?? "not-analyzed"} assigned={type?.IsDefinitelyAssigned} " +
                $"references={indexedReferences.Length}");
        }

        foreach (var reference in result.SemanticModel.References)
        {
            var target = result.SemanticModel.GetSymbolKey(reference.Symbol, moduleIdentity);
            output.WriteLine(
                $"reference name={reference.Name} resolution={reference.ResolutionKind} " +
                $"access={(reference.IsWrite ? "write" : "read")} " +
                $"span={FormatSpan(source, reference.Span)} target={target.Value}");
        }

        foreach (var expression in result.Analysis.Expressions.Take(12))
        {
            output.WriteLine(
                $"expression span={FormatSpan(source, expression.Span)} type={expression.Type.DisplayName}");
        }

        output.WriteLine("-- functions, CFG, and calls --");
        foreach (var function in result.Analysis.Functions)
        {
            var semanticFunction = result.SemanticModel.Functions.Single(item => item.Id == function.FunctionId);
            var key = result.SemanticModel.GetFunctionKey(semanticFunction, moduleIdentity);
            var cfg = function.ControlFlowGraph;
            output.WriteLine(
                $"function id={function.FunctionId} key={key.Value} type={function.Type.DisplayName} " +
                $"returns={function.InferredReturns.DisplayName} blocks={cfg.Blocks.Length} " +
                $"reachable={cfg.Blocks.Count(static block => block.IsReachable)} " +
                $"iterations={function.FlowIterationCount} widened={function.WasWidened}");
        }

        foreach (var edge in result.Analysis.CallGraph.Edges)
        {
            output.WriteLine(
                $"call caller={edge.ContainingFunctionId} kind={edge.Kind} " +
                $"status={edge.ResolutionStatus} " +
                $"target={edge.TargetFunctionId?.ToString(CultureInfo.InvariantCulture) ?? "-"} " +
                $"span={FormatSpan(source, edge.Span)} reason={edge.UnresolvedReason ?? "-"}");
        }

        output.WriteLine("-- diagnostics and budgets --");
        foreach (var diagnostic in result.Diagnostics)
        {
            output.WriteLine(
                $"diagnostic phase={diagnostic.Phase} code={diagnostic.Code} " +
                $"severity={diagnostic.Severity} span={FormatSpan(source, diagnostic.Span)}");
        }

        var budget = result.Analysis.BudgetUsage;
        output.WriteLine(
            $"analysis-budget types={budget.TypeCount} constraints={budget.ConstraintCount} " +
            $"cfg-blocks={budget.ControlFlowBlockCount} generics={budget.GenericInstantiationCount} " +
            $"depth={budget.MaximumObservedTypeDepth} exceeded={budget.WasExceeded}");
    }

    private static void PrintWorkspace(
        TextWriter output,
        string snapshot,
        LuaWorkspaceResult result)
    {
        output.WriteLine($"== workspace snapshot: {snapshot} ==");
        var metrics = result.Metrics;
        output.WriteLine(
            $"metrics discovered={metrics.DiscoveredModuleCount} analyzed={metrics.AnalyzedModuleCount} " +
            $"hits={metrics.CacheHitCount} misses={metrics.CacheMissCount} " +
            $"invalidated={metrics.InvalidatedModuleCount} fixed-point={metrics.FixedPointIterationCount} " +
            $"parallelism={metrics.PeakParallelism}");

        foreach (var component in result.Graph.Components)
        {
            output.WriteLine(
                $"component id={component.Id} cyclic={component.IsCyclic} " +
                $"modules={string.Join(',', component.Modules.Select(static module => module.Name))}");
        }

        foreach (var module in result.Modules)
        {
            output.WriteLine(
                $"module={module.Identity.Name} source={module.SourceIdentity} " +
                $"content-hash={module.ContentHash} export={module.ExportedType.DisplayName} " +
                $"export-hash={module.ExportHash} iterations={module.FixedPointIterationCount} " +
                $"cache-hit={module.WasCacheHit} widened={module.WasWidened}");

            var symbol = module.Compilation.SemanticModel.Symbols.FirstOrDefault(static item =>
                item.Kind != LuaSymbolKind.Environment);
            if (symbol is not null)
            {
                var key = module.Compilation.SemanticModel.GetSymbolKey(symbol, module.Identity);
                output.WriteLine(
                    $"workspace-reference-key={key.Value} references={result.FindReferences(key).Length}");
            }
        }

        foreach (var diagnostic in result.Diagnostics)
        {
            output.WriteLine(
                $"workspace-diagnostic phase={diagnostic.Phase} " +
                $"compilation-phase={diagnostic.CompilationPhase?.ToString() ?? "-"} " +
                $"module={diagnostic.Module?.Name ?? "-"} code={diagnostic.Code} " +
                $"severity={diagnostic.Severity}");
        }

        var callGraph = result.GetCallGraph();
        output.WriteLine(
            $"workspace-index functions={callGraph.Functions.Length} calls={callGraph.Edges.Length}");
    }

    private static string FormatSpan(SourceText source, TextSpan span)
    {
        var start = source.GetLocation(span.Start);
        var end = source.GetLocation(span.End);
        return $"bytes[{span.Start}..{span.End}) " +
            $"utf16[{start.Line + 1}:{start.Utf16Column + 1}..{end.Line + 1}:{end.Utf16Column + 1})";
    }
}
