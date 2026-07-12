# ADR 0001: Execution kernel and code-generation ABI v1

- Status: Accepted
- Date: 2026-07-12
- Target: Lua 5.4.8, .NET 10
- Supersedes: the provisional `VmSignal Execute(LuaThread, ref LuaFrame)` signature in
  `docs/compiler-design.md`

## Context

Lunil's interpreter already implements the complete resumable Lua execution model. A Lua
activation is represented by `LuaThread`, `LuaFrame`, and tagged continuations rather than by
the CLR call stack. Calls, coroutine transfers, protected calls, close handlers, debug hooks,
and logical garbage collection all meet in the interpreter's scheduler.

The CoreCLR JIT, persisted CIL, and NativeAOT backends must execute the same canonical IR
without creating parallel implementations of those semantics. The old provisional entry
signature does not carry the current state, instruction budget, hook/debug mode, GC poll, or
deoptimization state. `LuaFrame` is also a reference type, so passing it by `ref` does not make
the register window or frame state more explicit.

## Decision

### One scheduler, multiple block executors

The scheduler remains the only component that may:

- push, replace, or pop Lua frames;
- transfer between coroutines;
- enter or resume native callbacks;
- establish protected boundaries and perform error/close unwinding;
- dispatch debug hooks and logical finalizers;
- materialize values at the public host boundary.

The reference interpreter and compiled backends are block executors beneath this scheduler.
A block executor may run several canonical instructions, but it must return before performing
an operation owned by the scheduler.

The conceptual compiled entry ABI is:

```csharp
LuaCompiledExit Execute(
    LuaExecutionContext context,
    LuaThread thread,
    LuaFrame frame);
```

`LuaExecutionContext` supplies the `LuaState`, the current activation's remaining instruction
budget, hook/debug epochs, GC polling state, and backend policy. `LuaCompiledExit` contains an
exit kind and canonical integer state such as the continuation PC and call/result windows. It
does not contain an untraced Lua object graph.

The concrete ABI is exposed through a versioned `Lunil.Runtime.CodeGen` facade because a
persisted CIL assembly is a separate assembly. The facade is marked as advanced infrastructure
API and versioned independently from normal hosting API. Code generators may not bypass it to
reach mutable Runtime internals.

The v1 exit kinds are:

- `Continue`: resume dispatch at the committed frame PC;
- `Poll`: return for budget, hook, debug, GC, finalizer, or tiering work;
- `Call`: ask the scheduler to resolve and enter a non-tail call;
- `TailCall`: ask the scheduler to replace or complete the current frame according to close and
  hook rules;
- `Return`: ask the scheduler to close and return the current result window;
- `Deopt`: continue in the canonical interpreter at the supplied PC.

Lua failures continue to use the existing Lua error value and `LuaRuntimeException` boundary.
A code-generation failure is reported separately and may cause an interpreter fallback; it is
never presented as a Lua program error.

### Canonical PC commit rule

`LuaFrame.ProgramCounter` is the restart point and is always observable in canonical IR
coordinates.

1. Before an instruction has externally visible effects, the frame PC identifies that
   instruction.
2. A completed fall-through instruction commits `PC + 1`.
3. A completed branch commits its selected canonical target.
4. An instruction that requests a Lua/native call keeps enough tagged continuation state to
   ensure the operation is not repeated when the call resumes. The scheduler owns the final PC
   transition.
5. A throwing instruction leaves a restart/unwind PC that identifies the source instruction.
6. Deoptimization is permitted only after all completed effects have been committed and before
   any uncommitted effect begins.

Generated code must not perform a guard after a non-idempotent effect unless its deoptimization
map resumes after that effect.

### Exact instruction accounting

One executed canonical instruction consumes one instruction-budget unit, independent of the
backend and independent of the amount of generated CIL. Synthetic CIL, guards, polls, and
runtime helpers do not consume additional Lua instruction units.

