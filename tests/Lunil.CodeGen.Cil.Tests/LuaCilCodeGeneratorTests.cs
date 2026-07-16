using System.Collections.Immutable;
using System.Runtime.CompilerServices;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.CodeGen.Cil.Analysis;
using Lunil.CodeGen.Cil.Emission;
using Lunil.CodeGen.Cil.Loading;
using Lunil.CodeGen.Cil.Jit;
using Lunil.CodeGen.Cil.Planning;
using Lunil.CodeGen.Cil.Verification;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.CodeGen.Cil.Tests;

public sealed class LuaCilCodeGeneratorTests
{
    [Fact]
    public void CanonicalModuleIdentityRoundTripsVerifiedIr()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(42)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var bytes = LuaAotModuleIdentity.SerializeCanonicalModule(module);

        var roundTripped = LuaAotModuleIdentity.DeserializeCanonicalModule(bytes);

        Assert.Equal(
            LuaAotModuleIdentity.ComputeContentId(module),
            LuaAotModuleIdentity.ComputeContentId(roundTripped));
        Assert.Equal(bytes, LuaAotModuleIdentity.SerializeCanonicalModule(roundTripped));
    }

    [Fact]
    public void PlansAndVerifiesTheInitialCanonicalOpcodeSubset()
    {
        var module = CreateModule(
            registerCount: 2,
            constants: [LuaIrConstant.FromInteger(42)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Move, a: 1, b: 0),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 1, b: 4),
            new LuaIrInstruction(LuaIrOpcode.SetTop, a: 2),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var result = LuaCilCodeGenerator.PlanFunction(module, 0);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(static d => d.Message)));
        Assert.NotNull(result.Plan);
        Assert.Contains(result.Plan.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCodegenAbiV1.MaterializeConstant");
        Assert.Contains(result.Plan.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCompiledExit.Return");
        Assert.Contains(result.Plan.GcMaps, map => map.CanonicalProgramCounter == 0);
        Assert.Equal(3, result.Plan.Blocks.Length);
    }

    [Fact]
    public void LowersNonNumericOpcodesThroughRuntimeAbiV3WithoutInterpreterSlowPath()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.NewTable, a: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var result = LuaCilCodeGenerator.PlanFunction(module, 0);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(static d => d.Message)));
        Assert.Contains(result.Plan!.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCodegenAbiV3.ExecuteNewTable");
        Assert.DoesNotContain(result.Plan.Instructions, instruction =>
            instruction.CanonicalProgramCounter == 0 &&
            instruction.CallTarget?.Id == "LuaCodegenAbiV1.ExecuteCanonicalInstruction");
        Assert.Contains(0, result.Plan.DirectCanonicalProgramCounters);
        Assert.DoesNotContain(result.Plan.Instructions, instruction =>
            instruction.CanonicalProgramCounter == 0 &&
            instruction.CallTarget?.Id == "LuaCompiledExit.Deopt");
    }

    [Fact]
    public void LowersOrdinaryCallsThroughTheInMethodFramelessFastPath()
    {
        var module = CreateModule(
            registerCount: 2,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Call, a: 0, b: 1, c: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var result = LuaCilCodeGenerator.PlanFunction(module, 0);

        Assert.True(result.Succeeded, string.Join("; ", result.Diagnostics.Select(static d => d.Message)));
        Assert.Contains(result.Plan!.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCodegenAbiV3.TryExecuteFramelessCall");
        Assert.Contains(result.Plan.Instructions, instruction =>
            instruction.CallTarget?.Id == "LuaCompiledExit.Call");
        Assert.Contains(result.Plan.Locals, local => local.Name == "framelessConsumed");
    }

    [Fact]
    public void EmitsDeterministicManagedPeAndPortablePdbArtifacts()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(7)],
            new LuaIrInstruction(
                LuaIrOpcode.LoadConstant,
                a: 0,
                b: 0,
                sourceLine: 1,
                logicalProgramCounter: 10),
            new LuaIrInstruction(
                LuaIrOpcode.Return,
                a: 0,
                b: 1,
                sourceLine: 2,
                logicalProgramCounter: 11));
        var options = LuaAotCompilationOptions.Default with
        {
            SourceDocument = new LuaAotSourceDocument
            {
                LogicalName = "module.lua",
                Content = "return 7\n"u8.ToArray().ToImmutableArray(),
            },
        };

        var first = LuaAotCompiler.Compile(module, options);
        var second = LuaAotCompiler.Compile(module, options);

        Assert.True(first.Succeeded, string.Join("; ", first.Diagnostics.Select(static d => d.Message)));
        Assert.True(first.Artifact!.PeImage.SequenceEqual(second.Artifact!.PeImage));
        Assert.True(first.Artifact.PortablePdbImage.SequenceEqual(second.Artifact.PortablePdbImage));
        Assert.False(first.Artifact.Manifest.ProfileGuidedNumericRegions);
        Assert.Equal(
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855",
            first.Artifact.Manifest.ProfileFingerprint);
        Assert.Equal((byte)'M', first.Artifact.PeImage[0]);
        Assert.Equal((byte)'Z', first.Artifact.PeImage[1]);

        using var peStream = new MemoryStream(first.Artifact.PeImage.ToArray());
        using var peReader = new PEReader(peStream);
        var metadata = peReader.GetMetadataReader();
        Assert.Contains(metadata.ManifestResources.Select(handle =>
            metadata.GetString(metadata.GetManifestResource(handle).Name)),
            name => name == "lunil.aot.manifest.json");

        using var pdbStream = new MemoryStream(first.Artifact.PortablePdbImage.ToArray());
        using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
        var pdb = provider.GetMetadataReader();
        Assert.Single(pdb.Documents);
        Assert.Contains(
            pdb.MethodDebugInformation
                .Select(handle => pdb.GetMethodDebugInformation(handle))
                .SelectMany(static information => information.GetSequencePoints()),
            point => point.StartLine == 1);
        var custom = Assert.Single(pdb.CustomDebugInformation.Select(handle =>
            pdb.GetCustomDebugInformation(handle)));
        Assert.Equal(
            LuaAotPortablePdbMetadata.ProgramCounterMapKind,
            pdb.GetGuid(custom.Kind));
        var programCounters = LuaAotPortablePdbMetadata.DecodeProgramCounterMap(
            pdb.GetBlobBytes(custom.Value));
        Assert.Contains(programCounters, entry =>
            entry.CanonicalProgramCounter == 0 && entry.LogicalProgramCounter == 10);
    }

    [Fact]
    public void LoadsAndInvokesPersistedCilFromACollectibleContext()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(7)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;

        var loading = LuaAotArtifactLoader.Load(artifact);

        Assert.True(loading.Succeeded, string.Join("; ", loading.Diagnostics.Select(static d => d.Message)));
        using var loaded = loading.Module!;
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 2);

        var exit = loaded.GetFunction(0)(context, thread, frame);

        Assert.Equal(LuaCompiledExitKind.Return, exit.Kind);
        Assert.Equal(2, exit.InstructionsConsumed);
        Assert.Equal(LuaValue.FromInteger(7), thread.Stack[0]);
        Assert.True(loaded.LoadContextWeakReference.IsAlive);
        Assert.False(loaded.IsDisposed);
    }

    [Fact]
    public void ExecutesLoadedPersistedCilThroughTheSharedScheduler()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(11)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;
        var loading = LuaAotArtifactLoader.Load(artifact);

        Assert.True(
            loading.Succeeded,
            string.Join("; ", loading.Diagnostics.Select(static item => item.Message)));
        Assert.True(loading.Metrics.ValidationDuration > TimeSpan.Zero);
        Assert.True(loading.Metrics.AssemblyLoadDuration > TimeSpan.Zero);
        Assert.True(loading.Metrics.DelegateBindingDuration > TimeSpan.Zero);
        Assert.True(loading.Metrics.TotalDuration >= loading.Metrics.ValidationDuration);
        Assert.True(loading.Metrics.AllocatedBytes > 0);
        using var loaded = loading.Module!;
        var executor = new LuaPersistedAotExecutor(loaded);
        var state = new LuaState();

        var result = executor.Execute(state, state.CreateMainClosure(module));

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.Equal(LuaValue.FromInteger(11), Assert.Single(result.Values));
        Assert.True(executor.Statistics.CompiledInvocations > 0);
        Assert.Equal(0, executor.Statistics.InterpreterFallbacks);
        Assert.Equal(0, executor.Statistics.Deoptimizations);
        Assert.Equal(0, executor.Statistics.DebugModeDeoptimizations);
        Assert.Equal(0, executor.Statistics.UnexpectedDeoptimizations);
    }

    [Fact]
    public void PersistedAbiV4RoundTripsNonNumericOpcodesAndExecutesThem()
    {
        var module = CompileSource(
            """
            local values = { 10, 20 }
            values.answer = 40
            local function add(...)
                local left, right = ...
                return left + right
            end
            return values[1] + add(values.answer, 2)
            """);
        var opcodes = module.Functions
            .SelectMany(static function => function.Instructions)
            .Select(static instruction => instruction.Opcode)
            .ToHashSet();
        Assert.All(
            new[]
            {
                LuaIrOpcode.NewTable,
                LuaIrOpcode.GetTable,
                LuaIrOpcode.SetTable,
                LuaIrOpcode.SetList,
                LuaIrOpcode.Closure,
                LuaIrOpcode.VarArg,
                LuaIrOpcode.Call,
            },
            opcode => Assert.Contains(opcode, opcodes));

        var compilation = LuaAotCompiler.Compile(module);
        Assert.True(
            compilation.Succeeded,
            string.Join("; ", compilation.Diagnostics.Select(static item => item.Message)));
        var artifact = compilation.Artifact!;
        Assert.Equal(LuaCodegenAbiV4.RuntimeAbiVersion, artifact.Manifest.RuntimeAbiVersion);
        var loading = LuaAotArtifactLoader.Load(artifact);
        Assert.True(
            loading.Succeeded,
            string.Join("; ", loading.Diagnostics.Select(static item => item.Message)));

        using var loaded = loading.Module!;
        var state = new LuaState();
        var executor = new LuaPersistedAotExecutor(loaded);
        var result = executor.Execute(
            state,
            state.CreateMainClosure(module));

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.Equal(LuaValue.FromInteger(52), Assert.Single(result.Values));
    }

    [Fact]
    public void ProfileGuidedPersistedAotEmitsAndExecutesExactNumericRegions()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CompileSource(
            "local total = 0; for i = 1, 1000 do total = total + i end; return total");
        var profile = TrainProfile(module);

        var compilation = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = profile,
        });

        Assert.True(
            compilation.Succeeded,
            string.Join("; ", compilation.Diagnostics.Select(static item => item.Message)));
        var artifact = compilation.Artifact!;
        Assert.True(artifact.Manifest.ProfileGuidedNumericRegions);
        Assert.Equal(
            LuaAotArtifactManifest.CurrentProfilePolicyVersion,
            artifact.Manifest.ProfilePolicyVersion);
        Assert.NotEmpty(artifact.Manifest.Functions.SelectMany(
            static function => function.NumericRegions));
        Assert.All(
            artifact.Manifest.Functions.SelectMany(static function => function.NumericRegions),
            static region =>
            {
                Assert.True(region.UnboxedNumericLocalCount > 0);
                Assert.True(region.DirectNumericInstructionCount > 0);
                Assert.True(region.SafepointCount > 0);
            });

        var loading = LuaAotArtifactLoader.Load(artifact);
        Assert.True(
            loading.Succeeded,
            string.Join("; ", loading.Diagnostics.Select(static item => item.Message)));
        using var loaded = loading.Module!;
        var state = new LuaState();
        var executor = new LuaPersistedAotExecutor(loaded);
        var result = executor.Execute(
            state,
            state.CreateMainClosure(module));

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.Equal(LuaValue.FromInteger(500_500), Assert.Single(result.Values));
        Assert.Equal(0, executor.Statistics.UnexpectedDeoptimizations);
        Assert.True(executor.Statistics.CompiledInvocations > 0);
    }

    [Fact]
    public void ProfileGuidedPersistedAotGuardsMixedKindsAndFallsBackExactly()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CompileSource(
            "local value = ...; for i = 1, 100 do value = value + 1 end; return value");
        var profile = TrainProfile(module, LuaValue.FromInteger(0));
        var compilation = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = profile,
        });
        Assert.True(
            compilation.Succeeded,
            string.Join("; ", compilation.Diagnostics.Select(static item => item.Message)));
        var loading = LuaAotArtifactLoader.Load(compilation.Artifact!);
        Assert.True(
            loading.Succeeded,
            string.Join("; ", loading.Diagnostics.Select(static item => item.Message)));

        using var loaded = loading.Module!;
        var executor = new LuaPersistedAotExecutor(loaded);
        var state = new LuaState();
        var result = executor.Execute(
            state,
            state.CreateMainClosure(module),
            [LuaValue.FromFloat(0.5)]);

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
        Assert.Equal(LuaValue.FromFloat(100.5), Assert.Single(result.Values));
        Assert.True(executor.Statistics.Deoptimizations > 0);
    }

    [Fact]
    public void PersistedAotRejectsAProfileFromAnotherModule()
    {
        var source = CompileSource("local total = 0; for i = 1, 10 do total = total + i end; return total");
        var other = CompileSource("return 42");
        var profile = TrainProfile(source);

        var compilation = LuaAotCompiler.Compile(other, new LuaAotCompilationOptions
        {
            Profile = profile,
        });

        Assert.False(compilation.Succeeded);
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "AOT1005");
    }

    [Theory]
    [InlineData("local v = 9223372036854775800; for i = 1, 100 do v = v + i * 7 end; return v")]
    [InlineData("local v = 0.5; for i = 100, 1, -0.5 do v = v + i / 3.0 + (i % 2.5) end; return v")]
    [InlineData("local v = 0; for i = 100, 1, -1 do if i % 3 == 0 then v = v + i else v = v - i end end; return v")]
    public void ProfileGuidedPersistedAotPreservesNumericAndControlSemantics(string source)
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CompileSource(source);
        var profile = TrainProfile(module);
        var compilation = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = profile,
        });
        Assert.True(
            compilation.Succeeded,
            string.Join("; ", compilation.Diagnostics.Select(static item => item.Message)));
        Assert.NotEmpty(compilation.Artifact!.Manifest.Functions.SelectMany(
            static function => function.NumericRegions));

        var referenceState = new LuaState();
        var reference = new LuaInterpreter().Execute(
            referenceState,
            referenceState.CreateMainClosure(module));
        var loading = LuaAotArtifactLoader.Load(compilation.Artifact);
        Assert.True(
            loading.Succeeded,
            string.Join("; ", loading.Diagnostics.Select(static item => item.Message)));

        using var loaded = loading.Module!;
        var executor = new LuaPersistedAotExecutor(loaded);
        var state = new LuaState();
        var actual = executor.Execute(state, state.CreateMainClosure(module));

        Assert.Equal(reference.Signal, actual.Signal);
        Assert.True(reference.Values.SequenceEqual(actual.Values));
        Assert.Equal(0, executor.Statistics.UnexpectedDeoptimizations);
    }

    [Fact]
    public void PersistedAotProfileFingerprintIsStableAndChangesArtifactIdentity()
    {
        var module = CompileSource(
            "local value = ...; for i = 1, 100 do value = value + i end; return value");
        var integerProfile = TrainProfile(module, LuaValue.FromInteger(0));
        var floatProfile = TrainProfile(module, LuaValue.FromFloat(0.5));

        var first = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = integerProfile,
        }).Artifact!;
        var repeated = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = integerProfile,
        }).Artifact!;
        var changed = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = floatProfile,
        }).Artifact!;

        Assert.Equal(first.Manifest.ProfileFingerprint, repeated.Manifest.ProfileFingerprint);
        Assert.Equal(first.Manifest.OptionsFingerprint, repeated.Manifest.OptionsFingerprint);
        Assert.Equal(first.Manifest.AssemblyName, repeated.Manifest.AssemblyName);
        Assert.NotEqual(first.Manifest.ProfileFingerprint, changed.Manifest.ProfileFingerprint);
        Assert.NotEqual(first.Manifest.OptionsFingerprint, changed.Manifest.OptionsFingerprint);
        Assert.NotEqual(first.Manifest.AssemblyName, changed.Manifest.AssemblyName);
    }

    [Fact]
    public void PersistedAotRejectsMalformedProfileFunctionCoverage()
    {
        var module = CompileSource(
            "local total = 0; for i = 1, 10 do total = total + i end; return total");
        var profile = TrainProfile(module);
        var malformed = profile with
        {
            Functions = profile.Functions.Add(profile.Functions[0]),
        };

        var compilation = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = malformed,
        });

        Assert.False(compilation.Succeeded);
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "AOT1006");
    }

    [Fact]
    public void PersistedAotRejectsNullProfileEntriesAsDiagnostics()
    {
        var module = CompileSource(
            "local total = 0; for i = 1, 10 do total = total + i end; return total");
        var profile = TrainProfile(module);
        var malformed = profile with
        {
            Functions = [new LuaJitFunctionProfileEntry(0, null!)],
        };

        var compilation = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = malformed,
        });

        Assert.False(compilation.Succeeded);
        Assert.Contains(compilation.Diagnostics, static diagnostic => diagnostic.Code == "AOT1006");
    }

    [Fact]
    public void PersistedAotRejectsCorruptedProfilePolicyAndNumericRegionManifest()
    {
        var module = CompileSource(
            "local total = 0; for i = 1, 1000 do total = total + i end; return total");
        var artifact = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = TrainProfile(module),
        }).Artifact!;

        var stalePolicy = artifact.PeImage.ToArray();
        var policy = "\"profilePolicyVersion\":1"u8;
        var policyOffset = stalePolicy.AsSpan().IndexOf(policy);
        Assert.True(policyOffset >= 0);
        stalePolicy[policyOffset + policy.Length - 1] = (byte)'2';
        var stalePolicyValidation = LuaAotArtifactLoader.Validate(
            stalePolicy.ToImmutableArray(),
            artifact.PortablePdbImage);
        Assert.False(stalePolicyValidation.Succeeded);
        Assert.Contains(
            stalePolicyValidation.Diagnostics,
            static diagnostic => diagnostic.Code == "AOT2007");

        var malformedRegion = artifact.PeImage.ToArray();
        var safepoint = "\"safepointCount\":1"u8;
        var safepointOffset = malformedRegion.AsSpan().IndexOf(safepoint);
        Assert.True(safepointOffset >= 0);
        malformedRegion[safepointOffset + safepoint.Length - 1] = (byte)'0';
        var malformedRegionValidation = LuaAotArtifactLoader.Validate(
            malformedRegion.ToImmutableArray(),
            artifact.PortablePdbImage);
        Assert.False(malformedRegionValidation.Succeeded);
        Assert.Contains(
            malformedRegionValidation.Diagnostics,
            static diagnostic => diagnostic.Code == "AOT2007");
    }

    [Fact]
    public void ProfileGuidedPersistedAotHonorsInstructionBudgetAtNumericSafepoints()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CompileSource(
            "local total = 0; for i = 1, 10000 do total = total + i end; return total");
        var artifact = LuaAotCompiler.Compile(module, new LuaAotCompilationOptions
        {
            Profile = TrainProfile(module),
        }).Artifact!;
        using var loaded = LuaAotArtifactLoader.Load(artifact).Module!;
        var executor = new LuaPersistedAotExecutor(
            loaded,
            LuaInterpreterOptions.Default with { MaximumInstructionCount = 50 });
        var state = new LuaState();

        Assert.Throws<LuaRuntimeException>(() => executor.Execute(
            state,
            state.CreateMainClosure(module)));
        Assert.True(executor.Statistics.CompiledInvocations > 0);
        Assert.Equal(LuaThreadStatus.Error, state.MainThread.Status);
    }

    [Fact]
    public void PersistedExecutorAttributesExpectedDebugDeoptimizationSeparately()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(13)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;
        using var loaded = LuaAotArtifactLoader.Load(artifact).Module!;
        var executor = new LuaPersistedAotExecutor(loaded);
        var state = new LuaState();
        LuaDebugApi.SetHook(
            state,
            state.MainThread,
            LuaValue.FromFunction(new LuaNativeFunction("hook", static (_, _) => [])),
            LuaDebugHookMask.Count,
            count: 1);

        var result = executor.Execute(state, state.CreateMainClosure(module));

        Assert.Equal(LuaValue.FromInteger(13), Assert.Single(result.Values));
        Assert.True(executor.Statistics.CompiledInvocations > 0);
        Assert.Equal(0, executor.Statistics.InterpreterFallbacks);
        Assert.True(executor.Statistics.Deoptimizations > 0);
        Assert.Equal(
            executor.Statistics.Deoptimizations,
            executor.Statistics.DebugModeDeoptimizations);
        Assert.Equal(0, executor.Statistics.UnexpectedDeoptimizations);
    }

    [Fact]
    public void PersistedExecutorFallsBackForAMismatchedOrDisposedArtifact()
    {
        var compiledModule = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(7)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var fallbackModule = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(19)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var artifact = LuaAotCompiler.Compile(compiledModule).Artifact!;
        var loaded = LuaAotArtifactLoader.Load(artifact).Module!;
        var executor = new LuaPersistedAotExecutor(loaded);
        var state = new LuaState();

        var mismatched = executor.Execute(state, state.CreateMainClosure(fallbackModule));
        loaded.Dispose();
        var disposed = executor.Execute(state, state.CreateMainClosure(compiledModule));

        Assert.Equal(LuaValue.FromInteger(19), Assert.Single(mismatched.Values));
        Assert.Equal(LuaValue.FromInteger(7), Assert.Single(disposed.Values));
        Assert.Equal(0, executor.Statistics.CompiledInvocations);
        Assert.Equal(4, executor.Statistics.InterpreterFallbacks);
        Assert.Equal(0, executor.Statistics.Deoptimizations);
        Assert.Equal(0, executor.Statistics.DebugModeDeoptimizations);
        Assert.Equal(0, executor.Statistics.UnexpectedDeoptimizations);
    }

    [Fact]
    public void ValidatesPersistedArtifactWithoutLoadingDynamicCode()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;

        var validation = LuaAotArtifactLoader.Validate(
            artifact,
            new LuaAotLoadOptions
            {
                ExpectedModuleContentId = artifact.Manifest.ModuleContentId,
            });

        Assert.True(
            validation.Succeeded,
            string.Join("; ", validation.Diagnostics.Select(static item => item.Message)));
        Assert.Equal(artifact.Manifest.ModuleContentId, validation.Manifest!.ModuleContentId);
        Assert.Equal(
            artifact.Manifest.Functions.Select(static function => function.FunctionId),
            validation.Manifest.Functions.Select(static function => function.FunctionId));
    }

    [Fact]
    public void ValidationRequiresPortablePdbDeclaredByManifest()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;

        var validation = LuaAotArtifactLoader.Validate(artifact.PeImage);

        Assert.False(validation.Succeeded);
        Assert.Contains(validation.Diagnostics, static item => item.Code == "AOT2008");
    }

    [Fact]
    public void RejectsCorruptedEmbeddedCanonicalModuleBeforeLoading()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;
        var corrupted = artifact.PeImage.ToArray();
        var moduleOffset = corrupted.AsSpan().IndexOf("LUNILIR\0"u8);
        Assert.True(moduleOffset >= 0);
        corrupted[moduleOffset + 8] ^= 0x01;

        var loading = LuaAotArtifactLoader.Load(corrupted.ToImmutableArray());

        Assert.False(loading.Succeeded);
        Assert.Contains(loading.Diagnostics, diagnostic => diagnostic.Code == "AOT2005");
    }

    [Fact]
    public void RejectsPeCorruptionCoveredByTheArtifactChecksumFooter()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;
        var corrupted = artifact.PeImage.ToArray();
        corrupted[2] ^= 0x01;

        var loading = LuaAotArtifactLoader.Load(corrupted.ToImmutableArray());

        Assert.False(loading.Succeeded);
        Assert.Contains(loading.Diagnostics, diagnostic => diagnostic.Code == "AOT2009");
    }

    [Fact]
    public void PersistedArtifactFaultMatrixRejectsEveryTruncatedBoundary()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;
        var lengths = new[]
        {
            0,
            1,
            artifact.PeImage.Length / 2,
            artifact.PeImage.Length - 1,
        };

        foreach (var length in lengths)
        {
            var validation = LuaAotArtifactLoader.Validate(
                artifact.PeImage.AsSpan(0, length).ToArray().ToImmutableArray(),
                artifact.PortablePdbImage);
            Assert.False(validation.Succeeded);
            Assert.NotEmpty(validation.Diagnostics);
        }
    }

    [Fact]
    public void SplitsLargeFunctionsOnBlockBoundariesAndResumesAcrossShards()
    {
        var module = CreateModule(
            registerCount: 1,
            constants:
            [
                LuaIrConstant.FromBoolean(true),
                LuaIrConstant.FromInteger(7),
                LuaIrConstant.FromInteger(9),
            ],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.JumpIfTrue, a: 0, b: 4),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 1),
            new LuaIrInstruction(LuaIrOpcode.Jump, b: 5, c: -1),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 2),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var artifact = LuaAotCompiler.Compile(module, LuaAotCompilationOptions.Default with
        {
            MaximumCanonicalInstructionsPerMethod = 2,
        }).Artifact!;

        Assert.Equal(3, artifact.Manifest.Functions[0].Shards.Length);
        Assert.All(
            artifact.Manifest.Functions[0].Shards,
            shard => Assert.InRange(shard.InstructionCount, 1, 2));

        using var loaded = LuaAotArtifactLoader.Load(artifact).Module!;
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        LuaCompiledExit exit;
        do
        {
            var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 100);
            exit = loaded.GetFunction(0)(context, thread, frame);
            LuaCodegenAbiV1.CommitProgramCounter(frame, exit.ProgramCounter);
        }
        while (exit.Kind == LuaCompiledExitKind.Continue);

        Assert.Equal(LuaCompiledExitKind.Return, exit.Kind);
        Assert.Equal(LuaValue.FromInteger(9), thread.Stack[0]);
    }

    [Fact]
    public void RejectsIncompatibleRuntimeAbiBeforeAssemblyLoad()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;
        var incompatible = artifact.PeImage.ToArray();
        var version = "\"runtimeAbiVersion\":4"u8;
        var offset = incompatible.AsSpan().IndexOf(version);
        Assert.True(offset >= 0);
        incompatible[offset + version.Length - 1] = (byte)'3';

        var loading = LuaAotArtifactLoader.Load(incompatible.ToImmutableArray());

        Assert.False(loading.Succeeded);
        Assert.Contains(loading.Diagnostics, diagnostic => diagnostic.Code == "AOT2004");
    }

    [Fact]
    public void RejectsPortablePdbThatDoesNotMatchThePeDebugDirectory()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0, sourceLine: 1));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;
        var corruptedPdb = artifact.PortablePdbImage.ToArray();
        corruptedPdb[^1] ^= 0x01;

        var loading = LuaAotArtifactLoader.Load(
            artifact.PeImage,
            corruptedPdb.ToImmutableArray());

        Assert.False(loading.Succeeded);
        Assert.Contains(loading.Diagnostics, diagnostic => diagnostic.Code == "AOT2008");
    }

    [Fact]
    public void EnforcesPersistedMethodBodyAndMetadataLimits()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.LoadNil, a: 0, b: 1),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var bodyLimited = LuaAotCompiler.Compile(module, LuaAotCompilationOptions.Default with
        {
            MaximumMethodBodyBytes = 1,
        });
        var metadataLimited = LuaAotCompiler.Compile(module, LuaAotCompilationOptions.Default with
        {
            MaximumMetadataTokens = 1,
        });
        var branchLimited = LuaAotCompiler.Compile(module, LuaAotCompilationOptions.Default with
        {
            MaximumBranchInstructionsPerMethod = 1,
        });

        Assert.Contains(bodyLimited.Diagnostics, diagnostic => diagnostic.Code == "CIL0028");
        Assert.Contains(metadataLimited.Diagnostics, diagnostic => diagnostic.Code == "CIL0029");
        Assert.Contains(branchLimited.Diagnostics, diagnostic => diagnostic.Code == "CIL0026");
    }

    [Fact]
    public void CollectibleArtifactContextCanUnloadAfterRegistrationIsDisposed()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var artifact = LuaAotCompiler.Compile(module).Artifact!;

        var weakReference = LoadAndRelease(artifact);
        for (var attempt = 0; attempt < 10 && weakReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weakReference.IsAlive);
    }

    [Fact]
    public void RefusesUnverifiedCanonicalModulesBeforePlanning()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0)) with
        {
            FormatVersion = int.MaxValue,
        };

        var result = LuaCilCodeGenerator.PlanFunction(module, 0);

        Assert.False(result.Succeeded);
        Assert.Null(result.Plan);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL1001");
    }

    [Fact]
    public void ReusesTheOwnerScopedPlanAndClearsCacheHitMetrics()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));

        var first = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);
        var cached = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);

        Assert.True(first.Succeeded);
        Assert.Same(first.Plan, cached.Plan);
        Assert.Equal(default, cached.Metrics);
    }

    [Fact]
    public void CanceledPlanningDoesNotPopulateTheOwnerScopedPlanCache()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(() => LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false,
            cancellationToken: cancellation.Token));

        var first = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);
        var cached = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);

        Assert.True(first.Succeeded);
        Assert.NotEqual(default, first.Metrics);
        Assert.Same(first.Plan, cached.Plan);
        Assert.Equal(default, cached.Metrics);
    }

    [Fact]
    public async Task ConcurrentFirstUseBuildsTheOwnerScopedPlanOnlyOnce()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        using var start = new ManualResetEventSlim();
        var tasks = Enumerable.Range(0, 32)
            .Select(_ => Task.Run(() =>
            {
                start.Wait();
                return LuaCilCodeGenerator.PlanFunction(
                    module,
                    0,
                    includeInstructionObservation: false);
            }))
            .ToArray();

        start.Set();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, static result => Assert.True(result.Succeeded));
        Assert.All(results, result => Assert.Same(results[0].Plan, result.Plan));
    }

    [Fact]
    public void OwnerScopedPlanCacheDoesNotKeepTheModuleAlive()
    {
        var moduleReference = CreateCachedModuleWeakReference();

        for (var attempt = 0; attempt < 10 && moduleReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(moduleReference.IsAlive);
    }

    [Fact]
    public void ComputesRegisterLivenessAndSafePointMaps()
    {
        var module = CreateModule(
            registerCount: 2,
            constants: [LuaIrConstant.FromString("value"u8)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Move, a: 1, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 1, b: 1));

        var result = LuaRegisterLiveness.Analyze(module, module.Functions[0]);

        Assert.Empty(result.LiveBefore[0]);
        Assert.True(result.LiveBefore[1].SequenceEqual([0]));
        Assert.True(result.LiveBefore[2].SequenceEqual([1]));
        Assert.True(result.GcMaps
            .Select(static map => map.CanonicalProgramCounter)
            .SequenceEqual([0, 2]));
    }

    [Fact]
    public void OwnerScopedLivenessCacheReusesAnalysisWithoutKeepingModuleAlive()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));

        var first = LuaRegisterLiveness.AnalyzeCached(
            module,
            module.Functions[0],
            out var firstHit);
        var second = LuaRegisterLiveness.AnalyzeCached(
            module,
            module.Functions[0],
            out var secondHit);

        Assert.False(firstHit);
        Assert.True(secondHit);
        Assert.Same(first, second);

        var moduleReference = CreateLivenessCachedModuleWeakReference();
        for (var attempt = 0; attempt < 10 && moduleReference.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(moduleReference.IsAlive);
    }

    [Fact]
    public void RejectsEvaluationStackUnderflow()
    {
        var plan = MinimalPlan(
            CilPlanInstruction.Simple(CilPlanOpCode.Return));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0020");
    }

    [Fact]
    public void RejectsIncompatibleMergeStacks()
    {
        var merge = new CilLabel(1);
        var plan = MinimalPlan(
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 1),
            CilPlanInstruction.WithLabel(CilPlanOpCode.BranchTrue, merge),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 2),
            CilPlanInstruction.MarkLabel(merge),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.Call(CilWellKnownCalls.ExitReturn),
            CilPlanInstruction.Simple(CilPlanOpCode.Return));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0004");
    }

    [Fact]
    public void RejectsReachableUndefinedBranchTargets()
    {
        var plan = MinimalPlan(
            CilPlanInstruction.WithLabel(CilPlanOpCode.Branch, new CilLabel(999)));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0019");
    }

    [Fact]
    public void RequiresGcMapsAndSpilledValuesAtSafePoints()
    {
        var target = new CilCallTarget(
            "safe",
            [CilStackValueKind.Int32],
            CilStackValueKind.Void,
            IsGcSafePoint: true);
        var plan = MinimalPlan(
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 1),
            CilPlanInstruction.Call(target, canonicalProgramCounter: 3),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.Call(CilWellKnownCalls.ExitReturn),
            CilPlanInstruction.Simple(CilPlanOpCode.Return));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0016");
    }

    [Fact]
    public void RejectsUnspilledLuaValuesAcrossSafePoints()
    {
        var target = new CilCallTarget(
            "safe",
            [CilStackValueKind.Int32],
            CilStackValueKind.Void,
            IsGcSafePoint: true);
        var plan = MinimalPlan(
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadArgument, 0),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 1),
            CilPlanInstruction.Call(target, canonicalProgramCounter: 3),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.Call(CilWellKnownCalls.ExitReturn),
            CilPlanInstruction.Simple(CilPlanOpCode.Return)) with
        {
            ParameterKinds = [CilStackValueKind.LuaValue],
            GcMaps = [new CilGcMap(3, [0])],
        };

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0015");
    }

    [Fact]
    public void EnforcesPlanSizeLimitsBeforeGraphTraversal()
    {
        var plan = MinimalPlan(
            CilPlanInstruction.Simple(CilPlanOpCode.Nop),
            CilPlanInstruction.Simple(CilPlanOpCode.Nop));

        var result = CilMethodPlanVerifier.Verify(plan, CilPlanLimits.Default with
        {
            MaximumInstructions = 1,
        });

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0001");
    }

    [Fact]
    public void RejectsOversizedCanonicalFunctionsBeforePlanning()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.LoadNil, a: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));

        var result = LuaCilCodeGenerator.PlanFunction(module, 0, CilPlanLimits.Default with
        {
            MaximumInstructions = 1,
        });

        Assert.False(result.Succeeded);
        Assert.Null(result.Plan);
        Assert.Null(result.Verification);
        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0001");
    }

    [Fact]
    public void RejectsTamperedRuntimeAbiSignatures()
    {
        var tampered = CilWellKnownCalls.ExitReturn with
        {
            ParameterKinds = [CilStackValueKind.Int32],
        };
        var plan = MinimalPlan(
            CilPlanInstruction.WithInt32(CilPlanOpCode.LoadInt32, 0),
            CilPlanInstruction.Call(tampered),
            CilPlanInstruction.Simple(CilPlanOpCode.Return));

        var result = CilMethodPlanVerifier.Verify(plan);

        Assert.Contains(result.Diagnostics, diagnostic => diagnostic.Code == "CIL0024");
    }

    [Fact]
    public void BothEmitterFlavorsConsumeTheSameVerifiedPlan()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(1)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var plan = LuaCilCodeGenerator.PlanFunction(module, 0).Plan!;
        var reflection = new RecordingSink(CilEmitterFlavor.ReflectionEmit);
        var metadata = new RecordingSink(CilEmitterFlavor.Metadata);

        var reflectionResult = CilPlanEmitter.Emit(plan, reflection);
        var metadataResult = CilPlanEmitter.Emit(plan, metadata);

        Assert.True(reflectionResult.Succeeded);
        Assert.True(metadataResult.Succeeded);
        Assert.Equal(reflection.Instructions, metadata.Instructions);
        Assert.Equal(reflection.MaximumStack, metadata.MaximumStack);
    }

    [Fact]
    public void EmissionCancellationDoesNotFinalizeTheInstructionSink()
    {
        var canonicalInstructions = Enumerable.Repeat(
                new LuaIrInstruction(LuaIrOpcode.Move, a: 0, b: 0),
                16)
            .Append(new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0))
            .ToArray();
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            canonicalInstructions);
        var planning = LuaCilCodeGenerator.PlanFunction(module, 0);
        Assert.True(planning.Succeeded);
        Assert.True(planning.Plan!.Instructions.Length > 64);
        using var cancellation = new CancellationTokenSource();
        var sink = new CancelingSink(cancellation);

        Assert.Throws<OperationCanceledException>(() => CilPlanEmitter.EmitVerified(
            planning.Plan,
            sink,
            planning.Verification!,
            cancellation.Token));

        Assert.False(sink.Finalized);
    }

    [Fact]
    public void ReflectionEmitterExecutesThePlannedSubsetAgainstRuntimeAbiV1()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateModule(
            registerCount: 2,
            constants: [LuaIrConstant.FromInteger(42)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Move, a: 1, b: 0),
            new LuaIrInstruction(LuaIrOpcode.JumpIfFalse, a: 1, b: 4),
            new LuaIrInstruction(LuaIrOpcode.SetTop, a: 2),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var plan = LuaCilCodeGenerator.PlanFunction(module, 0).Plan!;
        var emission = ReflectionEmitCilPlanSink.Compile(plan);
        var referenceState = new LuaState();
        var reference = new LuaInterpreter().Execute(
            referenceState,
            referenceState.CreateMainClosure(module));
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 100);

        var exit = emission.Method!(context, thread, frame);

        Assert.True(emission.Succeeded, string.Join("; ", emission.Diagnostics.Select(static d => d.Message)));
        Assert.Equal(
            plan.CanonicalInstructionCount,
            plan.DirectCanonicalInstructionCount + plan.SlowPathCanonicalInstructionCount);
        Assert.Equal(plan.CanonicalInstructionCount, plan.DirectCanonicalInstructionCount);
        Assert.True(emission.Metrics.PlanVerificationDuration >= TimeSpan.Zero);
        Assert.True(emission.Metrics.EmissionDuration >= TimeSpan.Zero);
        Assert.True(emission.Metrics.DelegateCreationDuration >= TimeSpan.Zero);
        Assert.Equal(LuaCompiledExitKind.Return, exit.Kind);
        Assert.Equal(4, exit.ProgramCounter);
        Assert.Equal(5, exit.InstructionsConsumed);
        Assert.Equal(LuaValue.FromInteger(42), thread.Stack[0]);
        Assert.Equal(LuaValue.FromInteger(42), thread.Stack[1]);
        Assert.True(reference.Values.SequenceEqual([thread.Stack[0]]));
    }

    [Fact]
    public void MetadataRecipeIsDeterministicAndPreservesTheVerifiedPlan()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(1)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var plan = LuaCilCodeGenerator.PlanFunction(module, 0).Plan!;

        var first = MetadataCilPlanSink.CreateRecipe(plan);
        var second = MetadataCilPlanSink.CreateRecipe(plan);

        Assert.True(first.Verification.Succeeded);
        Assert.Equal(first.Recipe!.MethodName, second.Recipe!.MethodName);
        Assert.Equal(first.Recipe.MaximumEvaluationStack, second.Recipe.MaximumEvaluationStack);
        Assert.True(first.Recipe.Locals.SequenceEqual(second.Recipe.Locals));
        Assert.True(first.Recipe.Instructions.SequenceEqual(second.Recipe.Instructions));
        Assert.True(first.Recipe!.Instructions.SequenceEqual(plan.Instructions));
    }

    [Fact]
    public void ReflectionEmitterReturnsBudgetPollBeforeExecutingACanonicalInstruction()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateModule(
            registerCount: 1,
            constants: [LuaIrConstant.FromInteger(1)],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 1));
        var method = ReflectionEmitCilPlanSink.Compile(
            LuaCilCodeGenerator.PlanFunction(module, 0).Plan!).Method!;
        var state = new LuaState();
        var thread = state.MainThread;
        var frame = new LuaFrame(
            state.CreateMainClosure(module),
            @base: 0,
            top: 0,
            returnBase: 0,
            expectedResults: 0,
            varArgs: []);
        var context = new LuaExecutionContext(state, thread, remainingInstructionCount: 0);

        var exit = method(context, thread, frame);

        Assert.Equal(LuaCompiledExitKind.Poll, exit.Kind);
        Assert.Equal(LuaCompiledExitReason.InstructionBudget, exit.Reason);
        Assert.Equal(0, exit.ProgramCounter);
        Assert.Equal(0, exit.InstructionsConsumed);
        Assert.Equal(LuaValue.Nil, thread.Stack[0]);
    }

    [Fact]
    public void AbiV2DirectSegmentStopsAtEveryExactBudgetBoundary()
    {
        if (!RuntimeFeature.IsDynamicCodeSupported)
        {
            return;
        }

        var module = CreateModule(
            registerCount: 5,
            constants:
            [
                LuaIrConstant.FromInteger(5),
                LuaIrConstant.FromInteger(3),
            ],
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 0, b: 0),
            new LuaIrInstruction(
                LuaIrOpcode.Unary,
                a: 1,
                b: 0,
                c: (int)LuaIrUnaryOperator.Negate),
            new LuaIrInstruction(LuaIrOpcode.LoadConstant, a: 2, b: 1),
            new LuaIrInstruction(
                LuaIrOpcode.Binary,
                a: 3,
                b: 1,
                c: 2,
                d: (int)LuaIrBinaryOperator.Add),
            new LuaIrInstruction(LuaIrOpcode.Move, a: 4, b: 3),
            new LuaIrInstruction(LuaIrOpcode.Return, a: 4, b: 1));
        var method = ReflectionEmitCilPlanSink.Compile(
            LuaCilCodeGenerator.PlanFunction(
                module,
                0,
                includeInstructionObservation: false).Plan!).Method!;

        for (var remaining = 0; remaining <= 6; remaining++)
        {
            var state = new LuaState();
            var thread = state.MainThread;
            var frame = new LuaFrame(
                state.CreateMainClosure(module),
                @base: 0,
                top: 0,
                returnBase: 0,
                expectedResults: 0,
                varArgs: []);
            var context = new LuaExecutionContext(
                state,
                thread,
                remainingInstructionCount: remaining);

            var exit = method(context, thread, frame);

            if (remaining < 6)
            {
                Assert.Equal(LuaCompiledExitKind.Poll, exit.Kind);
                Assert.Equal(LuaCompiledExitReason.InstructionBudget, exit.Reason);
                Assert.Equal(remaining, exit.ProgramCounter);
                Assert.Equal(remaining, exit.InstructionsConsumed);
                Assert.Equal(remaining, frame.ProgramCounter);
            }
            else
            {
                Assert.Equal(LuaCompiledExitKind.Return, exit.Kind);
                Assert.Equal(5, exit.ProgramCounter);
                Assert.Equal(6, exit.InstructionsConsumed);
                Assert.Equal(LuaValue.FromInteger(-2), thread.Stack[4]);
            }
        }
    }

    [Fact]
    public void MalformedPlanFuzzingNeverEscapesTheVerifier()
    {
        var random = new Random(1);
        var opcodes = Enum.GetValues<CilPlanOpCode>();
        for (var pass = 0; pass < 250; pass++)
        {
            var instructions = Enumerable.Range(0, random.Next(1, 80))
                .Select(_ => CilPlanInstruction.WithInt32(
                    opcodes[random.Next(opcodes.Length)],
                    random.Next(-5, 20)))
                .ToImmutableArray();
            var plan = MinimalPlan(instructions.ToArray());

            var exception = Record.Exception(() => CilMethodPlanVerifier.Verify(plan));

            Assert.Null(exception);
        }
    }

    private static LuaJitModuleProfile TrainProfile(
        LuaIrModule module,
        params LuaValue[] arguments)
    {
        using var executor = new LuaJitExecutor(LuaJitExecutorOptions.Default with
        {
            FunctionEntryThreshold = 1,
            BackedgeThreshold = 1,
            EnableLoopOsr = false,
            Tier2InvocationThreshold = int.MaxValue,
            Tier2BackedgeThreshold = int.MaxValue,
            SynchronousCompilation = true,
        });
        var state = new LuaState();
        var closure = state.CreateMainClosure(module);
        _ = executor.Execute(state, closure, arguments);
        _ = executor.Execute(state, closure, arguments);
        return LuaJitProfileCodec.Deserialize(module, executor.ExportProfile(module));
    }

    private static LuaIrModule CreateModule(
        int registerCount,
        ImmutableArray<LuaIrConstant> constants,
        params LuaIrInstruction[] instructions)
    {
        var immutableInstructions = instructions.ToImmutableArray();
        return new LuaIrModule
        {
            MainFunctionId = 0,
            Functions =
            [
                new LuaIrFunction
                {
                    Id = 0,
                    Span = new TextSpan(0, 0),
                    RegisterCount = registerCount,
                    Constants = constants,
                    Instructions = immutableInstructions,
                    BasicBlocks = LuaIrControlFlow.Build(immutableInstructions),
                },
            ],
        };
    }

    private static LuaIrModule CompileSource(string source)
    {
        var parsing = LuaParser.Parse(SourceText.FromUtf8(source));
        var binding = LuaBinder.Bind(parsing);
        var lowering = LuaLowerer.Lower(binding);
        Assert.True(
            lowering.Succeeded,
            string.Join("; ", lowering.Diagnostics.Select(static diagnostic => diagnostic.Message)));
        return Assert.IsType<LuaIrModule>(lowering.Module);
    }

    private static CilMethodPlan MinimalPlan(params CilPlanInstruction[] instructions) => new()
    {
        Name = "test",
        FunctionId = 0,
        ReturnKind = CilStackValueKind.CompiledExit,
        Instructions = instructions.ToImmutableArray(),
    };

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadAndRelease(LuaAotArtifact artifact)
    {
        var loaded = LuaAotArtifactLoader.Load(artifact).Module!;
        var weakReference = loaded.LoadContextWeakReference;
        loaded.Dispose();
        return weakReference;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateCachedModuleWeakReference()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        var result = LuaCilCodeGenerator.PlanFunction(
            module,
            0,
            includeInstructionObservation: false);
        Assert.True(result.Succeeded);
        return new WeakReference(module);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateLivenessCachedModuleWeakReference()
    {
        var module = CreateModule(
            registerCount: 1,
            constants: [],
            new LuaIrInstruction(LuaIrOpcode.Return, a: 0, b: 0));
        _ = LuaRegisterLiveness.AnalyzeCached(
            module,
            module.Functions[0],
            out _);
        return new WeakReference(module);
    }

    private sealed class RecordingSink : ICilInstructionSink
    {
        public RecordingSink(CilEmitterFlavor flavor)
        {
            Flavor = flavor;
        }

        public CilEmitterFlavor Flavor { get; }

        public List<CilPlanInstruction> Instructions { get; } = [];

        public int MaximumStack { get; private set; }

        public void BeginMethod(CilMethodPlan plan, int maximumEvaluationStack)
        {
            MaximumStack = maximumEvaluationStack;
        }

        public void DeclareLocal(CilLocal local)
        {
        }

        public void Emit(CilPlanInstruction instruction) => Instructions.Add(instruction);

        public void EndMethod()
        {
        }
    }

    private sealed class CancelingSink(CancellationTokenSource cancellation) :
        ICilInstructionSink
    {
        private int _emitted;

        public CilEmitterFlavor Flavor => CilEmitterFlavor.ReflectionEmit;

        public bool Finalized { get; private set; }

        public void BeginMethod(CilMethodPlan plan, int maximumEvaluationStack)
        {
        }

        public void DeclareLocal(CilLocal local)
        {
        }

        public void Emit(CilPlanInstruction instruction)
        {
            if (Interlocked.Increment(ref _emitted) == 1)
            {
                cancellation.Cancel();
            }
        }

        public void EndMethod() => Finalized = true;
    }
}
