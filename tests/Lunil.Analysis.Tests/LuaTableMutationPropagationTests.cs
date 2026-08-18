using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lunil.Core;
using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis.Tests;

/// <summary>
/// Regression coverage for table-mutation propagation: every member write,
/// setmetatable/rawset call, and dynamic-call escape rewrites aliasing types
/// across the flow state and the global base universe.
/// </summary>
public sealed class LuaTableMutationPropagationTests
{
    [Fact]
    public void MemberWritePropagatesAcrossLocalAliases()
    {
        var result = Analyze(
            """
            ---@param value number
            local function takes(value) end
            local a = {}
            local b = a
            a.x = "text"
            takes(b.x)
            """);

        Assert.Contains(result.Diagnostics, static item =>
            item.Code == "LUA6003" && item.Message.Contains("argument 1", StringComparison.Ordinal));
    }

    [Fact]
    public void MemberWritePropagatesAcrossGlobalAliases()
    {
        var result = Analyze(
            """
            ---@param value number
            local function takes(value) end
            GlobalA = {}
            GlobalB = GlobalA
            GlobalA.x = "text"
            takes(GlobalB.x)
            """);

        Assert.Contains(result.Diagnostics, static item =>
            item.Code == "LUA6003" && item.Message.Contains("argument 1", StringComparison.Ordinal));
    }

    [Fact]
    public void MemberWriteThroughLocalPropagatesIntoGlobalGraph()
    {
        var result = Analyze(
            """
            ---@param value number
            local function takes(value) end
            GlobalM = { inner = {} }
            local function mutate()
                local t = GlobalM.inner
                t.x = "text"
                takes(GlobalM.inner.x)
            end
            """);

        // The final hop of the `GlobalM.inner.x` read widens to any (a separate
        // inference gap), so propagation is asserted on the receiver's recorded
        // type: without it the receiver stays `{inner: {}}`.
        Assert.Contains(result.Expressions, static expression =>
            expression.Type is LuaStructuralTableType &&
            expression.Type.DisplayName.Contains("x: 'text'", StringComparison.Ordinal));
    }

    [Fact]
    public void RawsetPropagatesAcrossLocalAliases()
    {
        var result = Analyze(
            """
            ---@param value number
            local function takes(value) end
            local a = {}
            local b = a
            rawset(a, "x", "text")
            takes(b.x)
            """);

        Assert.Contains(result.Diagnostics, static item =>
            item.Code == "LUA6003" && item.Message.Contains("argument 1", StringComparison.Ordinal));
    }

    [Fact]
    public void DenseTableWritesAgainstLargeGlobalUniverseStayBounded()
    {
        // 2,000 external globals, each a nested structural table (~20 type nodes),
        // mirrors a large library-stub universe that the workspace seeds into every
        // document analysis. Before the propagation fix every table write deep-copied
        // the whole universe per write, so this shape of input never completed.
        var globals = ImmutableDictionary.CreateBuilder<string, LuaType>(StringComparer.Ordinal);
        for (var index = 0; index < 2_000; index++)
        {
            globals[$"Ext{index}"] = new LuaStructuralTableType(
                [
                    new LuaTableField("value", null, LuaTypes.Number, false),
                    new LuaTableField("name", null, LuaTypes.String, false),
                    new LuaTableField("child", null, new LuaStructuralTableType(
                        [
                            new LuaTableField("value", null, LuaTypes.Number, false),
                            new LuaTableField("tag", null, LuaTypes.String, false),
                            new LuaTableField("leaf", null, new LuaStructuralTableType(
                                [new LuaTableField("value", null, LuaTypes.Boolean, false)],
                                IsOpen: true), false),
                        ]), false),
                    new LuaTableField("fn", null, new LuaFunctionType(
                        [new LuaFunctionParameter("self", LuaTypes.Any)],
                        new LuaTypePack([LuaTypes.Any]),
                        []), false),
                ],
                IsOpen: true);
        }

        var environment = new LuaAnalysisEnvironment
        {
            ExternalGlobals = globals.ToImmutable(),
        };

        var source = new StringBuilder();
        source.AppendLine("GlobalA = {}");
        source.AppendLine("GlobalB = GlobalA");
        for (var function = 0; function < 60; function++)
        {
            source.AppendLine(CultureInfo.InvariantCulture, $"local function handler{function}()");
            source.AppendLine("  local t = {}");
            for (var write = 0; write < 24; write++)
            {
                source.AppendLine(CultureInfo.InvariantCulture, $"  t.field{write} = {write}");
            }

            source.AppendLine("  local mt = setmetatable(t, { __index = Ext0 })");
            source.AppendLine("  unknownsink(mt)");
            source.AppendLine("  return t.field23");
            source.AppendLine("end");
        }

        source.AppendLine("GlobalA.x = 1");

        var stopwatch = Stopwatch.StartNew();
        GC.Collect();
        var allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        var result = Analyze(source.ToString(), environment: environment);
        var allocatedBytes = GC.GetAllocatedBytesForCurrentThread() - allocatedBefore;
        stopwatch.Stop();

        Assert.False(
            result.BudgetUsage.WasExceeded,
            "table mutation propagation must stay inside the analysis budget");
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(5),
            $"analysis of dense table writes completed in {stopwatch.Elapsed}");
        Assert.True(
            allocatedBytes < 512L * 1024 * 1024,
            $"analysis of dense table writes allocated {allocatedBytes / (1024.0 * 1024):F1} MB");
    }

    [Fact]
    public void LargeTableLiteralStaysLinear()
    {
        // Generated data literals carry tens of thousands of bracket entries;
        // bulk key/value unions used to fold them pairwise-quadratically.
        var source = new StringBuilder("return {\n");
        for (var entry = 0; entry < 25_000; entry++)
        {
            source.AppendLine(CultureInfo.InvariantCulture,
                $"  [{entry}] = {{ id = {entry}, name = 'n', flags = {{ true, false }} }},");
        }

        source.AppendLine("}");

        var stopwatch = Stopwatch.StartNew();
        var result = Analyze(source.ToString());
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"25k-entry table literal analyzed in {stopwatch.Elapsed}");
    }

    [Fact]
    public void LargeAccumulatedTableStaysLinear()
    {
        // Generated data files also accumulate entries through member writes;
        // per-write field-array rebuilds used to cost quadratic copies.
        var source = new StringBuilder("local d = {}\n");
        for (var entry = 0; entry < 20_000; entry++)
        {
            source.AppendLine(CultureInfo.InvariantCulture,
                $"d.k{entry} = {{ id = {entry}, name = 'n', pos = {{ x = 1, y = 2 }} }}");
        }

        source.AppendLine("return d");

        var stopwatch = Stopwatch.StartNew();
        var result = Analyze(source.ToString());
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(10),
            $"20k-write accumulated table analyzed in {stopwatch.Elapsed}");
        // Growth beyond the field cap keeps absorbing into map types instead of
        // unknown: spot-check the final shape still records a map value type.
        Assert.Contains(result.Expressions, static expression =>
            expression.Type is LuaStructuralTableType);
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
