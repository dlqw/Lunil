using Lunil.Core.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Parsing;

namespace Lunil.Semantics.Tests.Binding;

public sealed class LuaBinderTests
{
    [Fact]
    public void ResolvesShadowingInitializersWritesAndGlobals()
    {
        const string source = """
            local x = 1
            do
                local x = x
                x = 2
            end
            return x, y
            """;

        var model = Bind(source);
        var xSymbols = model.Symbols.Where(symbol => symbol.Name == "x").ToArray();
        var xReferences = model.References.Where(reference => reference.Name == "x").ToArray();

        Assert.Empty(model.Diagnostics);
        Assert.Equal(2, xSymbols.Length);
        Assert.Equal(3, xReferences.Length);
        Assert.Same(xSymbols[0], xReferences[0].Symbol);
        Assert.Same(xSymbols[1], xReferences[1].Symbol);
        Assert.True(xReferences[1].IsWrite);
        Assert.Same(xSymbols[0], xReferences[2].Symbol);

        var global = Assert.Single(model.References, reference => reference.Name == "y");
        Assert.Equal(LuaNameResolutionKind.Global, global.ResolutionKind);
        Assert.Equal(LuaSymbolKind.Environment, global.Symbol.Kind);
    }

    [Fact]
    public void PropagatesCapturesThroughNestedFunctions()
    {
        const string source = """
            local x = 1
            local function outer(a, ...)
                local y = x
                return function(b, ...)
                    return x + y + a + b + ... + global
                end
            end
            """;

        var model = Bind(source);
        var main = model.Functions.Single(function => function.Id == 0);
        var outer = model.Functions.Single(function => function.Id == 1);
        var inner = model.Functions.Single(function => function.Id == 2);

        Assert.Empty(model.Diagnostics);
        Assert.Empty(main.Captures);
        Assert.Equal(["_ENV", "x"], outer.Captures.Select(static symbol => symbol.Name).Order());
        Assert.Equal(
            ["_ENV", "a", "x", "y"],
            inner.Captures.Select(static symbol => symbol.Name).Order());
        Assert.True(model.Symbols.Single(symbol => symbol.Name == "x").IsCaptured);
        Assert.True(model.Symbols.Single(symbol => symbol.Name == "y").IsCaptured);
        Assert.True(model.Symbols.Single(symbol => symbol.Name == "a").IsCaptured);

        var global = Assert.Single(model.References, reference => reference.Name == "global");
        Assert.Equal(LuaNameResolutionKind.Global, global.ResolutionKind);
        Assert.True(global.Symbol.IsCaptured);
    }

    [Fact]
    public void LocalEnvironmentShadowsImplicitEnvironmentForGlobals()
    {
        var model = Bind("local _ENV = sandbox; return value");
        var environments = model.Symbols.Where(symbol => symbol.Name == "_ENV").ToArray();
        var sandbox = Assert.Single(model.References, reference => reference.Name == "sandbox");
        var value = Assert.Single(model.References, reference => reference.Name == "value");

        Assert.Empty(model.Diagnostics);
        Assert.Equal(2, environments.Length);
        Assert.Same(environments[0], sandbox.Symbol);
        Assert.Same(environments[1], value.Symbol);
        Assert.Equal(LuaNameResolutionKind.Global, sandbox.ResolutionKind);
        Assert.Equal(LuaNameResolutionKind.Global, value.ResolutionKind);
    }

    [Fact]
    public void ResolvesImplicitEnvironmentAsAnUpvalueEvenInTheMainChunk()
    {
        var model = Bind("local original = _ENV; _ENV = nil; return original, _ENV");
        var environment = model.Symbols.Single(static symbol =>
            symbol.Kind == LuaSymbolKind.Environment);
        var references = model.References.Where(static reference =>
            reference.Name == "_ENV").ToArray();

        Assert.Empty(model.Diagnostics);
        Assert.Equal(3, references.Length);
        Assert.All(references, reference => Assert.Same(environment, reference.Symbol));
        Assert.All(references, reference =>
            Assert.Equal(LuaNameResolutionKind.Upvalue, reference.ResolutionKind));
        Assert.True(references[1].IsWrite);
    }

