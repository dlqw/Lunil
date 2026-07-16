using System.Runtime.CompilerServices;
using Lunil.CodeGen.Cil.Loading;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.CodeGen;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;

namespace Lunil.CodeGen.Cil;

/// <summary>
/// Executes canonical Lua modules through a validated, dynamically loaded persisted CIL artifact.
/// The caller owns the loaded module and controls its collectible load-context lifetime.
/// </summary>
public sealed class LuaPersistedAotExecutor
{
    private readonly LuaExecutionEngine _engine;
    private readonly PersistedInstructionExecutor _instructionExecutor;

    public LuaPersistedAotExecutor(
        LuaAotLoadedModule loadedModule,
        LuaInterpreterOptions? interpreterOptions = null)
    {
        ArgumentNullException.ThrowIfNull(loadedModule);
        LoadedModule = loadedModule;
        InterpreterOptions = interpreterOptions ?? LuaInterpreterOptions.Default;
        _instructionExecutor = new PersistedInstructionExecutor(loadedModule);
        _engine = new LuaExecutionEngine(
            InterpreterOptions,
            _instructionExecutor);
    }

    public LuaAotLoadedModule LoadedModule { get; }

    public LuaInterpreterOptions InterpreterOptions { get; }

    public LuaPersistedAotStatistics Statistics => _instructionExecutor.GetStatistics();

    public LuaExecutionResult Execute(
        LuaState state,
        LuaClosure closure,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Execute(state, closure, arguments);

    public LuaExecutionResult Start(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Start(state, thread, arguments);

    public LuaExecutionResult Resume(
        LuaState state,
        LuaThread thread,
        ReadOnlySpan<LuaValue> arguments = default) =>
        _engine.Resume(state, thread, arguments);

    public LuaExecutionResult Close(LuaState state, LuaThread thread) =>
        _engine.Close(state, thread);

    private sealed class PersistedInstructionExecutor(
        LuaAotLoadedModule loadedModule) : ILuaInstructionExecutor
    {
        private readonly ConditionalWeakTable<LuaIrModule, ModuleIdentityMatch> _moduleMatches =
            new();
        private LuaIrModule? _fastModule;

        public LuaCompiledExit Execute(
            LuaExecutionEngine engine,
            LuaExecutionContext context,
            LuaState state,
            LuaThread thread,
            LuaFrame frame,
            LuaIrInstruction instruction)
        {
            if (MatchesArtifact(frame.Module) &&
                loadedModule.TryGetFunction(frame.Function.Id, out var function) &&
                function is not null)
            {
                Interlocked.Increment(ref _compiledInvocations);
                var exit = function(context, thread, frame);
                if (exit.Kind == LuaCompiledExitKind.Deopt)
                {
                    Interlocked.Increment(ref _deoptimizations);
                    if (exit.Reason == LuaCompiledExitReason.DebugModeChanged)
                    {
                        Interlocked.Increment(ref _debugModeDeoptimizations);
                    }
                    else
                    {
                        Interlocked.Increment(ref _unexpectedDeoptimizations);
                    }
                }

                return exit;
            }

            Interlocked.Increment(ref _interpreterFallbacks);
            return LuaCompiledExit.Deopt(
                frame.ProgramCounter,
                instructionsConsumed: 0,
                LuaCompiledExitReason.UnsupportedInstruction);
        }

        private bool MatchesArtifact(LuaIrModule module)
        {
            if (ReferenceEquals(Volatile.Read(ref _fastModule), module))
            {
                return true;
            }

            var matches = _moduleMatches.GetValue(module, CreateIdentityMatch).Matches;
            if (matches)
            {
                Volatile.Write(ref _fastModule, module);
            }

            return matches;
        }

        private ModuleIdentityMatch CreateIdentityMatch(LuaIrModule module) => new(
            string.Equals(
                LuaAotModuleIdentity.ComputeContentId(module),
                loadedModule.Manifest.ModuleContentId,
                StringComparison.Ordinal));

        private long _compiledInvocations;
        private long _interpreterFallbacks;
        private long _deoptimizations;
        private long _debugModeDeoptimizations;
        private long _unexpectedDeoptimizations;

        public LuaPersistedAotStatistics GetStatistics() => new(
            Interlocked.Read(ref _compiledInvocations),
            Interlocked.Read(ref _interpreterFallbacks),
            Interlocked.Read(ref _deoptimizations),
            Interlocked.Read(ref _debugModeDeoptimizations),
            Interlocked.Read(ref _unexpectedDeoptimizations));

        private sealed record ModuleIdentityMatch(bool Matches);
    }
}

public sealed record LuaPersistedAotStatistics(
    long CompiledInvocations,
    long InterpreterFallbacks,
    long Deoptimizations,
    long DebugModeDeoptimizations,
    long UnexpectedDeoptimizations);
