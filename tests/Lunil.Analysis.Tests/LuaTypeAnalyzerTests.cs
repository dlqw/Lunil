using System.Collections.Immutable;
using Lunil.Core;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis.Tests;

public sealed class LuaTypeAnalyzerTests
{
    [Fact]
    public void ResolvesCrossFileExternalClassDeclarations()
    {
        // Build an external declaration from a separate document (the workspace index) and verify
        // the analyzed document resolves the type without an "unknown annotation type" error.
        var externalSource = SourceText.FromUtf8(
            """
            ---@class ExternalTask
            ---@field name string
            local ExternalTask = {}
            """);
        var externalLexing = LuaLexer.Lex(externalSource, LuaLexerOptions.Default);
        var externalAnnotations = LuaAnnotationParser.Parse(externalLexing);
        var external = LuaExternalTypeDeclarations.Collect(externalAnnotations);
        Assert.Contains("ExternalTask", external.Keys);

        var environment = new LuaAnalysisEnvironment
        {
            ExternalTypeDeclarations = external,
        };

        var text = SourceText.FromUtf8(
            """
            ---@type ExternalTask
            local task = nil
            return task
            """);
        var lexing = LuaLexer.Lex(text, LuaLexerOptions.Default);
        var syntax = LuaParser.Parse(lexing, LuaParserOptions.Default);
        var annotations = LuaAnnotationParser.Parse(lexing);
        var semantics = LuaBinder.Bind(syntax, LuaBinderOptions.Default);
        var result = LuaTypeAnalyzer.Analyze(semantics, annotations, environment);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6001");
    }

    [Fact]
    public void ResolvesClassAliasEnumAndStructuralMembers()
    {
        var result = Analyze(
            """
            ---@class Box<T>
            ---@field value T
            ---@operator add(Box<T>): Box<T>
            local Box = {}
            ---@alias Name string|'anonymous'
            ---@enum Status: string
            ---| 'ready'
            ---| 'done'
            local status = 'ready'
            return Box, status
            """);

        Assert.Empty(result.Diagnostics.Where(static item => item.Severity == DiagnosticSeverity.Error));
        var @class = Assert.IsType<LuaClassDeclaration>(result.TypeDeclarations[0]);
        Assert.Equal("Box", @class.Name);
        Assert.Equal("T", Assert.Single(@class.TypeParameters).Name);
        Assert.Equal("value", Assert.Single(@class.Fields).Name);
        Assert.Contains("add", @class.Operators.Keys);
        Assert.IsType<LuaAliasDeclaration>(result.TypeDeclarations[1]);
        var @enum = Assert.IsType<LuaEnumDeclaration>(result.TypeDeclarations[2]);
        Assert.Equal(2, @enum.Members.Length);
    }

    [Fact]
    public void ReportsDeclaredInitializerAndAssignmentMismatches()
    {
        var result = Analyze(
            """
            ---@type string
            local value = 42
            value = true
            return value
            """);

        Assert.Equal(2, result.Diagnostics.Count(static item => item.Code == "LUA6003"));
        var symbol = Assert.Single(result.Symbols.Where(static item => item.Symbol.Name == "value"));
        Assert.Equal("string", symbol.DeclaredType.DisplayName);
    }

    [Fact]
    public void MutableInferredTableFieldsAreNotConstrainedToTheirInitialLiterals()
    {
        var result = Analyze(
            """
            local table = { value = 40, [1] = 1 }
            table.value = table.value + table[1] + 1
            return table.value
            """);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6003");
        var table = Assert.Single(result.Symbols.Where(static item => item.Symbol.Name == "table"));
        var inferred = Assert.IsType<LuaStructuralTableType>(table.InferredType);
        Assert.Equal(
            LuaTypes.Integer,
            Assert.Single(inferred.Fields.Where(static item => item.Name == "value")).ValueType);
    }

