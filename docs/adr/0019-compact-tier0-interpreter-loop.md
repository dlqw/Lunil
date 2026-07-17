# ADR 0019: Compact Tier 0 interpreter loop

- Status: Accepted
- Date: 2026-07-18
- Target: `0.8.0-alpha.13`, Lua 5.4.8, .NET 10
- Related: [ADR 0001](0001-execution-backend-abi-v1.md),
  [ADR 0013](0013-64-bit-instruction-accounting.md),
  [ADR 0018](0018-remove-lua-aot.md)

## Context

The reference interpreter previously returned to `LuaExecutionEngine.RunScheduler` after every
canonical instruction. Each instruction therefore repeated context reset, route selection,
interface dispatch, exit validation, program-counter commit, checked instruction accumulation,
debug-state tests, heap safe points, and finalizer scans. It also copied the roughly 40-byte
`LuaIrInstruction` value across the scheduler/executor boundary. Sampling and cross-runtime
measurements showed this fixed bookkeeping, rather than `LuaValue` representation, dominated
numeric and control-flow Tier 0 work.

Duplicating the scheduler inside a second interpreter, reserving a large instruction-budget block,
or skipping debug/GC boundaries would improve a benchmark while creating a second semantic kernel.
The optimization must instead retain the existing scheduler, one opcode implementation, exact
budget accounting, and deterministic backend handoff.

## Decision

Starting in `0.8.0-alpha.13`:

1. `LuaInterpreterInstructionExecutor.Execute` may continue canonical instructions inside the same
   frame while the previous exit is `Continue` and no scheduler transfer, forced result, pending
   error, unwind, close, continuation, backend entry, or debug-version change exists.
2. `ExecuteSingleInstruction` remains the only opcode switch. Single-step hook execution, compact
   execution, compiled slow paths, and deoptimization all reuse it rather than copying semantics.
   Pure compact instructions return a byte-sized internal result and do not materialize a
   `LuaCompiledExit`; the exit is created only at a scheduler boundary.
3. Call, tail call, return, instruction-budget poll, and every non-continue exit return to the shared
   scheduler. The execution context charges one unit before each instruction and reports the exact
   accumulated `long` count on every exit.
4. A dispatchable hook on an ordinary visible frame disables compact execution. Hook and hidden
   frames may remain compact because recursive hook dispatch is prohibited. `LuaThread` maintains a
   derived dispatchable-hook state whenever the hook, mask, or running-hook state changes.
5. `InterpreterWithBackedgeProbes` exits before the next backedge so Tier 1/OSR observation and
   publication cannot be swallowed. Backend execution retains the existing compiled-exit ABI.
6. Ordinary compact execution advances the logical heap and drains finalizers at least every 32
   instructions. Every-allocation stress after an allocation and any pending finalizer force an
   immediate interpreter safe point. Every compact-loop exit also reaches the scheduler safe point.
7. The immutable instruction vector is accessed through its supported backing-array marshal and
   passed by readonly reference. The canonical IR storage contract remains immutable and no
   `unsafe` code is introduced.
8. Frame routing, backedge commits, instruction observation, and Loop OSR observation become
   optional members of the existing internal executor boundary. This removes three nullable
   single-implementation interface fields while keeping custom/test executors compatible through
   default no-op behavior.
9. After one full continuation-state validation, a consecutive chain of instructions proven not to
   schedule Lua work reuses that validation and checks only the next-PC bound. Table/metamethod
   operations, close scheduling, safe points, hooks, and probe routes always restore the full check.

## Consequences

- Straight-line and loop Tier 0 work pays scheduler bookkeeping once per compact batch or semantic
  boundary instead of once per instruction.
- Debug hooks, exact budget failures, coroutine/protected-call transfers, close/error unwind, GC
  stress, Lua finalizers, JIT profiling, and OSR publication continue to use their existing oracles.
- GC/finalizer latency for ordinary non-allocating Tier 0 work is bounded by 32 instructions rather
  than one instruction; stress mode and pending work retain immediate servicing.
- Runtime cannot directly cache the CodeGen.Cil registry as a concrete type because that would
  create a project-reference cycle. Consolidating optional behavior on the existing executor is the
  concrete fast boundary available without reversing package dependencies.

## Rejected alternatives

- **Copy the opcode switch into `RunScheduler`:** rejected because fixes could diverge between two
  semantic kernels and the scheduler would grow around another complete interpreter.
- **Reserve budget in blocks:** rejected because an error, hook, call, or return inside a partially
  consumed block would need rollback and could move the exact failure PC.
- **Run compactly while hooks are armed:** rejected because line/count/call/return ordering is
  observable and must not be approximated.
- **Only service GC at calls/returns:** rejected because long non-calling loops would make logical GC
  and finalizer latency unbounded.
- **Replace immutable IR storage or use `unsafe`:** rejected because supported marshal/ref access
  removes the hot copy without weakening ownership or portability.
