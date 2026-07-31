using System.Collections.Immutable;
using Lunil.Analysis;
using Lunil.IR.Canonical;

namespace Lunil.Compiler.Tests;

public sealed class LuaFrontEndSessionTests
{
    [Fact]
    public void SyntaxStageStopsBeforeBindingAndAnalysis()
    {
        var session = new LuaFrontEndSession();

        var snapshot = session.Process(
            LuaSourceDocument.FromUtf8("local value = 42"),
            LuaFrontEndStage.Syntax);

        Assert.Equal(LuaFrontEndStage.Syntax, snapshot.Stage);
        Assert.Null(snapshot.SemanticModel);
        Assert.Null(snapshot.Analysis);
        Assert.Null(snapshot.Module);
        Assert.Equal(
            [
                LuaFrontEndOperation.Lexing,
                LuaFrontEndOperation.Annotation,
                LuaFrontEndOperation.Parsing,
            ],
            snapshot.Metrics.Select(static metric => metric.Operation));
    }

    [Fact]
    public void AdvancingSnapshotReusesSyntaxAndBindingProducts()
    {
        var session = new LuaFrontEndSession();
        var source = LuaSourceDocument.FromUtf8("local value = 42; return value");
        var bound = session.Process(source, LuaFrontEndStage.Binding);

        var verified = session.Advance(bound, LuaFrontEndStage.Verification);

        Assert.Same(bound.Lexing, verified.Lexing);
        Assert.Same(bound.Annotations, verified.Annotations);
        Assert.Same(bound.Syntax, verified.Syntax);
        Assert.Same(bound.SemanticModel, verified.SemanticModel);
        Assert.Equal(LuaFrontEndStage.Verification, verified.Stage);
        Assert.NotNull(verified.Module);
        AssertOperationsOccurOnce(verified);
    }

    [Fact]
    public void AdvancingWithChangedEnvironmentOnlyRepeatsAnalysisAndLaterStages()
    {
        var session = new LuaFrontEndSession();
        var initial = session.Process(
            LuaSourceDocument.FromUtf8("local dep = require('dep'); return dep.value"),
            LuaFrontEndStage.Verification,
            LuaAnalysisEnvironment.Empty);
        var environment = new LuaAnalysisEnvironment
        {
            ModuleTypes = ImmutableDictionary<string, LuaType>.Empty
                .WithComparers(StringComparer.Ordinal)
                .Add("dep", LuaTypes.Any),
        };

        var updated = session.Advance(
            initial,
            LuaFrontEndStage.Verification,
            environment);

        Assert.Same(initial.Syntax, updated.Syntax);
        Assert.Same(initial.SemanticModel, updated.SemanticModel);
        Assert.NotSame(initial.Analysis, updated.Analysis);
        Assert.Equal(1, Count(updated, LuaFrontEndOperation.Lexing));
        Assert.Equal(1, Count(updated, LuaFrontEndOperation.Annotation));
        Assert.Equal(1, Count(updated, LuaFrontEndOperation.Parsing));
        Assert.Equal(1, Count(updated, LuaFrontEndOperation.Binding));
        Assert.Equal(2, Count(updated, LuaFrontEndOperation.Analysis));
        Assert.Equal(2, Count(updated, LuaFrontEndOperation.Lowering));
        Assert.Equal(2, Count(updated, LuaFrontEndOperation.Verification));
    }

    [Fact]
    public void AdvancingCompletedSnapshotDoesNotRepeatWorkOrRegressStage()
    {
        var session = new LuaFrontEndSession();
        var verified = session.Process(
            LuaSourceDocument.FromUtf8("return 42"),
            LuaFrontEndStage.Verification);

        var repeated = session.Advance(verified, LuaFrontEndStage.Verification);
        var lowerTarget = session.Advance(verified, LuaFrontEndStage.Binding);

        Assert.Same(verified, repeated);
        Assert.Same(verified, lowerTarget);
        AssertOperationsOccurOnce(verified);
    }

    [Fact]
    public void SessionRejectsForeignSnapshots()
    {
        var first = new LuaFrontEndSession();
        var second = new LuaFrontEndSession();
        var snapshot = first.Process(
            LuaSourceDocument.FromUtf8("return 42"),
            LuaFrontEndStage.Verification);

        Assert.Throws<ArgumentException>(() =>
            second.Advance(snapshot, LuaFrontEndStage.Verification));
        Assert.Throws<ArgumentException>(() => second.ToCompilationResult(snapshot));
    }