A compiled block may reserve its straight-line instruction count in one operation only when the
remaining budget is at least that count and no instruction inside the block is an earlier exit.
Otherwise it returns `Poll`, and the interpreter executes from the current canonical PC. This
preserves the exact failure point of `MaximumInstructionCount`. Every backedge and every block
entry reached through OSR is a mandatory budget poll.

### Logical GC and safe points

CLR GC liveness is not Lua logical-GC liveness. At each safe point:

- every live collectable `LuaValue` is stored in the thread stack, a frame/root continuation,
  or an explicitly registered owner-aware cache;
- no live Lua value exists only in a static field, delegate target, profile record, or transient
  CLR closure;
- register stores use owner validation and the logical GC write barrier;
- `frame.Top`, the committed PC, open-result windows, and continuation tags describe the roots
  that the Runtime must traverse.

The plan verifier rejects a generated safe point whose live-register map has not been spilled.
Allocating, calling, yielding, throwing, closing, polling, and any helper that may advance the
logical collector are safe points. A primitive fast path may omit a poll only when its effects
prove that it neither allocates nor calls Runtime code that can collect.

### Hooks and debug state

Exact hook/debug mode is a semantic mode rather than a best-effort diagnostic feature.

- Compiled code guards a Runtime hook/debug epoch at entry and mandatory polls.
- Line/count/call/return/tail-call events are delivered by the shared scheduler using canonical
  PCs and source lines.
- If the active mode requires an event inside a compiled block, the block ends before that
  instruction or deoptimizes to the interpreter.
- Writes to locals/upvalues/metatables and changes to hook configuration invalidate an affected
  specialization through epochs or version guards.
- Portable PDB sequence points aid CLR tooling but never replace Lua hook or traceback data.

### Artifact identity

Persisted CIL, cached IR, and profile artifacts use independent schemas. A persisted code
artifact identity includes at least:

- artifact format version;
- canonical IR format version;
- Runtime code-generation ABI version;
- Lunil/code-generator version;
- module content SHA-256 and static dependency hashes;
- optimization, debug, hook, sandbox, and standard-library feature flags;
- target framework; NativeAOT build products additionally include RID, trimming, and AOT flags;
- payload lengths and checksums.

Source names and traceback bindings are not silently shared merely because code bytes match.
An absent, corrupt, or incompatible artifact causes recompilation or interpretation and cannot
change Lua semantics. CoreCLR machine code is never persisted.

## Canonical opcode contract

The following table freezes the first code-generation treatment. "Boundary" means that a
compiled executor must use the shared Runtime helper or return to the scheduler. Every row is
still charged as exactly one canonical instruction.

Effects use `A` = may allocate, `C` = may call, `Y` = may yield, `T` = may throw,
and `S` = logical-GC safe point. They are conservative guarantees used by code generation; an
immediate fast path may do less work. A plain `Jump` with `C < 0` is the one operand-sensitive
exception and has no effects.