    [Fact]
    public void EnforcesConstCloseAndAttributeRules()
    {
        const string source = """
            local a <const> = 1
            local b <close>
            local c <unknown>
            local d <close>, e <close>
            a = 2
            b = 3
            """;

        var model = Bind(source);

        Assert.Equal(2, model.Diagnostics.Count(diagnostic => diagnostic.Code == "LUA3002"));
        Assert.Single(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3003");
        Assert.Single(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3004");
        Assert.Equal(
            LuaLocalAttributeKind.Constant,
            model.Symbols.Single(symbol => symbol.Name == "a").Attribute);
        Assert.Equal(
            LuaLocalAttributeKind.ToBeClosed,
            model.Symbols.Single(symbol => symbol.Name == "b").Attribute);
    }

    [Fact]
    public void RejectsBreakAndVarargOutsideTheirFunctionContexts()
    {
        var model = Bind("break; local f = function() return ... end; return ...");

        Assert.Single(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3005");
        Assert.Single(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3001");
    }

    [Theory]
    [InlineData("goto L; local x; ::L::", null)]
    [InlineData("goto L; local x; ::L:: print()", "LUA3008")]
    [InlineData("do goto L end; ::L::", null)]
    [InlineData("goto L; do ::L:: end", "LUA3007")]
    [InlineData("::L:: ::L::", "LUA3006")]
    [InlineData("::L:: do ::L:: end", "LUA3006")]
    [InlineData("do ::L:: end ::L::", null)]
    [InlineData("repeat goto L; local x; ::L:: until x", "LUA3008")]
    public void ImplementsLuaLabelAndGotoScopeRules(string source, string? expectedCode)
    {
        var model = Bind(source);
        var bindingDiagnostics = model.Diagnostics.Where(diagnostic =>
            diagnostic.Code.StartsWith("LUA3", StringComparison.Ordinal)).ToArray();

        if (expectedCode is null)
        {
            Assert.Empty(bindingDiagnostics);
        }
        else
        {
            Assert.Contains(bindingDiagnostics, diagnostic => diagnostic.Code == expectedCode);
        }
    }

    [Fact]
    public void ColonFunctionDeclaresImplicitSelfParameter()
    {
        var model = Bind("function object:method(a) return self, a end");
        var function = model.Functions.Single(info => info.Id == 1);

        Assert.Empty(model.Diagnostics);
        Assert.Equal(
            ["self", "a"],
            function.Symbols
                .Where(static symbol => symbol.Kind == LuaSymbolKind.Parameter)
                .Select(static symbol => symbol.Name));
        Assert.All(
            model.References.Where(reference => reference.Name is "self" or "a"),
            reference => Assert.Equal(LuaNameResolutionKind.Local, reference.ResolutionKind));
    }

    [Fact]
    public void RepeatConditionCanSeeBodyLocals()
    {
        var model = Bind("repeat local x = 1 until x");
        var symbol = model.Symbols.Single(candidate => candidate.Name == "x");
        var reference = model.References.Single(candidate => candidate.Name == "x");

        Assert.Empty(model.Diagnostics);
        Assert.Same(symbol, reference.Symbol);
        Assert.Equal(LuaNameResolutionKind.Local, reference.ResolutionKind);
    }

    [Fact]
    public void FunctionDeclarationAssignmentHonorsConstButMemberMutationDoesNot()
    {
        var direct = Bind("local f <const> = nil; function f() end");
        var member = Bind("local t <const> = {}; function t.f() end");

        Assert.Single(direct.Diagnostics, diagnostic => diagnostic.Code == "LUA3002");
        Assert.DoesNotContain(member.Diagnostics, diagnostic => diagnostic.Code == "LUA3002");
    }

    [Fact]
    public void DuplicateParameterAndLocalNamesAreLegalShadowing()
    {
        var model = Bind("local a, a; return function(a, a) return a end");

        Assert.Empty(model.Diagnostics);
        Assert.Equal(4, model.Symbols.Count(symbol => symbol.Name == "a"));
    }

    [Fact]
    public void EnforcesConfiguredActiveLocalAndUpvalueLimits()
    {
        var localOptions = LuaBinderOptions.Default with { MaximumActiveLocalsPerFunction = 2 };
        var upvalueOptions = LuaBinderOptions.Default with { MaximumUpvaluesPerFunction = 1 };

        var locals = Bind("local a, b, c", localOptions);
        var upvalues = Bind("local a, b; return function() return a + b end", upvalueOptions);

        Assert.Single(locals.Diagnostics, diagnostic => diagnostic.Code == "LUA3009");
        Assert.Single(upvalues.Diagnostics, diagnostic => diagnostic.Code == "LUA3010");
    }

    [Fact]
    public void ActiveLocalLimitCounterResetsAcrossScopesAndFunctions()
    {
        var options = LuaBinderOptions.Default with { MaximumActiveLocalsPerFunction = 2 };

        var model = Bind(
            "do local a,b end; do local c,d end; " +
            "local function first(x,y) return x+y end " +
            "local function second(x,y) return x+y end",
            options);

        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3009");
    }

    private static LuaSemanticModel Bind(string source, LuaBinderOptions? options = null) =>
        LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source)), options);
}
