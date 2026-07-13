using Lunil.Core.Text;
using Lunil.Syntax.Lexing;

namespace Lunil.EmmyLua.Tests;

public sealed class LuaAnnotationParserTests
{
    [Fact]
    public void LuaLsParserBuildsSharedDirectiveAndTypeSyntax()
    {
        var document = Parse(
            """
            ---@class Box<T>: Base
            ---@field private value? T|string[]
            ---@alias Mapper fun(input: {name: string, [string]: number}): boolean
            ---| 'named'
            ---@generic T: Base, U
            ---@param input? T
            ---@return U result
            ---@overload fun(value: string): number
            ---@operator add(Box<T>): Box<T>
            ---@future-tag preserve this payload
            return 1
            """);

        Assert.Equal(LuaAnnotationDialect.LuaLs, document.Dialect);
        Assert.Empty(document.Diagnostics);
        Assert.Collection(
            document.Annotations,
            annotation =>
            {
                var item = Assert.IsType<LuaClassAnnotationSyntax>(annotation);
                Assert.Equal("Box", item.Name);
                Assert.Equal("T", Assert.Single(item.TypeParameters));
                Assert.Single(item.BaseTypes);
            },
            annotation =>
            {
                var field = Assert.IsType<LuaFieldAnnotationSyntax>(annotation);
                Assert.Equal(LuaAnnotationVisibility.Private, field.Visibility);
                Assert.True(field.IsOptional);
                var union = Assert.IsType<LuaUnionTypeSyntax>(field.Type);
                Assert.IsType<LuaArrayTypeSyntax>(union.Types[1]);
            },
            annotation =>
            {
                var alias = Assert.IsType<LuaAliasAnnotationSyntax>(annotation);
                var function = Assert.IsType<LuaFunctionTypeSyntax>(alias.Type);
                Assert.IsType<LuaTableTypeSyntax>(Assert.Single(function.Parameters).Type);
            },
            annotation => Assert.IsType<LuaAliasContinuationAnnotationSyntax>(annotation),
            annotation =>
            {
                var generic = Assert.IsType<LuaGenericAnnotationSyntax>(annotation);
                Assert.Equal(2, generic.Parameters.Length);
                Assert.NotNull(generic.Parameters[0].Constraint);
            },
            annotation => Assert.True(Assert.IsType<LuaParamAnnotationSyntax>(annotation).IsOptional),
            annotation => Assert.Equal("result", Assert.Single(
                Assert.IsType<LuaReturnAnnotationSyntax>(annotation).Returns).Name),
            annotation => Assert.IsType<LuaOverloadAnnotationSyntax>(annotation),
            annotation => Assert.IsType<LuaOperatorAnnotationSyntax>(annotation),
            annotation =>
            {
                var unknown = Assert.IsType<LuaUnknownAnnotationSyntax>(annotation);
                Assert.Equal("future-tag", unknown.UnknownTag);
                Assert.Equal("preserve this payload", unknown.RawText);
            });
    }

    [Fact]
    public void CompatibleModeFallsBackToLegacyParser()
    {
        var document = Parse(
            "---@generic T extends Base\nreturn 1",
            new LuaAnnotationOptions
            {
                Dialect = LuaAnnotationDialect.Compatible,
            });

        Assert.Equal(LuaAnnotationDialect.LegacyEmmyLua, document.Dialect);
        var generic = Assert.IsType<LuaGenericAnnotationSyntax>(Assert.Single(document.Annotations));
        Assert.NotNull(Assert.Single(generic.Parameters).Constraint);
        Assert.Contains(document.Diagnostics, static diagnostic => diagnostic.Code == "LUA5007");
        Assert.DoesNotContain(document.Diagnostics, static diagnostic => diagnostic.Code == "LUA5003");
    }

    [Fact]
    public void LuaLsParserCoversEnumCastVarargAndMarkerDirectives()
    {
        var document = Parse(
            """
            ---@enum Status: string
            ---@cast value +string
            ---@vararg integer
            ---@meta plugin
            return 1
            """);

        Assert.Empty(document.Diagnostics);
        Assert.IsType<LuaEnumAnnotationSyntax>(document.Annotations[0]);
        Assert.Equal(
            LuaCastOperation.Add,
            Assert.IsType<LuaCastAnnotationSyntax>(document.Annotations[1]).Operation);
        Assert.IsType<LuaVarargAnnotationSyntax>(document.Annotations[2]);
        var marker = Assert.IsType<LuaMarkerAnnotationSyntax>(document.Annotations[3]);
        Assert.Equal("plugin", marker.Arguments);
    }