| Opcode | Tier 1 / persisted CIL v1 fast/generic path | Effects | Exit, exception, and PC contract |
|---|---|---:|---|
| `LoadConstant` | Materialize through ABI, then store | A/T/S | Store then commit `PC+1`; allocation/quota failure identifies current PC |
| `LoadNil` | Clear the verified destination range | none | Commit `PC+1`; no allocation |
| `Move` | Load/store canonical register | none | Commit `PC+1`; owner/barrier failure identifies current PC |
| `SetTop` | Clear truncated slots and set frame top | none | Commit `PC+1`; expanded slots are logically nil |
| `GetUpvalue` | ABI read followed by register store | none | Commit `PC+1`; preserve real cell identity |
| `SetUpvalue` | ABI cell write | none | Commit `PC+1`; owner validation and barrier are mandatory |
| `NewTable` | Runtime allocation helper | A/T/S | Store rooted result then commit `PC+1`; quota failure identifies current PC |
| `GetTable` | Generic helper; later guarded PIC | A/C/Y/T/S | Immediate result commits `PC+1`; metamethod exits `Call`; error/current PC is preserved |
| `SetTable` | Generic helper; later guarded PIC | A/C/Y/T/S | Immediate write commits `PC+1`; metamethod exits `Call`; resumed write is not repeated |
| `SetList` | ABI range write | A/T/S | Commit `PC+1`; open range uses frame top; quota/error preserves current PC |
| `Closure` | Runtime closure/upvalue-capture helper | A/T/S | Root closure then commit `PC+1`; capture/allocation error preserves current PC |
| `VarArg` | Copy frame varargs to fixed/open result window | none | Commit `PC+1`; result top exactly matches interpreter rules |
| `Unary` | Primitive fast path or generic helper | A/C/Y/T/S | Immediate result commits `PC+1`; metamethod exits `Call`; errors preserve current PC |
| `Binary` | Primitive fast path or generic helper | A/C/Y/T/S | Same as `Unary`; Runtime owns overflow, conversion, NaN, concat and comparison semantics |
| `Jump` | Direct branch; close operand uses Runtime | none or A/C/Y/T/S | Plain jump commits `B`; closing jump exits before target commit and may resume close machinery |
| `JumpIfFalse` | Truthiness test and branch | none | Commit `B` when false, otherwise `PC+1` |
| `JumpIfTrue` | Truthiness test and branch | none | Commit `B` when true, otherwise `PC+1` |
| `Call` | Scheduler-visible call boundary | A/C/Y/T/S | Exit `Call` with callable/argument/result windows; continuation prevents repeated invocation |
| `TailCall` | Scheduler-visible boundary | A/C/Y/T/S | Exit `TailCall`; scheduler applies hooks/close and snapshots target/arguments when required |
| `Return` | Scheduler-visible boundary | A/C/Y/T/S | Exit `Return`; preserve result window; scheduler performs close, transfer and return hook |
| `Close` | Runtime close boundary | A/C/Y/T/S | May call/yield/throw; commit `PC+1` only after all required closers finish |
| `MarkToBeClosed` | Runtime validation and frame close-list update | T | Validate `__close`, update owner-visible state, then commit `PC+1`; failure preserves current PC |
| `NumericForPrepare` | Generic Runtime numeric-for helper | T | Commit selected target/fall-through PC; conversion error preserves current PC |
| `NumericForLoop` | Generic Runtime numeric-for helper | T | Mandatory backedge poll; commit loop target/fall-through without endpoint wraparound |

Tier 2 may specialize a row only if all assumptions are represented by explicit tag,
metatable, shape, storage, target, hook, and ABI guards as applicable. A failed guard uses the
generic path when no state reconstruction is needed; otherwise it uses a verified deoptimization
map to the canonical PC and register model.

## Artifact loading and NativeAOT

Persisted CIL is emitted with `System.Reflection.Metadata` and `ManagedPEBuilder`. Ordinary
CoreCLR may load a compatible artifact into a collectible `AssemblyLoadContext`. NativeAOT does
not dynamically emit or load CIL: the build task generates the CIL assembly plus a static C#
registration manifest before `CoreCompile`, so trimming and NativeAOT see direct method-group
references. Source compiled dynamically inside a NativeAOT process uses the interpreter.

## Consequences

- Interpreter refactoring must separate scheduler responsibilities from opcode execution before
  a code generator is added.
- The Runtime code-generation facade becomes a deliberately small versioned compatibility
  surface.
- Tier 1 can remain simple and correct by returning at semantic boundaries; later specialization
  does not need to reimplement continuation or unwind behavior.
- Exact hooks and very small instruction budgets may reduce compiled block length or force
  interpreter execution. Correctness takes priority over compiled-code residency.
- AOT and JIT share a typed method plan but use different encoders, avoiding two opcode lowerers.

## Validation requirements

Every backend must run the same differential corpus for results, Lua errors, coroutine signals,
hook traces, close order, logical-GC stress, and instruction-budget exhaustion. Tests must support
forced exits at every canonical PC, forced guard failure, and forced GC at every safe point before
Tier 2 is enabled by default.
