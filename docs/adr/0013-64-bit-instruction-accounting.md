# ADR 0013: Use 64-bit compiled instruction accounting

- Status: Accepted for the 0.8 development line
- Date: 2026-07-16
- Target: Lua 5.4.8, .NET 10
- Extends: [ADR 0001](0001-execution-backend-abi-v1.md),
  [ADR 0011](0011-linear-numeric-regions.md)

## Context

The scheduler and configured activation budget already use `long`, but
`LuaExecutionContext.InstructionsConsumed` and `LuaCompiledExit.InstructionsConsumed` used `int`.
Before linear numeric regions, compiled methods returned to the scheduler often enough that one
entry could not realistically cross `Int32.MaxValue`. A numeric region can now remain inside one
generated method across many safepoint polls. Cross-runtime calibration on osx-arm64 selected a
batch large enough to overflow the checked 32-bit accumulator even though the 64-bit activation
budget remained valid.

Saturating or silently wrapping the counter would make exact instruction budgets, telemetry, and
exception-path accounting incorrect. Artificially returning at an arbitrary 32-bit threshold would
also add a scheduler boundary that exists only because of the representation.

## Decision

`LuaExecutionContext` stores its consumed and last-observed instruction counts as `long`.
`LuaCompiledExit.InstructionsConsumed` is also `long`, so scheduler validation and activation
accounting preserve the complete value.

Every exit factory gains a `long instructionsConsumed` overload. The existing `int` overloads
remain and forward to the 64-bit implementation. Persisted CIL and third-party emitters compiled
against the existing factory signatures therefore retain their call targets, while dynamic Tier 2,
Loop OSR, and numeric-region emitters bind to the 64-bit overloads.

`TryReserveInstructions` continues to accept one positive `int` range at a time. Its checked
accumulator now overflows only at the 64-bit limit, consistent with `MaximumInstructionCount` and
`LuaSchedulerActivation.InstructionCount`.

## Verification

- Runtime ABI tests reserve `Int32.MaxValue + 10` instructions across two ranges and require the
  exact remaining and consumed 64-bit values.
- Existing int-factory tests continue to exercise the retained signatures.
- Reflection.Emit numeric-region, Tier 2, and Loop OSR tests resolve and execute the long factory
  overloads.
- Full solution, conformance, public API, formatting, package, and six-RID performance gates remain
  required on the integrated pull request.

## Consequences

- The advanced `LuaCompiledExit.InstructionsConsumed` property type changes from `int` to `long` in
  the open 0.8 Alpha API snapshot.
- Existing emitter calls to the int factory overloads remain source and binary compatible.
- Hosts reading the property must accept the wider value; narrowing requires an explicit checked or
  saturating policy chosen by that host.
- Long-running compiled entries preserve exact budgets and telemetry without representation-driven
  scheduler exits.
