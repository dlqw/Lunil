using Lunil.Core.Diagnostics;
using Lunil.EmmyLua;
using Lunil.Compiler;
using Lunil.Analysis;
using System.Collections.Immutable;
using System.Runtime.CompilerServices;

namespace Lunil.Workspace.Tests;

public sealed class LuaWorkspaceTests
{
    [Fact]
    public async Task ResolvesStaticRequiresAndPropagatesExportTypes()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("app", "local dep = require('dep')\nreturn dep.value + 1"),
            Document("dep", "return { value = 42 }"),
        ]);

        Assert.Equal(2, result.Graph.Nodes.Length);
        var edge = Assert.Single(result.Graph.Dependencies);
        Assert.Equal(LuaModuleDependencyKind.Static, edge.Kind);
        Assert.Equal("dep", edge.Target?.Name);
        Assert.DoesNotContain(result.GetModule("app")!.Compilation.Diagnostics, static diagnostic =>
            diagnostic.Code is "LUA6003" or "LUA6007");
        Assert.Equal("integer", result.GetModule("app")!.ExportedType.DisplayName);
    }

    [Fact]
    public async Task TypedDependencyAnalysisReusesDiscoveryFrontEndProducts()
    {
        using var workspace = new LuaWorkspace();

        var result = await workspace.AnalyzeAsync([
            Document("app", "local dep = require('dep')\nreturn dep.value + 1"),
            Document("dep", "return { value = 42 }"),
        ]);

        foreach (var module in result.Modules)
        {
            var snapshot = Assert.IsType<LuaFrontEndSnapshot>(
                module.Compilation.FrontEndSnapshot);
            foreach (var operation in Enum.GetValues<LuaFrontEndOperation>())
            {
                Assert.Equal(
                    1,
                    snapshot.Metrics.Count(metric => metric.Operation == operation));
            }
        }
    }

    [Fact]
    public async Task TypedDependencyWalkPreservesShorthandAndExcludesMethodCalls()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "app",
                "local object = { require = function() end }\n" +
                "object:require('ignored')\nreturn require 'dep'"),
            Document("dep", "return 42"),
        ]);

        var dependency = Assert.Single(result.Graph.Dependencies);
        Assert.Equal(LuaModuleDependencyKind.Static, dependency.Kind);
        Assert.Equal("dep", dependency.Target?.Name);
    }

    [Fact]
    public async Task StableKeysSurviveWorkspaceReanalysisWithUnrelatedEdits()
    {
        using var workspace = new LuaWorkspace();
        var first = await workspace.AnalyzeAsync([
            Document("app", "---@class Player\nlocal stable = 1\nreturn stable"),
        ]);
        var firstModule = Assert.Single(first.Modules);
        var firstSymbol = Assert.Single(firstModule.Compilation.SemanticModel.Symbols, symbol =>
            symbol.Name == "stable");
        var firstKey = firstModule.Compilation.SemanticModel.GetSymbolKey(
            firstSymbol,
            firstModule.Identity);
        var firstAnnotation = Assert.Single(
            firstModule.Compilation.Annotations.Annotations.OfType<LuaClassAnnotationSyntax>());
        var firstAnnotationKey = firstModule.Compilation.GetAnnotationKey(
            firstAnnotation,
            firstModule.Identity);

        var second = await workspace.AnalyzeAsync([
            Document(
                "app",
                "-- comment\n---@alias Other integer\n---@class Player\n" +
                "local unrelated = 0\nlocal stable = 1\nreturn stable"),
        ]);
        var secondModule = Assert.Single(second.Modules);
        var secondSymbol = Assert.Single(secondModule.Compilation.SemanticModel.Symbols, symbol =>
            symbol.Name == "stable");

        Assert.Equal(
            firstKey,
            secondModule.Compilation.SemanticModel.GetSymbolKey(
                secondSymbol,
                secondModule.Identity));
        Assert.Same(
            secondSymbol,
            secondModule.Compilation.SemanticModel.ResolveSymbolKey(firstKey, secondModule.Identity));
        var secondAnnotation = Assert.Single(
            secondModule.Compilation.Annotations.Annotations.OfType<LuaClassAnnotationSyntax>());
        Assert.Equal(
            firstAnnotationKey,
            secondModule.Compilation.GetAnnotationKey(secondAnnotation, secondModule.Identity));
        Assert.Same(
            secondAnnotation,
            secondModule.Compilation.ResolveAnnotationKey(firstAnnotationKey, secondModule.Identity));
    }

    [Fact]
    public async Task WorkspaceReferenceIndexUsesStableTargetsAndContainingFunctions()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "app",
                "local value = 1\n" +
                "local function read() return value end\n" +
                "return value, read()"),
            Document("other", "return missing_global"),
        ]);
        var app = result.GetModule("app")!;
        var value = Assert.Single(app.Compilation.SemanticModel.Symbols, symbol =>
            symbol.Name == "value");
        var key = app.Compilation.SemanticModel.GetSymbolKey(value, app.Identity);

        var references = result.FindReferences(key);
        Assert.Equal(2, references.Length);
        Assert.All(references, reference => Assert.Equal(key, reference.TargetKey));
        Assert.Equal(2, references.Select(static reference => reference.ContainingFunctionId).Distinct().Count());
        Assert.All(references, static reference =>
            Assert.False(string.IsNullOrWhiteSpace(reference.ContainingFunctionKey.Value)));

        var globals = result.FindGlobalReferences("missing_global");
        var global = Assert.Single(globals);
        Assert.Equal("other", global.Module.Name);
        Assert.Null(global.TargetKey);
        Assert.Equal(Lunil.Semantics.Binding.LuaNameResolutionKind.Global, global.ResolutionKind);
    }

    [Fact]
    public async Task WorkspaceCallGraphProjectsModuleAliasesExportsAndStableFunctions()
    {
        using var workspace = new LuaWorkspace();
        LuaWorkspaceDocument[] documents = [
            Document(
                "app",
                "local dep = require('dep')\n" +
                "dep.run()\n" +
                "require('dep').run()\n" +
                "local function local_target() return 1 end\n" +
                "return local_target()"),
            Document("dep", "return { run = function() return 42 end }"),
        ];
        var first = await workspace.AnalyzeAsync(documents);
        var second = await workspace.AnalyzeAsync(documents);

        var graph = first.GetCallGraph();
        var moduleCalls = graph.Edges.Where(static edge =>
            edge.TargetModule?.Name == "dep").ToArray();
        Assert.Equal(4, moduleCalls.Length);
        Assert.Equal(
            2,
            moduleCalls.Count(static edge => edge.TargetExportName == "run"));
        Assert.Contains(moduleCalls, static edge => edge.Site.ModuleRequest == "dep");

        var localCall = Assert.Single(graph.Edges, edge =>
            edge.Site.DirectSymbol?.Name == "local_target");
        Assert.NotNull(localCall.TargetFunctionKey);
        Assert.Equal("app", localCall.Module.Name);
        Assert.Null(localCall.TargetModule);
        var cachedGraph = second.GetCallGraph();
        Assert.Equal(
            graph.Functions.Select(static function =>
                (function.Module.Name, function.FunctionId, function.FunctionKey.Value)),
            cachedGraph.Functions.Select(static function =>
                (function.Module.Name, function.FunctionId, function.FunctionKey.Value)));
        Assert.Equal(
            graph.Edges.Select(static edge =>
                (edge.Module.Name,
                 edge.Site.Span,
                 edge.Site.ResolutionStatus,
                 TargetModule: edge.TargetModule?.Name,
                 edge.TargetExportName,
                 TargetFunction: edge.TargetFunctionKey?.Value)),
            cachedGraph.Edges.Select(static edge =>
                (edge.Module.Name,
                 edge.Site.Span,
                 edge.Site.ResolutionStatus,
                 TargetModule: edge.TargetModule?.Name,
                 edge.TargetExportName,
                 TargetFunction: edge.TargetFunctionKey?.Value)));
    }

    [Fact]
    public async Task ExportGraphBindsNestedAndLiteralIndexCallsToRealFunctionKeys()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "app",
                "local service = require('service')\n" +
                "service.api.run()\n" +
                "return service.api[\"run\"]()"),
            Document(
                "service",
                "return { api = { run = function() return 42 end } }"),
        ]);

        var exported = Assert.Single(result.GetModule("service")!.ExportedSymbols, static symbol =>
            symbol.Path == "api.run");
        Assert.Equal(LuaWorkspaceExportKind.Function, exported.Kind);
        Assert.NotNull(exported.FunctionKey);
        Assert.True(Lunil.Semantics.Binding.LuaSymbolKey.TryParse(exported.FunctionKey, out _));

        var calls = result.GetCallGraph().Edges.Where(static edge =>
            edge.TargetExportName == "api.run").ToArray();
        Assert.Equal(2, calls.Length);
        Assert.All(calls, call =>
        {
            Assert.Equal("service", call.TargetModule?.Name);
            Assert.Equal(exported.Key, call.TargetExportSymbolKey);
            Assert.Equal(exported.FunctionKey, call.TargetExportFunctionKey);
            Assert.Equal(exported.DefinitionSpan, call.ExternalDefinitionSpan);
            Assert.Equal(LuaWorkspaceBindingStatus.Resolved, call.WorkspaceResolutionStatus);
        });
    }

    [Fact]
    public async Task ModuleTableFunctionsAndReExportsPreserveDefinitionTargets()
    {
        using var workspace = new LuaWorkspace();
        LuaWorkspaceDocument[] documents = [
            Document("service", "local M = {}\nfunction M.run() return 42 end\nreturn M"),
            Document("facade", "return require('service')"),
            Document("app", "local api = require('facade')\nreturn api.run()"),
        ];

        var first = await workspace.AnalyzeAsync(documents);
        var second = await workspace.AnalyzeAsync(documents);
        var service = first.ExportGraph.Find("service", "run")!;
        var facade = first.ExportGraph.Find("facade", "run")!;
        Assert.True(facade.IsReExport);
        Assert.Equal(service.Key, facade.TargetKey);
        Assert.Equal(service.FunctionKey, facade.FunctionKey);
        Assert.Contains(first.ExportGraph.Edges, edge =>
            edge.SourceKey == facade.Key && edge.TargetKey == service.Key && edge.Kind == "re-export");

        var call = Assert.Single(first.GetCallGraph().Edges, edge =>
            edge.TargetExportSymbolKey == facade.Key);
        Assert.Equal(service.FunctionKey, call.TargetExportFunctionKey);
        Assert.Equal("facade", call.TargetModule?.Name);
        Assert.Equal(
            first.Modules.Select(static module =>
                (module.Identity.Name, module.ExportSymbolHash, module.FunctionSummaryHash,
                    module.AnalysisSummaryHash)),
            second.Modules.Select(static module =>
                (module.Identity.Name, module.ExportSymbolHash, module.FunctionSummaryHash,
                    module.AnalysisSummaryHash)));
    }

    [Fact]
    public async Task ReassignedModuleAliasesProduceAnExplicitUnresolvedReason()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "app",
                "local dep = require('dep')\n" +
                "dep = {}\n" +
                "return dep.run()"),
            Document("dep", "return { run = function() return 42 end }"),
        ]);

        var binding = Assert.Single(result.CallBindings.Edges);
        Assert.Equal(LuaWorkspaceBindingStatus.Unresolved, binding.Status);
        Assert.Equal("module-alias-reassigned", binding.Reason);
        Assert.Null(binding.TargetSymbolKey);
        var call = Assert.Single(result.GetCallGraph().Edges, edge =>
            edge.Site.MemberTarget?.Name == "run");
        Assert.Null(call.TargetModule);
        Assert.Equal("module-alias-reassigned", call.WorkspaceResolutionReason);
    }

    [Fact]
    public async Task DynamicExportsReturnBoundedCandidatesInsteadOfFalseTargets()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("app", "local dep = require('dep')\nreturn dep.run()"),
            Document("dep", "return unknown_factory()"),
        ]);

        var binding = Assert.Single(result.CallBindings.Edges);
        Assert.Equal(LuaWorkspaceBindingStatus.Dynamic, binding.Status);
        Assert.Equal("dynamic-export-candidate", binding.Reason);
        Assert.NotEmpty(binding.CandidateKeys);
        Assert.Null(binding.TargetSymbolKey);
    }

    [Fact]
    public async Task HostModulesBindWithoutResolverDiagnosticsAndProjectExternalLocations()
    {
        var functionType = new LuaHostTypeDescriptor
        {
            Kind = LuaHostTypeKind.Function,
            Returns = [new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Integer }],
        };
        var source = new LuaHostSourceLocation
        {
            Uri = "cpp://engine/native.hpp#run",
            Line = 12,
            Column = 7,
            ImplementationUri = "cpp-implementation://engine/native.cpp#run",
        };
        var contract = new LuaHostContractBuilder("workspace-host")
            .AddModule("native", new LuaHostTypeDescriptor
            {
                Kind = LuaHostTypeKind.Table,
                Fields = ImmutableDictionary<string, LuaHostTypeDescriptor>.Empty
                    .Add("run", functionType),
            })
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "native.run",
                Returns = functionType.Returns,
                Source = source,
            })
            .Build();
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions { HostContract = contract });

        var result = await workspace.AnalyzeAsync([
            Document("app", "local native = require('native')\nreturn native.run()"),
        ]);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7002");
        Assert.Equal(LuaModuleDependencyKind.Host, Assert.Single(result.Graph.Dependencies).Kind);
        var binding = Assert.Single(result.CallBindings.Edges);
        Assert.Equal(LuaWorkspaceBindingStatus.Resolved, binding.Status);
        Assert.Equal(source.Uri, binding.ExternalDefinition?.Uri);
        Assert.Equal(source.ImplementationUri, binding.ExternalImplementation?.Uri);
        Assert.StartsWith("host-function:workspace-host::native.run", binding.TargetFunctionKey,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task CallbackAndPersistenceFactsCreateNavigableGraphEdges()
    {
        var callback = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Function };
        var integer = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Integer };
        var contract = new LuaHostContractBuilder("effects-host")
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "host.subscribe",
                Parameters = [new LuaHostParameterContract { Name = "callback", Type = callback }],
                Effects = LuaHostEffectKind.RegistersCallback,
                Callback = new LuaHostCallbackContract { ParameterIndex = 0 },
            })
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "host.save",
                Parameters =
                [
                    new LuaHostParameterContract
                    {
                        Name = "key",
                        Type = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.String },
                    },
                    new LuaHostParameterContract { Name = "value", Type = integer },
                ],
                Effects = LuaHostEffectKind.WritesPersistence,
                Persistence = new LuaHostPersistenceContract
                {
                    Operation = LuaPersistenceOperationKind.Write,
                    KeyParameterIndex = 0,
                    ValueParameterIndex = 1,
                    SchemaId = "player",
                    SchemaVersion = 2,
                    ValueType = integer,
                },
            })
            .Build();
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions { HostContract = contract });

        LuaWorkspaceDocument[] documents = [
            Document("app", "host.subscribe(function() end)\nhost.save('score', 42)\nreturn true"),
        ];
        var result = await workspace.AnalyzeAsync(documents);

        Assert.Contains(result.ExportGraph.Symbols, static symbol =>
            symbol.ModuleName == "app" && symbol.Kind == LuaWorkspaceExportKind.Callback);
        Assert.Contains(result.ExportGraph.Symbols, static symbol =>
            symbol.ModuleName == "app" && symbol.Path == "$persistence-schema/player/2");
        Assert.Contains(result.ExportGraph.Edges, static edge => edge.Kind == "callback-registration");
        Assert.Contains(result.ExportGraph.Edges, static edge => edge.Kind == "persistence-schema");
        Assert.Contains(result.ExportGraph.Edges, static edge => edge.Kind == "persistence-access");
        var compact = await workspace.AnalyzeCompactAsync(documents);
        var subscribe = compact.ExportGraph.Find("$host:effects-host", "host.subscribe")!;
        Assert.NotEmpty(compact.FindCallsToExport(subscribe.Key));
        Assert.NotEmpty(compact.FindCallbackRegistrations(subscribe.Key));
        Assert.NotEmpty(compact.FindPersistenceSchemas("player"));
    }

    [Fact]
    public async Task PrototypeExportsExposeMethodsAsClassSymbols()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "class",
                "local Class = {}\n" +
                "Class.__index = Class\n" +
                "function Class:run() return 42 end\n" +
                "return Class"),
        ]);

        var root = result.ExportGraph.Find("class", string.Empty)!;
        Assert.Equal(LuaWorkspaceExportKind.Class, root.Kind);
        var method = result.ExportGraph.Find("class", "run")!;
        Assert.Equal(LuaWorkspaceExportKind.Function, method.Kind);
        Assert.NotNull(method.FunctionKey);
    }

    [Fact]
    public async Task CyclicReExportsProduceBoundedDeterministicEdges()
    {
        using var workspace = new LuaWorkspace();
        LuaWorkspaceDocument[] documents = [
            Document("a", "return require('b')"),
            Document("b", "return require('a')"),
        ];

        var first = await workspace.AnalyzeAsync(documents);
        var second = await workspace.AnalyzeAsync(documents);

        Assert.True(Assert.Single(first.Graph.Components).IsCyclic);
        Assert.Equal(2, first.ExportGraph.Edges.Count(static edge => edge.Kind == "re-export"));
        Assert.Equal(
            first.ExportGraph.Edges.Select(static edge => (edge.SourceKey, edge.TargetKey, edge.Kind)),
            second.ExportGraph.Edges.Select(static edge => (edge.SourceKey, edge.TargetKey, edge.Kind)));
        Assert.All(first.Modules, static module =>
        {
            Assert.NotEmpty(module.ExportSymbolHash);
            Assert.NotEmpty(module.FunctionSummaryHash);
            Assert.NotEmpty(module.AnalysisSummaryHash);
        });
    }

    [Fact]
    public async Task ReassignedModuleAliasDoesNotProduceAFalseMemberTarget()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "app",
                "local dep = require('dep')\n" +
                "dep = {}\n" +
                "return dep.run()"),
            Document("dep", "return { run = function() return 42 end }"),
        ]);

        var member = Assert.Single(result.GetCallGraph().Edges, edge =>
            edge.Site.MemberTarget?.Name == "run");
        Assert.Null(member.TargetModule);
        Assert.Null(member.TargetExportName);
    }

    [Fact]
    public async Task AnyModuleExportRemainsConservativeAcrossRequire()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("app", "return require('dep')"),
            Document("dep", "return unknown_factory()"),
        ]);

        Assert.Equal("any", result.GetModule("dep")!.ExportedType.DisplayName);
        Assert.Equal("any", result.GetModule("app")!.ExportedType.DisplayName);
    }

    [Fact]
    public async Task ShadowedRequireIsNotTreatedAsAModuleDependencyOrBuiltin()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "app",
                "local function require(name) return { value = name } end\n" +
                "local dep = require('not-a-module')\nreturn dep.value"),
        ]);

        Assert.Empty(result.Graph.Dependencies);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7002");
    }

    [Fact]
    public async Task DynamicAndUnresolvedRequiresRemainConservativeAndDiagnosable()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document(
                "app",
                "local name = 'dynamic'\nlocal a = require(name)\nlocal b = require('missing')\nreturn a or b"),
        ]);

        Assert.Contains(result.Graph.Dependencies, static dependency =>
            dependency.Kind == LuaModuleDependencyKind.Dynamic);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7002");
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7003");
    }

    [Fact]
    public async Task CyclicModulesReachADeterministicFixedPoint()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("a", "local peer = require('b')\nreturn 'a'"),
            Document("b", "local peer = require('a')\nreturn 42"),
        ]);

        var component = Assert.Single(result.Graph.Components);
        Assert.True(component.IsCyclic);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7005");
        Assert.True(result.Metrics.FixedPointIterationCount >= 2);
        Assert.Equal("'a'", result.GetModule("a")!.ExportedType.DisplayName);
        Assert.Equal("42", result.GetModule("b")!.ExportedType.DisplayName);
    }

    [Fact]
    public async Task NonConvergingCycleUsesBoundedWidening()
    {
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            MaximumFixedPointIterations = 1,
        });
        var result = await workspace.AnalyzeAsync([
            Document("a", "local peer = require('b')\nreturn { peer = peer, kind = 'a' }"),
            Document("b", "local peer = require('a')\nreturn { peer = peer, kind = 'b' }"),
        ]);

        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7005");
        Assert.All(result.Modules, static module => Assert.True(module.WasWidened));
        Assert.Equal(1, result.Metrics.FixedPointIterationCount);
    }

    [Fact]
    public async Task RepeatedSnapshotUsesContentAddressedAnalysisCache()
    {
        using var workspace = new LuaWorkspace();
        LuaWorkspaceDocument[] documents = [
            Document("app", "local dep = require('dep')\nreturn dep.value"),
            Document("dep", "return { value = 42 }"),
        ];

        var first = await workspace.AnalyzeAsync(documents);
        var second = await workspace.AnalyzeAsync(documents.Reverse());

        Assert.Equal(2, first.Metrics.CacheMissCount);
        Assert.Equal(0, first.Metrics.InvalidatedModuleCount);
        Assert.Equal(2, second.Metrics.CacheHitCount);
        Assert.Equal(0, second.Metrics.CacheMissCount);
        Assert.Equal(0, second.Metrics.InvalidatedModuleCount);
        Assert.All(second.Modules, static module => Assert.True(module.WasCacheHit));
    }

    [Fact]
    public async Task NonExportingLeafChangeInvalidatesOnlyThatModule()
    {
        using var workspace = new LuaWorkspace();
        var app = Document("app", "local dep = require('dep')\nreturn dep.value + 1");
        var first = await workspace.AnalyzeAsync([
            app,
            WidenedDependency(42),
        ]);
        var second = await workspace.AnalyzeAsync([
            app,
            WidenedDependency(43),
        ]);

        Assert.Equal(first.GetModule("dep")!.ExportHash, second.GetModule("dep")!.ExportHash);
        Assert.Equal(1, second.Metrics.CacheHitCount);
        Assert.Equal(1, second.Metrics.CacheMissCount);
        Assert.Equal(1, second.Metrics.InvalidatedModuleCount);
        Assert.True(second.GetModule("app")!.WasCacheHit);
        Assert.False(second.GetModule("dep")!.WasCacheHit);
    }

    [Fact]
    public async Task ExportChangeInvalidatesDependentAndPublishesNewDiagnostics()
    {
        using var workspace = new LuaWorkspace();
        var app = Document("app", "local dep = require('dep')\nreturn dep.value + 1");
        _ = await workspace.AnalyzeAsync([
            app,
            Document("dep", "return { value = 42 }"),
        ]);
        var changed = await workspace.AnalyzeAsync([
            app,
            Document("dep", "return { value = 'text' }"),
        ]);

        Assert.Equal(0, changed.Metrics.CacheHitCount);
        Assert.Equal(2, changed.Metrics.CacheMissCount);
        Assert.Equal(2, changed.Metrics.InvalidatedModuleCount);
        Assert.Contains(changed.GetModule("app")!.Compilation.Diagnostics, static diagnostic =>
            diagnostic.Code == "LUA6003");
    }

    [Fact]
    public async Task FileResolverUsesRootConfinedLuaPathPatterns()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pkg"));
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(root, "pkg", "value.lua"),
                "return { answer = 42 }");
            var resolver = new LuaFileSystemModuleResolver(new LuaFileSystemModuleResolverOptions
            {
                RootDirectories = [root],
            });
            using var workspace = new LuaWorkspace(resolver: resolver);
            var result = await workspace.AnalyzeAsync([
                Document("app", "local value = require('pkg.value')\nreturn value.answer"),
            ]);

            Assert.NotNull(result.GetModule("pkg.value"));
            Assert.Equal("@pkg/value.lua", result.GetModule("pkg.value")!.SourceIdentity);
            Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA7002");
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ModuleAndSourceBudgetsStopGraphExpansion()
    {
        var resolver = new LuaInMemoryModuleResolver([
            Document("dep", "return 42"),
        ]);
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            MaximumModuleCount = 1,
            MaximumSourceBytes = 1_024,
        }, resolver);
        var result = await workspace.AnalyzeAsync([
            Document("app", "return require('dep')"),
        ]);

        Assert.Single(result.Graph.Nodes);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "LUA7004" && diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public async Task ResolverFailuresBecomeStableWorkspaceDiagnostics()
    {
        using var workspace = new LuaWorkspace(resolver: new FailingResolver());
        var result = await workspace.AnalyzeAsync([
            Document("app", "return require('dep')"),
        ]);

        var diagnostic = Assert.Single(result.Diagnostics.Where(static diagnostic =>
            diagnostic.Code == "LUA7006"));
        Assert.Equal(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("InvalidOperationException", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParallelResultMergingIsDeterministicAndGloballyBounded()
    {
        var source = string.Join(
            '\n',
            Enumerable.Range(0, 150).Select(index =>
                $"local function value{index}() return {index} end")) +
            "\nreturn value149()";
        var documents = Enumerable.Range(0, 12)
            .Select(index => Document("m" + index, source))
            .ToArray();
        var options = new LuaWorkspaceOptions { MaximumParallelism = 3 };
        using var firstWorkspace = new LuaWorkspace(options);
        using var secondWorkspace = new LuaWorkspace(options);

        var first = await firstWorkspace.AnalyzeAsync(documents);
        var second = await secondWorkspace.AnalyzeAsync(documents.Reverse());

        // MaximumParallelism 是并发上界语义；实际观测并行度取决于线程池调度，
        // 慢速 runner 上单 worker 串行 drain 全部 item 是合法结果，因此只断言有界（1..3）。
        Assert.InRange(first.Metrics.PeakParallelism, 1, 3);
        Assert.InRange(second.Metrics.PeakParallelism, 1, 3);
        Assert.Equal(
            first.Modules.Select(static module =>
                (module.Identity.Name, module.ContentHash, module.ExportHash)),
            second.Modules.Select(static module =>
                (module.Identity.Name, module.ContentHash, module.ExportHash)));
        Assert.Equal(first.Diagnostics, second.Diagnostics);
    }

    [Fact]
    public async Task PrivateFunctionChangesDoNotInvalidateUnchangedImporters()
    {
        using var workspace = new LuaWorkspace();
        var app = Document("app", "local dep = require('dep')\nreturn dep.value + 1");
        _ = await workspace.AnalyzeAsync([
            app,
            Document("dep", "local function hidden() return 1 end\nreturn { value = 42 }"),
        ]);

        var changed = await workspace.AnalyzeAsync([
            app,
            Document("dep", "local function hidden() return 2 end\nreturn { value = 42 }"),
        ]);

        Assert.True(changed.GetModule("app")!.WasCacheHit);
        Assert.False(changed.GetModule("dep")!.WasCacheHit);
        Assert.Equal(1, changed.Metrics.InvalidatedModuleCount);
        Assert.Equal(0, changed.Metrics.DirtyExportCount);
        Assert.True(changed.Metrics.DirtyFunctionCount >= 1);
    }

    [Fact]
    public async Task CompactSnapshotsIndexMemberReferencesByName()
    {
        using var workspace = new LuaWorkspace();
        LuaWorkspaceDocument[] documents = [
            Document(
                "lib",
                "local M = {}\nfunction M.fetch() return 1 end\nM.count = 2\nreturn M"),
            Document(
                "app",
                "local lib = require('lib')\nlocal n = lib.count\nreturn lib.fetch() + n"),
        ];

        var compact = await workspace.AnalyzeCompactAsync(documents);

        var fetch = compact.FindMemberReferences("fetch");
        Assert.Equal(2, fetch.Length);
        Assert.Contains(fetch, static item => item.Module.Name == "lib");
        Assert.Contains(fetch, static item => item.Module.Name == "app");
        Assert.All(fetch, static item => Assert.Equal("fetch", item.Name));

        var count = compact.FindMemberReferences("count");
        Assert.Equal(2, count.Length);
        Assert.Empty(compact.FindMemberReferences("missing"));
    }

    [Fact]
    public async Task CompactSnapshotsKeepQueryableReferencesWithoutCompilerModels()
    {
        using var workspace = new LuaWorkspace();
        LuaWorkspaceDocument[] documents = [
            Document(
                "app",
                "local value = 1\n" +
                "local function read() return value end\n" +
                "return value, read()"),
        ];
        var full = await workspace.AnalyzeAsync(documents);
        var module = Assert.Single(full.Modules);
        var symbol = Assert.Single(module.Compilation.SemanticModel.Symbols, static item =>
            item.Name == "value");
        var key = module.Compilation.SemanticModel.GetSymbolKey(symbol, module.Identity);

        var compact = await workspace.AnalyzeCompactAsync(documents);

        Assert.Equal(2, compact.FindReferences(key).Length);
        Assert.True(compact.EstimatedResidentBytes > 0);
        Assert.Equal(compact.EstimatedResidentBytes, compact.Metrics.CompactResidentBytes);
        Assert.True(compact.Metrics.IndexedReferenceCount >= 3);
        var materialized = await compact.MaterializeAsync(workspace, documents);
        Assert.True(materialized.GetModule("app")!.WasCacheHit);
    }

    [Fact]
    public async Task WeakAnalysisCacheDetectsReclaimedFullModelsAndRematerializes()
    {
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            RetainFullAnalysisCacheResults = false,
        });
        LuaWorkspaceDocument[] firstDocuments = [Document("app", "local value = 1\nreturn value")];
        LuaWorkspaceDocument[] otherDocuments = [Document("other", "local value = 2\nreturn value")];
        PopulateWeakAnalysisCache(workspace, firstDocuments);
        // 第二次分析替换上次结果 pin：首个条目只剩弱引用，可被 GC 回收。
        PopulateWeakAnalysisCache(workspace, otherDocuments);

        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();

        var materialized = await workspace.AnalyzeAsync(firstDocuments);
        Assert.True(materialized.Succeeded);
        Assert.True(materialized.Metrics.ReclaimedAnalysisCount >= 1 ||
            materialized.GetModule("app")!.WasCacheHit);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void PopulateWeakAnalysisCache(
        LuaWorkspace workspace,
        IReadOnlyCollection<LuaWorkspaceDocument> documents)
    {
        _ = workspace.AnalyzeAsync(documents).GetAwaiter().GetResult();
    }

    [Fact]
    public async Task FairWorkerQueuesBoundPendingWorkAndReportProgress()
    {
        var source = string.Join(
            '\n',
            Enumerable.Range(0, 40).Select(index =>
                $"local function value{index}() return {index} end")) +
            "\nreturn value39()";
        var progress = new ProgressCollector();
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            MaximumParallelism = 3,
            MaximumPendingWorkItems = 7,
            Progress = progress,
        });
        var documents = Enumerable.Range(0, 64)
            .Select(index => Document("m" + index, source))
            .ToArray();

        var result = await workspace.AnalyzeAsync(documents);

        // MaximumParallelism 是并发上界语义；实际观测并行度取决于线程池调度，
        // 慢速 runner 上单 worker 串行 drain 全部 item 是合法结果，因此只断言有界（1..3）。
        Assert.InRange(result.Metrics.PeakParallelism, 1, 3);
        Assert.InRange(result.Metrics.PendingWorkItemHighWatermark, 1, 7);
        Assert.Contains(progress.Values, static item => item.Phase == LuaWorkspaceProgressPhase.Discovery);
        Assert.Contains(progress.Values, static item => item.Phase == LuaWorkspaceProgressPhase.Analysis);
        Assert.Equal(LuaWorkspaceProgressPhase.Completed, progress.Values[^1].Phase);
    }

    [Fact]
    public async Task CacheEvictionHonorsEntryAndByteBudgets()
    {
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            MaximumCacheEntryCount = 4,
            MaximumCacheBytes = 4_096,
            RetainFullAnalysisCacheResults = true,
        });
        var result = await workspace.AnalyzeAsync(Enumerable.Range(0, 12)
            .Select(index => Document("m" + index, "local value = " + index + "\nreturn value")));

        Assert.True(result.Metrics.CacheEvictionCount > 0);
        Assert.InRange(result.Metrics.CacheResidentBytes, 0, 4_096);
    }

    [Fact]
    public async Task VersionedDiskCacheAcceptsValidSummariesAndRejectsCorruption()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-workspace-cache-" + Guid.NewGuid().ToString("N"));
        var options = new LuaWorkspaceOptions { DiskCacheDirectory = root };
        var documents = new[] { Document("app", "return { value = 42 }") };
        try
        {
            using (var first = new LuaWorkspace(options))
            {
                var result = await first.AnalyzeAsync(documents);
                Assert.Equal(0, result.Metrics.DiskCacheHitCount);
            }

            using (var warm = new LuaWorkspace(options))
            {
                var result = await warm.AnalyzeAsync(documents);
                Assert.True(result.Metrics.DiskCacheHitCount >= 1);
            }

            var cacheFile = Assert.Single(Directory.GetFiles(root, "*.lunilcache", SearchOption.AllDirectories));
            await File.WriteAllTextAsync(cacheFile, "corrupt");
            using var corrupted = new LuaWorkspace(options);
            var recovered = await corrupted.AnalyzeAsync(documents);
            Assert.Equal(0, recovered.Metrics.DiskCacheHitCount);
            Assert.True(recovered.Succeeded);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HostSummaryInvalidationIsScopedToReferencedFunctionPaths()
    {
        var root = Path.Combine(Path.GetTempPath(), "lunil-host-cache-" + Guid.NewGuid().ToString("N"));
        var integer = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Integer };
        var text = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.String };
        static LuaHostFunctionContract Function(string path, LuaHostTypeDescriptor result) => new()
        {
            Path = path,
            Returns = [result],
        };
        var firstContract = new LuaHostContractBuilder("selective-host")
            .AddFunction(Function("host.used", integer))
            .AddFunction(Function("host.unused", integer))
            .Build();
        var changedContract = new LuaHostContractBuilder("selective-host")
            .AddFunction(Function("host.used", integer))
            .AddFunction(Function("host.unused", text))
            .Build();
        var documents = new[] { Document("app", "return host.used()") };
        try
        {
            using (var first = new LuaWorkspace(new LuaWorkspaceOptions
            {
                DiskCacheDirectory = root,
                HostContract = firstContract,
            }))
            {
                _ = await first.AnalyzeAsync(documents);
            }

            using var changed = new LuaWorkspace(new LuaWorkspaceOptions
            {
                DiskCacheDirectory = root,
                HostContract = changedContract,
            });
            var result = await changed.AnalyzeAsync(documents);
            Assert.True(result.Metrics.DiskCacheHitCount >= 1);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task CancellationAndDiagnosticSuppressionAreHonored()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            SuppressedDiagnosticCodes = ["LUA7002", "LUA7003"],
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => workspace.AnalyzeAsync([
            Document("app", "return 42"),
        ], cancelled.Token));
        var result = await workspace.AnalyzeAsync([
            Document("app", "local name = 'x'\nrequire(name)\nreturn require('missing')"),
        ]);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code is "LUA7002" or "LUA7003");
    }

    [Fact]
    public async Task DisposalLetsAnActiveSnapshotFinishAndRejectsNewOperations()
    {
        var resolver = new BlockingResolver(Document("dep", "return 42"));
        var workspace = new LuaWorkspace(resolver: resolver);
        var analysis = workspace.AnalyzeAsync([
            Document("app", "return require('dep')"),
        ]);
        await resolver.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));

        workspace.Dispose();
        resolver.Release.SetResult();

        var result = await analysis;
        Assert.True(result.Succeeded);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => workspace.AnalyzeAsync([
            Document("other", "return 1"),
        ]));
        Assert.Throws<ObjectDisposedException>(workspace.ClearCache);
    }

    [Fact]
    public async Task RequireAnnotationMatchingModuleExportProducesNoLua6022()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("app", "---@type { value: integer }\nlocal dep = require('dep')\nreturn dep.value"),
            Document("dep", "return { value = 42 }"),
        ]);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "LUA6022");
    }

    [Fact]
    public async Task RequireAnnotationMismatchingModuleExportProducesLua6022()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("app", "---@type { value: string }\nlocal dep = require('dep')\nreturn dep.value"),
            Document("dep", "return { value = 42 }"),
        ]);

        var diagnostic = Assert.Single(
            result.Diagnostics,
            static item => item.Code == "LUA6022");
        Assert.Equal(LuaWorkspaceDiagnosticPhase.Analysis, diagnostic.Phase);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("app", diagnostic.Module?.Name);
        Assert.Contains("'dep'", diagnostic.Message);
        Assert.Contains("annotated as '{value: string}'", diagnostic.Message);
        Assert.Contains("module exports '{value:", diagnostic.Message);
    }

    [Fact]
    public async Task SuppressedLua6022IsOmittedFromWorkspaceDiagnostics()
    {
        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            SuppressedDiagnosticCodes = ["LUA6022"],
        });
        var result = await workspace.AnalyzeAsync([
            Document("app", "---@type { value: string }\nlocal dep = require('dep')\nreturn dep.value"),
            Document("dep", "return { value = 42 }"),
        ]);

        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "LUA6022");
    }

    [Fact]
    public async Task UnresolvedOrAnyTypesProduceNoCrossModuleLua6022()
    {
        using var workspace = new LuaWorkspace();
        var result = await workspace.AnalyzeAsync([
            Document("app", "---@type Missing\nlocal dep = require('dep')\nreturn dep.value"),
            Document("dep", "return { value = 42 }"),
        ]);
        Assert.DoesNotContain(result.Diagnostics, static diagnostic =>
            diagnostic.Code == "LUA6022");

        var resultAny = await workspace.AnalyzeAsync([
            Document("app", "---@type { value: integer }\nlocal dep = require('dep')\nreturn dep"),
            Document("dep", "return unknown_factory()"),
        ]);
        Assert.DoesNotContain(resultAny.Diagnostics, static diagnostic =>
            diagnostic.Code == "LUA6022");
    }

    private static LuaWorkspaceDocument Document(string name, string source) =>
        LuaWorkspaceDocument.FromUtf8(name, source);

    private static LuaWorkspaceDocument WidenedDependency(int value) =>
        Document(
            "dep",
            $"local exports = {{ value = {value} }}\n" +
            "---@cast exports { value: integer }\n" +
            "return exports");

    private sealed class FailingResolver : ILuaModuleResolver
    {
        public ValueTask<LuaWorkspaceDocument?> ResolveAsync(
            LuaModuleResolutionRequest request,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("resolver failure");
    }

    private sealed class BlockingResolver(LuaWorkspaceDocument document) : ILuaModuleResolver
    {
        public TaskCompletionSource Entered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Release { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<LuaWorkspaceDocument?> ResolveAsync(
            LuaModuleResolutionRequest request,
            CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            return document;
        }
    }

    private sealed class ProgressCollector : IProgress<LuaWorkspaceProgress>
    {
        public List<LuaWorkspaceProgress> Values { get; } = [];

        public void Report(LuaWorkspaceProgress value) => Values.Add(value);
    }
}
