using System.Text;
using Lunil.Analysis;
using Lunil.Core.Diagnostics;
using Lunil.EmmyLua;
using Lunil.IR.Canonical;

namespace Lunil.Compiler.Tests;

public sealed class LuaCompilerTests
{
    [Fact]
    public void CompileProducesVerifiedCanonicalModuleAndSourceIdentity()
    {
        var result = new LuaCompiler().CompileUtf8(
            "local total=0; for i=1,10 do total=total+i end; return total",
            "@scripts/sum.lua");

        Assert.True(result.Succeeded);
        Assert.NotNull(result.Module);
        Assert.Empty(result.Diagnostics);
        Assert.Empty(LuaIrVerifier.Verify(result.Module!));
        Assert.All(result.Module!.Functions, function =>
            Assert.Equal("@scripts/sum.lua", Encoding.UTF8.GetString(function.SourceName.AsSpan())));
    }

    [Fact]
    public void CompileAttributesParserAndBindingDiagnosticsWithoutDuplicates()
    {
        var lexerFailure = new LuaCompiler().CompileBytes([0]);
        var parserFailure = new LuaCompiler().CompileUtf8("local = 1");
        var bindingFailure = new LuaCompiler().CompileUtf8("goto missing");

        Assert.False(lexerFailure.Succeeded);
        Assert.Null(lexerFailure.Module);
        Assert.Contains(lexerFailure.Diagnostics, diagnostic =>
            diagnostic.Phase == LuaCompilationPhase.Lexing &&
            diagnostic.Code.StartsWith("LUA1", StringComparison.Ordinal));

        Assert.False(parserFailure.Succeeded);
        Assert.Null(parserFailure.Module);
        Assert.Contains(parserFailure.Diagnostics, diagnostic =>
            diagnostic.Phase == LuaCompilationPhase.Parsing &&
            diagnostic.Code.StartsWith("LUA2", StringComparison.Ordinal));
        Assert.Equal(
            parserFailure.Diagnostics.Distinct().Count(),
            parserFailure.Diagnostics.Length);

        Assert.False(bindingFailure.Succeeded);
        Assert.Null(bindingFailure.Module);
        Assert.Contains(bindingFailure.Diagnostics, diagnostic =>
            diagnostic.Phase == LuaCompilationPhase.Binding &&
            diagnostic.Code.StartsWith("LUA3", StringComparison.Ordinal));
    }

    [Fact]
    public void ConfiguredVerifierBudgetRejectsOtherwiseValidModule()
    {
        var compiler = new LuaCompiler(new LuaCompilerOptions
        {
            Verifier = new LuaIrVerifierOptions { MaximumFunctions = 0 },
        });

        var result = compiler.CompileUtf8("return 1");

        Assert.False(result.Succeeded);
        Assert.Null(result.Module);
        var diagnostic = Assert.Single(result.Diagnostics);
        Assert.Equal(LuaCompilationPhase.Verification, diagnostic.Phase);
        Assert.Equal("LUA4003", diagnostic.Code);
    }

    [Fact]
    public void CompileHonorsCancellationBeforePublishingAnyResult()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new LuaCompiler().CompileUtf8("return 1", cancellationToken: cancellation.Token));
    }

    [Fact]
    public void ByteSourcePreservesInvalidUtf8InsideComment()
    {
        byte[] source = [.. "-- "u8, 0xff, (byte)'\n', .. "return 7"u8];

        var result = new LuaCompiler().CompileBytes(source, "=bytes");

        Assert.True(result.Succeeded);
        Assert.Equal(source, result.Source.Text.ToArray());
    }

    [Fact]
    public void CompilePublishesAnnotationsWithoutChangingRuntimeLowering()
    {
        var result = new LuaCompiler().CompileUtf8(
            "---@type string|number\nreturn 42",
            "=annotations");

        Assert.True(result.Succeeded);
        var annotation = Assert.IsType<LuaTypeAnnotationSyntax>(
            Assert.Single(result.Annotations.Annotations));
        Assert.IsType<LuaUnionTypeSyntax>(Assert.Single(annotation.Types));
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Module);
    }

    [Fact]
    public void ConfiguredAnnotationErrorsPreventModulePublication()
    {
        var compiler = new LuaCompiler(new LuaCompilerOptions
        {
            Annotations = new LuaAnnotationOptions
            {
                SyntaxDiagnosticSeverity = DiagnosticSeverity.Error,
                ReportUnknownTags = true,
            },
        });

        var result = compiler.CompileUtf8("---@vendor payload\nreturn 42");

        Assert.False(result.Succeeded);
        Assert.Null(result.Module);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Phase == LuaCompilationPhase.Annotation &&
            diagnostic.Code == "LUA5002");
    }

    [Fact]
    public void CompilePublishesTypeAndControlFlowAnalysis()
    {
        var result = new LuaCompiler().CompileUtf8(
            "---@type string|nil\nlocal value = nil\nif value then return value end");

        Assert.True(result.Succeeded);
        Assert.Contains(result.Analysis.Symbols, static item => item.Symbol.Name == "value");
        Assert.Single(result.Analysis.Functions);
        Assert.NotEmpty(result.Analysis.Functions[0].ControlFlowGraph.Blocks);
    }

    [Fact]
    public void ConfiguredAnalysisErrorsPreventModulePublication()
    {
        var compiler = new LuaCompiler(new LuaCompilerOptions
        {
            Analysis = new LuaAnalysisOptions
            {
                DiagnosticSeverity = DiagnosticSeverity.Error,
            },
        });

        var result = compiler.CompileUtf8("---@type string\nlocal value = 42\nreturn value");

        Assert.False(result.Succeeded);
        Assert.Null(result.Module);
        Assert.Contains(result.Diagnostics, static diagnostic =>
            diagnostic.Phase == LuaCompilationPhase.Analysis &&
            diagnostic.Code == "LUA6003");
    }
}
