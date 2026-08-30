using Lunil.Core;
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

    [Theory]
    [InlineData(LuaLanguageVersion.Lua53, false)]
    [InlineData(LuaLanguageVersion.Lua54, true)]
    [InlineData(LuaLanguageVersion.Lua55, true)]
    public void AppliesVersionSpecificNestedLabelShadowingRules(
        LuaLanguageVersion version,
        bool rejectsNestedLabel)
    {
        var model = Bind(
            "::L:: do ::L:: end",
            new LuaBinderOptions { LanguageVersion = version });

        Assert.Equal(
            rejectsNestedLabel,
            model.Diagnostics.Any(diagnostic => diagnostic.Code == "LUA3006"));
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

    [Fact]
    public void BindsLua55NamedVarargsAsReadOnlyTables()
    {
        var model = Bind(
            "local function f(... values) return values.n end",
            LuaBinderOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 });

        var values = Assert.Single(model.Symbols, symbol => symbol.Name == "values");
        Assert.Equal(LuaSymbolKind.Local, values.Kind);
        Assert.Equal(LuaLocalAttributeKind.VarArg, values.Attribute);
        Assert.True(values.IsReadOnly);
        Assert.Empty(model.Diagnostics);
    }

    [Fact]
    public void BindsLua55GlobalDeclarationAndRejectsConstWrites()
    {
        var model = Bind(
            "global<const> answer; answer = 42",
            LuaBinderOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 });

        Assert.Single(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3002");
        Assert.Equal(
            LuaLocalAttributeKind.Constant,
            Assert.Single(model.Symbols, symbol => symbol.Name == "answer").Attribute);
    }

    [Fact]
    public void ProjectsMemberAndMethodNamesWithoutInventingLexicalSymbols()
    {
        const string source = "local foo, self; foo.bar(); self:helper()";

        var model = Bind(source);

        Assert.DoesNotContain(model.References, reference => reference.Name is "bar" or "helper");
        var bar = Assert.Single(model.MemberReferences, reference => reference.Name == "bar");
        var helper = Assert.Single(model.MemberReferences, reference => reference.Name == "helper");
        Assert.Equal(LuaReferenceKind.Member, bar.Kind);
        Assert.Equal(LuaReferenceAccess.Read | LuaReferenceAccess.Call, bar.Access);
        Assert.Equal(
            LuaReferenceAccess.Read | LuaReferenceAccess.Call | LuaReferenceAccess.MethodCall,
            helper.Access);
        Assert.Equal("foo", source[bar.ReceiverSpan.Start..bar.ReceiverSpan.End]);
        Assert.Equal("self", source[helper.ReceiverSpan.Start..helper.ReceiverSpan.End]);
        Assert.Equal(["foo", "bar", "self", "helper"],
            model.UnifiedReferences.Select(static reference => reference.Name));
    }

    [Fact]
    public void RecordsWritesLiteralAndDynamicIndicesAndContainingFunctions()
    {
        const string source = "local t, key; t.value = 1; t[\"saved\"](); local function f() t[key] = 2 end";

        var model = Bind(source);

        var value = Assert.Single(model.MemberReferences, reference => reference.Name == "value");
        var saved = Assert.Single(model.MemberReferences, reference => reference.Name == "saved");
        var dynamic = Assert.Single(model.MemberReferences, reference =>
            reference.ResolutionKind == LuaReferenceResolutionKind.DynamicIndex);
        Assert.Equal(LuaReferenceAccess.Write, value.Access);
        Assert.Equal(LuaReferenceAccess.Read | LuaReferenceAccess.Call, saved.Access);
        Assert.Equal(LuaReferenceAccess.Write, dynamic.Access);
        Assert.Equal(LuaReferenceKind.Index, saved.Kind);
        Assert.Equal(0, saved.ContainingFunctionId);
        Assert.Equal(1, dynamic.ContainingFunctionId);
        Assert.Equal(1, model.GetContainingFunction(dynamic.Span).Id);
    }

    [Fact]
    public void ReferenceIndexesHandleShadowingExactSpansAndForeignSymbols()
    {
        const string source = "local x = 1; do local x = x; x = 2 end; return x";
        var model = Bind(source);
        var symbols = model.Symbols.Where(static symbol => symbol.Name == "x").ToArray();

        Assert.Equal(2, model.FindCodeReferences(symbols[0]).Length);
        Assert.Single(model.FindCodeReferences(symbols[1]));
        var last = model.UnifiedReferences.Last();
        Assert.Same(last, model.FindCodeReferenceAt(last.Span.Start));
        Assert.Contains(last, model.FindCodeReferences(last.Span));

        var foreign = Bind("local x; return x").Symbols.Single(static symbol => symbol.Name == "x");
        Assert.Throws<ArgumentException>(() => model.FindCodeReferences(foreign));
    }

    [Fact]
    public void MalformedMemberAndIndexReferencesRemainQueryable()
    {
        var model = Bind("local t; t.; t[");

        Assert.Contains(model.MemberReferences, reference =>
            reference.ResolutionKind == LuaReferenceResolutionKind.Incomplete);
        Assert.All(model.MemberReferences, reference =>
            Assert.InRange(reference.ContainingFunctionId, 0, model.Functions.Length - 1));
    }

    [Fact]
    public void Lua55PrefixAttributeBindsTheFollowingNameAsConstant()
    {
        const string source = """
            local <const> x = 1
            x = 2
            """;

        var model = Bind(
            source,
            LuaBinderOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 });

        var symbol = Assert.Single(model.Symbols, candidate => candidate.Name == "x");
        Assert.Equal(LuaSymbolKind.Local, symbol.Kind);
        Assert.Equal(LuaLocalAttributeKind.Constant, symbol.Attribute);
        Assert.Empty(model.Symbols.Where(candidate => candidate.Name == "const"));
        Assert.Single(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3002");
    }

    [Fact]
    public void Lua55PrefixAttributeAppliesToEveryNameInTheList()
    {
        const string source = """
            local <const> x, y = 1, 2
            x = 3
            y = 4
            """;

        var model = Bind(
            source,
            LuaBinderOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 });

        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3003");
        Assert.All(model.Symbols.Where(candidate => candidate.Name is "x" or "y"), candidate =>
        {
            Assert.Equal(LuaSymbolKind.Local, candidate.Kind);
            Assert.Equal(LuaLocalAttributeKind.Constant, candidate.Attribute);
        });
        Assert.Equal(2, model.Diagnostics.Count(diagnostic => diagnostic.Code == "LUA3002"));
    }

    [Fact]
    public void Lua55PrefixAttributeDoesNotLeakPastTheDeclarationStatement()
    {
        const string source = """
            local <const> x, y = 1, 2
            local plain = 3
            plain = 4
            """;

        var model = Bind(
            source,
            LuaBinderOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 });

        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3002");
        Assert.All(model.Symbols.Where(candidate => candidate.Name is "x" or "y"), candidate =>
            Assert.Equal(LuaLocalAttributeKind.Constant, candidate.Attribute));
    }

    [Fact]
    public void Lua55GlobalPostfixAttributeAppliesOnlyToItsName()
    {
        const string source = """
            global x <const>, y = 1, 2
            y = 3
            x = 4
            """;

        var model = Bind(
            source,
            LuaBinderOptions.Default with { LanguageVersion = LuaLanguageVersion.Lua55 });

        Assert.Single(model.Diagnostics, diagnostic => diagnostic.Code == "LUA3002");
    }

    private static LuaSemanticModel Bind(string source, LuaBinderOptions? options = null)
    {
        options = (options ?? LuaBinderOptions.Default) with
        {
            CollectCodeReferences = true,
        };
        var syntax = LuaParser.Parse(
            SourceText.FromUtf8(source),
            lexerOptions: null,
            parserOptions: new LuaParserOptions
            {
                LanguageVersion = options?.LanguageVersion ?? LuaLanguageVersions.Default,
            });
        return LuaBinder.Bind(syntax, options);
    }
}