    [Fact]
    public void SessionHonorsCancellationBeforeStartingWork()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() =>
            new LuaFrontEndSession().Process(
                LuaSourceDocument.FromUtf8("return 42"),
                cancellationToken: cancellation.Token));
    }

    [Fact]
    public void StagedAndCompilerPipelinesProduceEquivalentResults()
    {
        var source = LuaSourceDocument.FromUtf8(
            "local total=0; for i=1,10 do total=total+i end; return total",
            "@scripts/sum.lua");
        var session = new LuaFrontEndSession();
        var staged = session.ToCompilationResult(session.Advance(
            session.Process(source, LuaFrontEndStage.Syntax),
            LuaFrontEndStage.Verification));
        var direct = new LuaCompiler().Compile(source);

        Assert.Equal(direct.Diagnostics, staged.Diagnostics);
        Assert.Equal(direct.Syntax.Diagnostics, staged.Syntax.Diagnostics);
        Assert.Equal(direct.Syntax.LanguageVersion, staged.Syntax.LanguageVersion);
        Assert.Equal(direct.SemanticModel.Diagnostics, staged.SemanticModel.Diagnostics);
        Assert.Equal(direct.Analysis.Diagnostics, staged.Analysis.Diagnostics);
        Assert.Equal(
            direct.Analysis.Symbols.Select(static item =>
                (item.Symbol.Name, item.DeclaredType.DisplayName,
                    item.InferredType.DisplayName, item.IsDefinitelyAssigned)),
            staged.Analysis.Symbols.Select(static item =>
                (item.Symbol.Name, item.DeclaredType.DisplayName,
                    item.InferredType.DisplayName, item.IsDefinitelyAssigned)));
        Assert.Equal(direct.Analysis.Functions.Length, staged.Analysis.Functions.Length);
        Assert.Equal(
            direct.Analysis.Functions.Select(static function =>
                function.ControlFlowGraph.Blocks.Length),
            staged.Analysis.Functions.Select(static function =>
                function.ControlFlowGraph.Blocks.Length));
        Assert.NotNull(direct.Module);
        Assert.NotNull(staged.Module);
        Assert.Equal(direct.Module.FormatVersion, staged.Module.FormatVersion);
        Assert.Equal(direct.Module.LanguageVersion, staged.Module.LanguageVersion);
        Assert.Equal(direct.Module.MainFunctionId, staged.Module.MainFunctionId);
        Assert.Equal(direct.Module.Functions.Length, staged.Module.Functions.Length);
        for (var index = 0; index < direct.Module.Functions.Length; index++)
        {
            var directFunction = direct.Module.Functions[index];
            var stagedFunction = staged.Module.Functions[index];
            Assert.Equal(directFunction.Id, stagedFunction.Id);
            Assert.Equal(directFunction.ParentFunctionId, stagedFunction.ParentFunctionId);
            Assert.Equal(directFunction.RegisterCount, stagedFunction.RegisterCount);
            Assert.Equal(directFunction.Constants.Length, stagedFunction.Constants.Length);
            for (var constantIndex = 0;
                 constantIndex < directFunction.Constants.Length;
                 constantIndex++)
            {
                AssertEquivalentConstant(
                    directFunction.Constants[constantIndex],
                    stagedFunction.Constants[constantIndex]);
            }

            Assert.Equal(
                directFunction.Instructions.Select(static instruction =>
                    (instruction.Opcode, instruction.A, instruction.B, instruction.C,
                        instruction.D, instruction.Span, instruction.SourceLine,
                        instruction.LogicalProgramCounter)),
                stagedFunction.Instructions.Select(static instruction =>
                    (instruction.Opcode, instruction.A, instruction.B, instruction.C,
                        instruction.D, instruction.Span, instruction.SourceLine,
                        instruction.LogicalProgramCounter)));
            Assert.Equal(directFunction.SourceName.ToArray(), stagedFunction.SourceName.ToArray());
        }

        Assert.Equal(direct.Succeeded, staged.Succeeded);
    }

    private static int Count(
        LuaFrontEndSnapshot snapshot,
        LuaFrontEndOperation operation) =>
        snapshot.Metrics.Count(metric => metric.Operation == operation);

    private static void AssertEquivalentConstant(
        LuaIrConstant expected,
        LuaIrConstant actual)
    {
        Assert.Equal(expected.Kind, actual.Kind);
        switch (expected.Kind)
        {
            case LuaIrConstantKind.Nil:
                break;
            case LuaIrConstantKind.Boolean:
                Assert.Equal(expected.Boolean, actual.Boolean);
                break;
            case LuaIrConstantKind.Integer:
                Assert.Equal(expected.Integer, actual.Integer);
                break;
            case LuaIrConstantKind.Float:
                Assert.Equal(expected.Float, actual.Float);
                break;
            case LuaIrConstantKind.String:
                Assert.Equal(expected.Bytes.ToArray(), actual.Bytes.ToArray());
                break;
            default:
                throw new InvalidOperationException($"Unexpected constant kind {expected.Kind}.");
        }
    }

    private static void AssertOperationsOccurOnce(LuaFrontEndSnapshot snapshot)
    {
        foreach (var operation in Enum.GetValues<LuaFrontEndOperation>())
        {
            Assert.Equal(1, Count(snapshot, operation));
        }
    }
}
