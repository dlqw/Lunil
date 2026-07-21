using Lunil.CodeGen.Cil.Jit;
using Lunil.Compiler;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.StandardLibrary;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaVersionJitSmokeTests
{
    public static IEnumerable<object[]> Versions() =>
        Enum.GetValues<LuaLanguageVersion>().Select(version => new object[] { version });

    [Theory]
    [MemberData(nameof(Versions))]
    public void AutoJitCompletesSimpleArithmeticForEachLanguageVersion(LuaLanguageVersion version)
    {
        var compilation = new LuaCompiler(new LuaCompilerOptions { LanguageVersion = version })
            .CompileUtf8("local sum = 0; for i = 1, 50 do sum = sum + i end; return sum", "@jit-smoke.lua");
        Assert.True(compilation.Succeeded, string.Join(Environment.NewLine, compilation.Diagnostics));

        var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
        LuaStandardLibrary.InstallAll(state);
        using var jit = new LuaJitExecutor(LuaJitExecutorOptions.Default with
        {
            SynchronousCompilation = true,
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
        });
        var result = jit.Execute(state, state.CreateMainClosure(compilation.Module!));
        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        // 1+...+50 = 1275; 5.1/5.2 use float numbers.
        if (version is LuaLanguageVersion.Lua51 or LuaLanguageVersion.Lua52)
        {
            Assert.Equal(1275.0, result.Values[0].AsFloat(), 1e-9);
        }
        else
        {
            Assert.Equal(1275, result.Values[0].AsInteger());
        }
    }

    [Theory]
    [InlineData(LuaLanguageVersion.Lua51, true)]
    [InlineData(LuaLanguageVersion.Lua52, true)]
    [InlineData(LuaLanguageVersion.Lua53, false)]
    [InlineData(LuaLanguageVersion.Lua54, false)]
    [InlineData(LuaLanguageVersion.Lua55, false)]
    public void Tier2NormalizesLegacyIntegerHintsByLanguageVersion(
        LuaLanguageVersion version,
        bool expectFloatGuard)
    {
        var compilation = new LuaCompiler(new LuaCompilerOptions
        {
            LanguageVersion = version,
        }).CompileUtf8("local value = ...; return value + 1", "@tier2-version.lua");
        Assert.True(compilation.Succeeded, string.Join(Environment.NewLine, compilation.Diagnostics));

        var module = compilation.Module!;
        var binaryPc = Enumerable.Range(0, module.Functions[0].Instructions.Length)
            .Single(pc => module.Functions[0].Instructions[pc].Opcode == LuaIrOpcode.Binary);
        var profile = new LuaJitFunctionProfile(
            Samples: 1,
            ArgumentKinds: [],
            Sites:
            [
                new LuaJitSiteProfile(
                    binaryPc,
                    LuaIrOpcode.Binary,
                    Samples: 1,
                    FirstOperandKinds: LuaJitValueKinds.Integer,
                    SecondOperandKinds: LuaJitValueKinds.Integer,
                    ThirdOperandKinds: LuaJitValueKinds.None,
                    BranchTaken: 0,
                    BranchNotTaken: 0,
                    IsMegamorphic: false,
                    TableShapes: [],
                    CallTargets: []),
            ]);

        var result = ProfileGuidedLuaTier2Compiler.Instance.Compile(
            module,
            0,
            profile,
            CancellationToken.None);
        Assert.True(result.Succeeded, string.Join(Environment.NewLine, result.Diagnostics));
        var optimization = Assert.Single(
            result.Plan!.Optimizations,
            item => item.ProgramCounter == binaryPc &&
                item.Kind == LuaJitOptimizationKind.NumericBinary);

        if (expectFloatGuard)
        {
            Assert.Contains("Float", optimization.Guard);
            Assert.DoesNotContain("Integer", optimization.Guard);
        }
        else
        {
            Assert.Contains("Integer", optimization.Guard);
            Assert.DoesNotContain("Float", optimization.Guard);
        }
    }

    [Fact]
    public void JitModuleIdentityRejectsCrossVersionReuse()
    {
        var lua53 = new LuaCompiler(new LuaCompilerOptions { LanguageVersion = LuaLanguageVersion.Lua53 })
            .CompileUtf8("return 1", "@v53.lua");
        var state54 = new LuaState(new LuaStateOptions { LanguageVersion = LuaLanguageVersion.Lua54 });
        Assert.ThrowsAny<Exception>(() => state54.CreateMainClosure(lua53.Module!));
    }
}