    [Fact]
    public void UnknownTagsArePreservedAndOptionallyReported()
    {
        var quiet = Parse("---@vendor payload\nreturn 1");
        var reported = Parse(
            "---@vendor payload\nreturn 1",
            new LuaAnnotationOptions { ReportUnknownTags = true });

        Assert.IsType<LuaUnknownAnnotationSyntax>(Assert.Single(quiet.Annotations));
        Assert.Empty(quiet.Diagnostics);
        Assert.Contains(reported.Diagnostics, static diagnostic => diagnostic.Code == "LUA5002");
    }

    [Fact]
    public void DiagnosticDisableNextLineSuppressesConfiguredCode()
    {
        var document = Parse(
            """
            ---@diagnostic disable-next-line: LUA5003
            ---@param
            return 1
            """);

        Assert.Equal(2, document.Annotations.Length);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void DiagnosticDisableAndEnableMaintainPerCodeState()
    {
        var document = Parse(
            """
            ---@diagnostic disable: LUA5002
            ---@vendor first
            ---@diagnostic enable: LUA5002
            ---@vendor second
            return 1
            """,
            new LuaAnnotationOptions { ReportUnknownTags = true });

        var diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal("LUA5002", diagnostic.Code);
        Assert.Equal(3, document.Source.GetLocation(diagnostic.Span.Start).Line);
    }

    [Fact]
    public void AnnotationCountAndTypeDepthAreBounded()
    {
        var countLimited = Parse(
            "---@type string\n---@type number\nreturn 1",
            new LuaAnnotationOptions { MaximumAnnotationCount = 1 });
        var depthLimited = Parse(
            "---@type Box<Box<Box<string>>>\nreturn 1",
            new LuaAnnotationOptions { MaximumTypeDepth = 2 });

        Assert.Single(countLimited.Annotations);
        Assert.Contains(countLimited.Diagnostics, static diagnostic => diagnostic.Code == "LUA5004");
        Assert.Contains(depthLimited.Diagnostics, static diagnostic => diagnostic.Code == "LUA5005");
    }

    [Fact]
    public void DiagnosticBudgetAppliesAcrossTheWholeDocument()
    {
        var document = Parse(
            "---@param\n---@param\n---@param\nreturn 1",
            new LuaAnnotationOptions { MaximumDiagnosticCount = 2 });

        Assert.Equal(6, document.ParseErrorCount);
        Assert.Equal(2, document.Diagnostics.Length);
    }

    [Fact]
    public void DisabledAnnotationParsingReturnsAnEmptyDocument()
    {
        var document = Parse(
            "---@type string\nreturn 1",
            new LuaAnnotationOptions { Enabled = false });

        Assert.Empty(document.Annotations);
        Assert.Empty(document.Diagnostics);
    }

    [Fact]
    public void RandomAnnotationBytesRemainBoundedAndDoNotThrow()
    {
        var random = new Random(0x70002);
        var options = new LuaAnnotationOptions
        {
            MaximumAnnotationCount = 4,
            MaximumTokensPerAnnotation = 32,
            MaximumTypeDepth = 8,
            MaximumDiagnosticCount = 16,
        };
        for (var iteration = 0; iteration < 500; iteration++)
        {
            var payload = new byte[random.Next(0, 129)];
            random.NextBytes(payload);
            for (var index = 0; index < payload.Length; index++)
            {
                if (payload[index] is (byte)'\r' or (byte)'\n')
                {
                    payload[index] = (byte)'x';
                }
            }

            byte[] source = [.. "---@type "u8, .. payload, (byte)'\n', .. "return 1"u8];
            var text = new SourceText(source);
            var document = LuaAnnotationParser.Parse(LuaLexer.Lex(text), options);

            Assert.InRange(document.Annotations.Length, 0, 1);
            Assert.InRange(document.Diagnostics.Length, 0, options.MaximumDiagnosticCount);
        }
    }

    private static LuaAnnotationDocument Parse(
        string source,
        LuaAnnotationOptions? options = null)
    {
        var text = SourceText.FromUtf8(source);
        return LuaAnnotationParser.Parse(LuaLexer.Lex(text), options);
    }
}
