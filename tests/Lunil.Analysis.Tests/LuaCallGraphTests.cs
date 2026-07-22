using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis.Tests;

public sealed class LuaCallGraphTests
{
    [Fact]
    public void ResolvesLocalAndUpvalueFunctionTargets()
    {
        var result = Analyze("""
            local function target() return 1 end
            local function caller()
                return target()
            end
            return caller()
            """);

        Assert.Collection(
            result.CallGraph.FunctionIds,
            id => Assert.Equal(0, id),
            id => Assert.Equal(1, id),
            id => Assert.Equal(2, id));
        var target = Assert.Single(result.SemanticModel.Symbols, symbol => symbol.Name == "target");
        var caller = Assert.Single(result.SemanticModel.Symbols, symbol => symbol.Name == "caller");
        var calls = result.CallGraph.Edges.OrderBy(static site => site.Span.Start).ToArray();

        Assert.Equal(2, calls.Length);
        Assert.Equal(2, calls[0].ContainingFunctionId);
        Assert.Equal(1, calls[0].TargetFunctionId);
        Assert.Same(target, calls[0].DirectSymbol);
        Assert.Equal(LuaCallKind.Direct, calls[0].Kind);
        Assert.Equal(LuaCallResolutionStatus.Resolved, calls[0].ResolutionStatus);
        Assert.Equal(0, calls[1].ContainingFunctionId);
        Assert.Equal(2, calls[1].TargetFunctionId);
        Assert.Same(caller, calls[1].DirectSymbol);
    }

    [Fact]
    public void ResolvesUniqueGlobalFunctionTargetsWithoutUsingTheEnvironmentSymbol()
    {
        var result = Analyze("""
            function global_target() return 1 end
            return global_target()
            """);

        var call = Assert.Single(result.CallGraph.Edges);
        Assert.Equal("global_target", call.DirectName);
        Assert.Null(call.DirectSymbol);
        Assert.Equal(1, call.TargetFunctionId);
        Assert.Equal(LuaCallResolutionStatus.Resolved, call.ResolutionStatus);
    }

    [Fact]
    public void RetainsMemberMethodCallableDynamicAndUnresolvedCalls()
    {
        var result = Analyze("""
            ---@class Service
            ---@field run fun(self: Service): integer
            ---@type Service
            local service = make()
            service.run(service)
            service:run()

            ---@class Factory
            ---@overload fun(): integer
            ---@type Factory
            local factory = make()
            factory()

            local dynamic = make()
            dynamic()
            local number = 1
            number()
            """);

        var member = Assert.Single(result.CallGraph.Edges, site => site.Kind == LuaCallKind.Member);
        Assert.Equal("run", Assert.IsType<LuaMemberTarget>(member.MemberTarget).Name);
        Assert.Equal(LuaCallResolutionStatus.Resolved, member.ResolutionStatus);

        var method = Assert.Single(result.CallGraph.Edges, site => site.Kind == LuaCallKind.Method);
        Assert.Equal("run", Assert.IsType<LuaMemberTarget>(method.MemberTarget).Name);
        Assert.Null(method.DirectSymbol);
        Assert.Null(method.DirectName);
        Assert.Equal(LuaCallResolutionStatus.Resolved, method.ResolutionStatus);

        var callable = Assert.Single(result.CallGraph.Edges, site =>
            site.Kind == LuaCallKind.Callable && site.DirectSymbol?.Name == "factory");
        Assert.Equal(LuaCallResolutionStatus.Resolved, callable.ResolutionStatus);

        var dynamic = Assert.Single(result.CallGraph.Edges, site =>
            site.DirectSymbol?.Name == "dynamic");
        Assert.Equal(LuaCallResolutionStatus.Dynamic, dynamic.ResolutionStatus);
        Assert.Equal(LuaCallUnresolvedReasons.CalleeSignatureIsDynamic, dynamic.UnresolvedReason);

        var unresolved = Assert.Single(result.CallGraph.Edges, site =>
            site.DirectSymbol?.Name == "number");
        Assert.Equal(LuaCallResolutionStatus.Unresolved, unresolved.ResolutionStatus);
        Assert.Equal(LuaCallUnresolvedReasons.CalleeIsNotCallable, unresolved.UnresolvedReason);
    }

    [Fact]
    public void OnlyGlobalRequireProducesModuleRequests()
    {
        var result = Analyze("""
            do
                local require = function() end
                require('shadowed')
            end
            require('game.inventory')
            require(moduleName)
            """);

        var calls = result.CallGraph.Edges.OrderBy(static site => site.Span.Start).ToArray();
        Assert.Equal(3, calls.Length);
        Assert.Null(calls[0].ModuleRequest);
        Assert.Equal("game.inventory", calls[1].ModuleRequest);
        Assert.Equal(LuaCallResolutionStatus.Resolved, calls[1].ResolutionStatus);
        Assert.Null(calls[2].ModuleRequest);
        Assert.Equal(LuaCallResolutionStatus.Dynamic, calls[2].ResolutionStatus);
        Assert.Equal(LuaCallUnresolvedReasons.ModuleRequestIsDynamic, calls[2].UnresolvedReason);
    }

    [Fact]
    public void UnreachableCallsRemainInTheGraph()
    {
        var result = Analyze("""
            local function choose(flag)
                if flag then
                    return 1
                else
                    return 2
                end
                print('never')
            end
            return choose(true)
            """);

        var unreachable = Assert.Single(result.CallGraph.Edges, site =>
            site.ContainingFunctionId == 1);
        Assert.Equal(1, unreachable.ContainingFunctionId);
        Assert.Equal(LuaCallResolutionStatus.Resolved, unreachable.ResolutionStatus);
    }

    private static LuaAnalysisResult Analyze(string source)
    {
        var text = SourceText.FromUtf8(source);
        var lexing = LuaLexer.Lex(text);
        return LuaTypeAnalyzer.Analyze(
            LuaBinder.Bind(LuaParser.Parse(lexing)),
            LuaAnnotationParser.Parse(lexing));
    }
}