    [Fact]
    public void ResolvesMetatableIndexRawAccessAndAliasMutation()
    {
        var result = Analyze(
            """
            local prototype = { answer = 42 }
            local metatable = { __index = prototype }
            local object = setmetatable({}, metatable)
            local alias = object
            local inherited = object.answer
            alias.extra = 'ok'
            local propagated = object.extra
            local rawInherited = rawget(object, 'answer')
            rawset(object, 'stored', true)
            local rawStored = rawget(alias, 'stored')
            local attached = getmetatable(object)
            return inherited, propagated, rawInherited, rawStored, attached
            """);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6007");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "inherited" && item.InferredType.DisplayName == "42");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "propagated" && item.InferredType.DisplayName == "'ok'");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "rawInherited" && item.InferredType == LuaTypes.Nil);
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "rawStored" && item.InferredType.DisplayName == "true");
        Assert.IsType<LuaMetatableType>(result.Symbols.Single(static item =>
            item.Symbol.Name == "object").InferredType);
        var fact = Assert.Single(result.MetatableFacts);
        Assert.True(fact.IsPrecise);
        Assert.IsType<LuaStructuralTableType>(fact.RawType);
    }

    [Fact]
    public void AppliesCallOperatorLengthNewIndexAndPairsMetamethods()
    {
        var result = Analyze(
            """
            local metatable = {
                __call = function(self, value) return 'called' end,
                __len = function(self) return 7 end,
                __add = function(left, right) return 'sum' end,
                __newindex = function(self, key, value) end,
                __pairs = function(self)
                    return function() return 'key', 3 end
                end
            }
            local object = setmetatable({}, metatable)
            local called = object(1)
            local length = #object
            local sum = object + object
            object.missing = 1
            local rawMissing = rawget(object, 'missing')
            for key, value in pairs(object) do
                local pairKey = key
                local pairValue = value
            end
            return called, length, sum, rawMissing
            """);

        Assert.DoesNotContain(result.Diagnostics, static item =>
            item.Code is "LUA6004" or "LUA6007");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "called" && item.InferredType.DisplayName == "'called'");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "length" && item.InferredType.DisplayName == "7");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "sum" && item.InferredType.DisplayName == "'sum'");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "rawMissing" && item.InferredType == LuaTypes.Nil);
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "pairKey" && item.InferredType.DisplayName == "'key'");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "pairValue" && item.InferredType.DisplayName == "3");
    }

    [Fact]
    public void FunctionIndexAndDynamicEscapeAreBoundedAndConservative()
    {
        var result = Analyze(
            """
            local functionIndex = {
                __index = function(self, key) return 9 end
            }
            local object = setmetatable({}, functionIndex)
            local beforeEscape = object.unknown
            external_sink(object)
            local afterEscape = object.dynamic
            return beforeEscape, afterEscape
            """);

        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "beforeEscape" && item.InferredType.DisplayName == "9");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "afterEscape" && item.InferredType == LuaTypes.Any);
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51, "integer")]
    [InlineData(LuaLanguageVersion.Lua52, "7")]
    [InlineData(LuaLanguageVersion.Lua53, "7")]
    [InlineData(LuaLanguageVersion.Lua54, "7")]
    [InlineData(LuaLanguageVersion.Lua55, "7")]
    public void AppliesVersionSpecificTableLengthMetamethod(
        LuaLanguageVersion version,
        string expected)
    {
        var result = Analyze(
            "local o = setmetatable({}, { __len = function() return 7 end }); local n = #o",
            version: version);

        Assert.Contains(result.Symbols, item =>
            item.Symbol.Name == "n" && item.InferredType.DisplayName == expected);
    }

    [Fact]
    public void RecognizesPrototypeConstructorsMethodsAndImplicitSelf()
    {
        var result = Analyze(
            """
            local Class = {}
            Class.__index = Class
            function Class:new(value)
                return setmetatable({ value = value }, self)
            end
            function Class:get()
                return self.value
            end
            local object = Class:new(5)
            local value = object:get()
            return value
            """);

        Assert.DoesNotContain(result.Diagnostics, static item =>
            item.Code is "LUA6004" or "LUA6007" or "LUA6017" or "LUA6018");
        var model = Assert.Single(result.ObjectModels);
        Assert.Equal("Class", model.Name);
        Assert.Equal(["new", "get"], model.Methods.Select(static method => method.Name));
        Assert.All(model.Methods, static method => Assert.True(method.HasImplicitSelf));
        Assert.IsType<LuaMetatableType>(result.Symbols.Single(static item =>
            item.Symbol.Name == "object").InferredType);
    }

    [Fact]
    public void RecognizesInheritanceMixinAndSingletonShapes()
    {
        var result = Analyze(
            """
            local Base = {}
            Base.__index = Base
            function Base:baseMethod() return 'base' end

            local mixin = { mixed = function(self) return 'mixed' end }
            local Derived = setmetatable({}, { __index = Base })
            Derived.__index = Derived
            Derived.mixed = mixin.mixed
            function Derived:derivedMethod() return 'derived' end

            local singleton = setmetatable({}, Derived)
            local inherited = singleton:baseMethod()
            local mixed = singleton:mixed()
            local own = singleton:derivedMethod()
            return inherited, mixed, own
            """);

        Assert.DoesNotContain(result.Diagnostics, static item =>
            item.Code is "LUA6004" or "LUA6007");
        var derived = Assert.Single(result.ObjectModels, static item => item.Name == "Derived");
        Assert.NotEmpty(derived.BaseTypes);
        Assert.Contains(derived.Methods, static method => method.Name == "derivedMethod");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "inherited" && item.InferredType.DisplayName == "'base'");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "mixed" && item.InferredType.DisplayName == "'mixed'");
    }

    [Fact]
    public void ReportsDotColonMismatchAndAnnotationConflicts()
    {
        var result = Analyze(
            """
            ---@class Broken
            ---@field value string
            local Broken = { value = 1 }
            Broken.__index = Broken
            function Broken:method() return self.value end
            function Broken.factory() return {} end
            local object = setmetatable({}, Broken)
            object.method()
            object:factory()
            """);

        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6019");
        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6018");
        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6017");
    }

    [Fact]
    public void NarrowsNilTypeAssertAndShortCircuitConditions()
    {
        var result = Analyze(
            """
            ---@type string|nil
            local value = nil
            if value ~= nil and type(value) == 'string' then
                print(#value)
            end
            assert(value)
            print(#value)
            """);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6003");
        Assert.Contains(result.Expressions, static item => item.Type.DisplayName == "string");
    }

    [Fact]
    public void NarrowsDiscriminatedStructuralUnion()
    {
        var result = Analyze(
            """
            ---@type {kind: 'text', value: string}|{kind: 'count', value: integer}
            local item = { kind = 'text', value = 'ok' }
            if item.kind == 'text' then
                print(#item.value)
            else
                print(item.value + 1)
            end
            """);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6003");
    }

    [Fact]
    public void InfersFunctionParametersReturnsOverloadsAndGenericCalls()
    {
        var result = Analyze(
            """
            ---@generic T
            ---@param value T
            ---@return T
            local function identity(value)
                return value
            end
            local text = identity('hello')
            local number = identity(42)
            return text, number
            """);

        var identity = Assert.Single(result.Functions.Where(static item => item.FunctionId != 0));
        Assert.Equal("T", Assert.Single(identity.Type.TypeParameters).Name);
        Assert.Equal("T", identity.InferredReturns.GetElementOrNil(0).DisplayName);
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "text" && item.InferredType is LuaStringLiteralType);
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "number" && item.InferredType is LuaIntegerLiteralType);
    }

    [Fact]
    public void ReportsCallArgumentAndReturnMismatches()
    {
        var result = Analyze(
            """
            ---@param value string
            ---@return integer
            local function size(value)
                return value
            end
            size(42)
            """);

        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6003" &&
            item.Message.Contains("function return", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6003" &&
            item.Message.Contains("call argument", StringComparison.Ordinal));
    }

    [Fact]
    public void BuildsReachableCfgAndReportsUnreachableStatements()
    {
        var result = Analyze(
            """
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

        var function = Assert.Single(result.Functions.Where(static item => item.FunctionId != 0));
        Assert.Contains(function.ControlFlowGraph.Blocks, static block => !block.IsReachable);
        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6009");
    }

    [Fact]
    public void TracksLoopFixedPointsAndWideningDeterministically()
    {
        const string source = """
            local value = nil
            local index = 0
            while index < 3 do
                if index == 0 then
                    value = 'first'
                else
                    value = index
                end
                index = index + 1
            end
            return value
            """;

        var first = Analyze(source, new LuaAnalysisOptions { MaximumFlowIterations = 2 });
        var second = Analyze(source, new LuaAnalysisOptions { MaximumFlowIterations = 2 });

        Assert.Equal(
            first.Diagnostics.Select(static item => (item.Code, item.Span, item.Message)),
            second.Diagnostics.Select(static item => (item.Code, item.Span, item.Message)));
        Assert.True(first.Functions[0].FlowIterationCount > 0);
    }

    [Fact]
    public void CastDirectivesAddRemoveAndReplaceFlowTypes()
    {
        var result = Analyze(
            """
            local value = nil
            ---@cast value +string
            print(value)
            ---@cast value -nil
            print(#value)
            ---@cast value integer
            print(value + 1)
            """);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6003");
    }

    [Fact]
    public void DefiniteAssignmentAndUnknownGlobalDiagnosticsAreConfigurable()
    {
        var result = Analyze(
            """
            local pending
            print(pending)
            missing(pending)
            """,
            new LuaAnalysisOptions { ReportUnknownGlobals = true });

        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6008");
        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6015");
    }

    [Fact]
    public void AnalysisBudgetsAreGlobalAndBounded()
    {
        var result = Analyze(
            """
            local a = 1
            local b = 2
            local c = a + b
            return c
            """,
            new LuaAnalysisOptions
            {
                MaximumConstraintCount = 1,
                MaximumTypeCount = 4,
                MaximumControlFlowBlockCount = 16,
            });

        Assert.True(result.BudgetUsage.WasExceeded);
        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6010");
        Assert.InRange(result.Diagnostics.Length, 0, 1_000);
    }

    [Fact]
    public void DisabledAnalysisReturnsAnEmptyResult()
    {
        var result = Analyze(
            "---@type string\nlocal value = 42",
            new LuaAnalysisOptions { Enabled = false });

        Assert.Empty(result.TypeDeclarations);
        Assert.Empty(result.Symbols);
        Assert.Empty(result.Diagnostics);
    }

    [Fact]
    public void TypeRelationsNormalizeUnionsAndCheckStructuralFunctions()
    {
        var relations = new LuaTypeRelations(maximumUnionMemberCount: 4);
        var union = relations.Union(
            new LuaIntegerLiteralType(1),
            LuaTypes.Integer,
            LuaTypes.Nil);
        var sourceTable = new LuaStructuralTableType([
            new LuaTableField("name", null, new LuaStringLiteralType("box"u8.ToArray().ToImmutableArray()), false),
        ]);
        var targetTable = new LuaStructuralTableType([
            new LuaTableField("name", null, LuaTypes.String, false),
        ]);
        var sourceFunction = new LuaFunctionType(
            [new LuaFunctionParameter("value", LuaTypes.Any)],
            new LuaTypePack([LuaTypes.Integer]),
            []);
        var targetFunction = new LuaFunctionType(
            [new LuaFunctionParameter("value", LuaTypes.String)],
            new LuaTypePack([LuaTypes.Number]),
            []);

        Assert.Equal("integer|nil", union.DisplayName);
        Assert.True(relations.IsAssignable(sourceTable, targetTable));
        Assert.True(relations.IsAssignable(sourceFunction, targetFunction));
    }

    [Fact]
    public void GenericSubstitutionCoversNestedPacksTablesAndFunctions()
    {
        var relations = new LuaTypeRelations();
        var parameter = new LuaGenericParameterType("T", 0);
        var function = new LuaFunctionType(
            [new LuaFunctionParameter("value", new LuaArrayType(parameter))],
            new LuaTypePack([
                new LuaStructuralTableType([
                    new LuaTableField("value", null, parameter, false),
                ]),
            ]),
            [parameter]);

        var substituted = Assert.IsType<LuaFunctionType>(relations.Substitute(
            function,
            new Dictionary<string, LuaType> { ["T"] = LuaTypes.String }));

        Assert.Equal("string[]", substituted.Parameters[0].Type.DisplayName);
        Assert.Contains("value: string", substituted.Returns.Head[0].DisplayName);
    }

    [Fact]
    public void GenericForInfersPairsAndIpairsVariables()
    {
        var result = Analyze(
            """
            local values = { 'a', 'b' }
            for index, value in ipairs(values) do
                print(index + 1, #value)
            end
            local record = { name = 'box', count = 2 }
            for key, value in pairs(record) do
                print(key, value)
            end
            """);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6003");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "index" && item.InferredType.DisplayName == "integer");
    }

    [Fact]
    public void CfgResolvesGotoLabelsAndLoopBreaks()
    {
        var result = Analyze(
            """
            local value = 0
            ::again::
            value = value + 1
            if value < 2 then goto again end
            while true do
                break
            end
            return value
            """);

        var graph = result.Functions[0].ControlFlowGraph;
        Assert.Contains(graph.Blocks.SelectMany(static block => block.Successors), static edge =>
            edge.Kind == LuaControlFlowEdgeKind.Goto);
        Assert.Contains(graph.Blocks.SelectMany(static block => block.Successors), static edge =>
            edge.Kind == LuaControlFlowEdgeKind.Break);
        Assert.True(graph.Blocks[graph.ExitBlockId].IsReachable);
    }

    [Fact]
    public void DeclarationExtrasDoNotCrossExecutableCode()
    {
        var result = Analyze(
            """
            ---@class First
            ---@field one string
            local First = {}
            ---@field leaked integer
            local value = 1
            """);

        var declaration = Assert.IsType<LuaClassDeclaration>(Assert.Single(result.TypeDeclarations));
        Assert.Single(declaration.Fields);
        Assert.Equal("one", declaration.Fields[0].Name);
    }

    [Fact]
    public void RecursiveAliasesAndUnknownTypesProduceStableDiagnostics()
    {
        var result = Analyze(
            """
            ---@alias A B
            ---@alias B A
            ---@type Missing
            local value = nil
            """);

        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6012");
        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6001");
    }

    [Fact]
    public void ControlFlowBudgetTruncatesWithoutExceedingConfiguredBlocks()
    {
        var result = Analyze(
            """
            local a = 1
            local b = 2
            if a < b then
                a = b
            else
                b = a
            end
            return a + b
            """,
            new LuaAnalysisOptions { MaximumControlFlowBlockCount = 4 });

        Assert.True(result.BudgetUsage.WasExceeded);
        Assert.True(result.BudgetUsage.ControlFlowBlockCount <= 4);
        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6010");
    }

    [Fact]
    public void RandomMalformedSourcesRemainBoundedAndDoNotThrow()
    {
        var random = new Random(0x70003);
        var options = new LuaAnalysisOptions
        {
            MaximumTypeCount = 128,
            MaximumConstraintCount = 128,
            MaximumControlFlowBlockCount = 128,
            MaximumFlowIterations = 4,
            MaximumGenericInstantiationCount = 32,
            MaximumDiagnosticCount = 32,
        };
        for (var iteration = 0; iteration < 200; iteration++)
        {
            var payload = new byte[random.Next(0, 160)];
            random.NextBytes(payload);
            for (var index = 0; index < payload.Length; index++)
            {
                if (payload[index] is (byte)'\r' or (byte)'\n')
                {
                    payload[index] = (byte)'x';
                }
            }

            byte[] source = [.. "---@type "u8, .. payload, (byte)'\n', .. "local value = nil"u8];
            var text = new SourceText(source);
            var lexing = LuaLexer.Lex(text);
            var result = LuaTypeAnalyzer.Analyze(
                LuaBinder.Bind(LuaParser.Parse(lexing)),
                LuaAnnotationParser.Parse(lexing),
                options);

            Assert.InRange(result.Diagnostics.Length, 0, options.MaximumDiagnosticCount);
            Assert.InRange(
                result.BudgetUsage.ControlFlowBlockCount,
                0,
                options.MaximumControlFlowBlockCount + 1);
        }
    }

    [Fact]
    public void SourceDiagnosticDirectivesSuppressAnalysisCodes()
    {
        var result = Analyze(
            """
            ---@type string
            ---@diagnostic disable-next-line: LUA6003
            local first = 1
            ---@diagnostic disable: LUA6003
            ---@type string
            local second = 2
            ---@diagnostic enable: LUA6003
            ---@type string
            local third = 3
            """);

        var diagnostic = Assert.Single(result.Diagnostics.Where(static item => item.Code == "LUA6003"));
        Assert.Contains("third", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellationIsObservedBeforeAnalysisWork()
    {
        var text = SourceText.FromUtf8("local value = 1");
        var lexing = LuaLexer.Lex(text);
        var semantics = LuaBinder.Bind(LuaParser.Parse(lexing));
        var annotations = LuaAnnotationParser.Parse(lexing);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => LuaTypeAnalyzer.Analyze(
            semantics,
            annotations,
            cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ClassOperatorsAndCallableOverloadsParticipateInInference()
    {
        var result = Analyze(
            """
            ---@class Vector
            ---@operator add(Vector): Vector
            ---@operator len: integer
            ---@overload fun(size: integer): Vector
            local Vector = {}
            ---@type Vector
            local left = make()
            ---@type Vector
            local right = make()
            local sum = left + right
            local size = #sum
            local created = left(4)
            return size, created
            """);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code is "LUA6003" or "LUA6004");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "sum" && item.InferredType.DisplayName == "Vector");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "size" && item.InferredType.DisplayName == "integer");
        Assert.Contains(result.Symbols, static item =>
            item.Symbol.Name == "created" && item.InferredType.DisplayName == "Vector");
    }

    [Fact]
    public void UnannotatedConstructorReturnsMetatableInstance()
    {
        var result = Analyze(
            """
            local Dog = {}
            Dog.__index = Dog
            function Dog.new(name)
                return setmetatable({}, Dog)
            end
            function Dog:fetch()
                return "ball"
            end
            local dog = Dog.new("rex")
            local ball = dog.fetch()
            return ball
            """);

        var dog = result.Symbols.Single(static item => item.Symbol.Name == "dog");
        var instance = Assert.IsType<LuaMetatableType>(dog.InferredType);
        // The instance carries the class table as its metatable so members resolve
        // through the receiver; call-through return refinement on rebuilt instances
        // remains a later refinement.
        Assert.Equal(LuaTypeKind.Prototype, instance.MetatableType.Kind);
    }

    [Fact]
    public void MutualClassFieldReferencesStayTyped()
    {
        // Entities hold component tables; components point back at their entity. The
        // reference cycle must resolve to a lazy class reference instead of widening.
        var externalSource = SourceText.FromUtf8(
            """
            ---@class Component
            ---@field entity Entity|nil
            local Component = {}
            """);
        var externalLexing = LuaLexer.Lex(externalSource, LuaLexerOptions.Default);
        var external = LuaExternalTypeDeclarations.Collect(
            LuaAnnotationParser.Parse(externalLexing));
        var environment = new LuaAnalysisEnvironment
        {
            ExternalTypeDeclarations = external,
        };

        var text = SourceText.FromUtf8(
            """
            ---@class Entity
            ---@field components table<string, Component>
            local Entity = {}
            return Entity
            """);
        var lexing = LuaLexer.Lex(text, LuaLexerOptions.Default);
        var syntax = LuaParser.Parse(lexing, LuaParserOptions.Default);
        var annotations = LuaAnnotationParser.Parse(lexing);
        var semantics = LuaBinder.Bind(syntax, LuaBinderOptions.Default);
        var result = LuaTypeAnalyzer.Analyze(semantics, annotations, environment);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6012");
    }

    [Fact]
    public void InheritedAnnotationFieldsResolveForSubclassInstances()
    {
        // A subclass instance's annotated field can be declared on the base class's
        // annotation in another module; the field's type name resolves through the
        // external declaration chain.
        var baseSource = SourceText.FromUtf8(
            """
            ---@class EventBus
            local EventBus = {}
            function EventBus:emit(kind, payload) end
            return EventBus

            ---@class System
            ---@field bus EventBus
            local System = {}
            function System:init(bus) self.bus = bus end
            return System
            """);
        var baseLexing = LuaLexer.Lex(baseSource, LuaLexerOptions.Default);
        var baseAnnotations = LuaAnnotationParser.Parse(baseLexing);
        var externalDeclarations = LuaExternalTypeDeclarations.Collect(baseAnnotations);

        var result = Analyze(
            """
            ---@class QuestSystem : System
            local QuestSystem = {}
            function QuestSystem:tick()
              self.bus:emit("quest", {})
            end
            return QuestSystem
            """,
            environment: new LuaAnalysisEnvironment
            {
                ExternalTypeDeclarations = externalDeclarations,
            });

        var busMember = result.Expressions.FirstOrDefault(static item =>
            item.Type is LuaClassType { Name: "EventBus" });
        Assert.NotNull(busMember);
        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6007");
    }

    [Fact]
    public void ExternalRuntimeMembersAndMetamethodsSatisfyChecks()
    {
        // The workspace knows Vec2's runtime members (`Vec2.new`, `Vec2.__add`) even when
        // the analyzed document cannot see the defining module's code.
        var vec2Class = new LuaClassType("Vec2", []);
        var members = ImmutableDictionary.CreateBuilder<string, LuaType>(StringComparer.Ordinal);
        members["new"] = new LuaFunctionType(
            ImmutableArray.Create(new LuaFunctionParameter("x", LuaTypes.Number)),
            new LuaTypePack(ImmutableArray.Create<LuaType>(vec2Class)),
            ImmutableArray<LuaGenericParameterType>.Empty);
        members["__add"] = new LuaFunctionType(
            ImmutableArray<LuaFunctionParameter>.Empty,
            new LuaTypePack(ImmutableArray.Create<LuaType>(vec2Class)),
            ImmutableArray<LuaGenericParameterType>.Empty);
        var classMembers = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, LuaType>>(
            StringComparer.Ordinal);
        classMembers["Vec2"] = members.ToImmutable();
        var environment = new LuaAnalysisEnvironment
        {
            ExternalClassMembers = classMembers.ToImmutable(),
        };

        var result = Analyze(
            """
            local Vec2 = {}
            ---@param a Vec2
            ---@param b Vec2
            local function combine(a, b)
              local sum = a + b
              local fresh = Vec2.new(1, 2)
              return fresh
            end
            return combine
            """,
            environment: environment);

        Assert.DoesNotContain(result.Diagnostics, static item =>
            item.Code is "LUA6003" or "LUA6007");
    }

    [Fact]
    public void UnknownArithmeticOperandsStayDynamic()
    {
        var classMembers = ImmutableDictionary.CreateBuilder<string, ImmutableDictionary<string, LuaType>>(
            StringComparer.Ordinal);
        classMembers["Vec2"] = ImmutableDictionary.Create<string, LuaType>(StringComparer.Ordinal);
        var result = Analyze(
            """
            ---@param a any
            ---@param b Vec2
            local function combine(a, b)
              local delta = a - b
              return delta:normalize()
            end
            """,
            environment: new LuaAnalysisEnvironment
            {
                ExternalClassMembers = classMembers.ToImmutable(),
            });

        Assert.DoesNotContain(result.Diagnostics, static item =>
            item.Code is "LUA6003" or "LUA6007");
    }

    [Fact]
    public void TypeTestNarrowingDoesNotLeakPastTheGuard()
    {
        var result = Analyze(
            """
            ---@param a any
            ---@param b any
            local function multiply(a, b)
              if type(a) == "number" then
                return a * b
              end
              if type(b) == "number" then
                return a * b
              end
              return a.x * b.x
            end
            return multiply
            """);

        Assert.DoesNotContain(result.Diagnostics, static item =>
            item.Code is "LUA6003" or "LUA6007");
    }

    [Fact]
    public void NilGuardReturnsNarrowMemberPaths()
    {
        var result = Analyze(
            """
            ---@param target {position: number}|nil
            local function aim(target)
              if target == nil then
                return
              end
              return target.position
            end
            return aim
            """);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6020");
    }

    [Fact]
    public void EmptyTableInitializersSatisfyAnnotatedSlots()
    {
        var result = Analyze(
            """
            ---@type table<string, number>
            local counters = {}
            ---@type string[]
            local names = {}
            ---@class Slot
            local slot = {}
            return counters, names, slot
            """);

        Assert.DoesNotContain(result.Diagnostics, static item => item.Code == "LUA6003");
    }

    [Fact]
    public void LiteralContractViolationsStillReport()
    {
        var result = Analyze(
            """
            ---@type number
            local speed = "fast"
            local broken = true + 1
            ---@param a number
            ---@param b number
            ---@return number
            local function add(a, b)
              return a + b
            end
            local sum = add(1)
            return speed, broken, sum
            """);

        Assert.Contains(result.Diagnostics, static item =>
            item.Code == "LUA6003" && item.Message.Contains("'fast'", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static item =>
            item.Code == "LUA6003" && item.Message.Contains("not 'true'", StringComparison.Ordinal));
        Assert.Contains(result.Diagnostics, static item => item.Code == "LUA6006");
    }

    private static LuaAnalysisResult Analyze(
        string source,
        LuaAnalysisOptions? options = null,
        LuaLanguageVersion? version = null,
        LuaAnalysisEnvironment? environment = null)
    {
        var text = SourceText.FromUtf8(source);
        var languageVersion = version ?? LuaLanguageVersions.Default;
        var lexing = LuaLexer.Lex(text, new LuaLexerOptions { LanguageVersion = languageVersion });
        var syntax = LuaParser.Parse(
            lexing,
            new LuaParserOptions { LanguageVersion = languageVersion });
        var annotations = LuaAnnotationParser.Parse(lexing);
        var semantics = LuaBinder.Bind(
            syntax,
            LuaBinderOptions.Default with { LanguageVersion = languageVersion });
        return environment is null
            ? LuaTypeAnalyzer.Analyze(semantics, annotations, options)
            : LuaTypeAnalyzer.Analyze(semantics, annotations, environment, options ?? LuaAnalysisOptions.Default);
    }
}
