# How to import PUC Lua 5.4 prototypes

[简体中文](puc-prototype-import.zh-CN.pub.md)

This guide shows the validated path from a PUC Lua 5.4 binary chunk to canonical IR. The resulting
module can run through the same interpreter and JIT contracts as source-compiled modules.

## 1. Import the chunk

```text
binary bytes
  → Lua54ChunkReader (format, width, and resource budgets)
  → Lua54ChunkVerifier (execution-level structure and control flow)
  → Lua54PrototypeConverter (two-pass PC mapping)
  → LuaIrVerifier
  → LuaState main-closure and upvalue materialization
  → LuaInterpreter
```

Prototypes receive dense preorder identifiers, with each parent assigned before its children. The
converter first fixes function IDs for the whole tree, then emits each function and its raw-PC to
canonical-PC map, and finally patches control-flow edges and debug-local ranges. A PUC opcode that
expands into several canonical instructions therefore still has a stable continuation PC.

## 2. Preserve instruction semantics

- Register, constant, upvalue, closure, table, and unary instructions map directly or expand through
  three reserved scratch registers above the PUC `MaximumStackSize`.
- `MMBIN`, `MMBINI`, and `MMBINK` combine with their preceding arithmetic/fallback instruction into
  one resumable `Binary`; a negative shift immediate selects `__shl` or `__shr` and adjusts operand
  order.
- Comparisons, `TEST`, and `TESTSET` combine with the following `JMP` into explicit conditional
  edges; `LFALSESKIP` preserves the edge that skips `LOADTRUE`.
- `LOADKX`, `NEWTABLE`, and extended `SETLIST` consume `EXTRAARG`; table-capacity hints remain
  bounded by the logical-byte quota.
- `CALL`, `TAILCALL`, `RETURN`, `VARARG`, and `SETLIST` convert PUC zero-encoded open windows to the
  canonical `-1` representation without allocating intermediate CLR arrays.
- `TFORPREP` marks the closing value, `TFORCALL` uses the PUC `A+4` call window, and `TFORLOOP`
  explicitly tests the first result and updates the control variable.
- `FORPREP` and `FORLOOP` support both PUC integer-counter and floating-limit forms. The integer
  counter interprets its 64-bit payload as `ulong`, covering the complete `long.MinValue` through
  `long.MaxValue` range.
- `TBC`, `CLOSE`, ordinary returns, and tail calls enter the same resumable close/continuation ABI.

## 3. Validate untrusted input

Before conversion, the chunk verifier rejects:

- out-of-range registers, ranges, constants, upvalues, prototypes, or jumps;
- mismatched `MMBIN*`, `EXTRAARG`, or test-companion `JMP` instructions, including companions that
  external control flow could enter;
- open call, return, or set-list windows that do not immediately follow a top producer;
- mismatched numeric/generic-for targets or iterator windows;
- inconsistent `<close>` marker ordering or control-flow merge state;
- invalid line deltas, absolute markers, local ranges, or debug-table counts; and
- extended arguments that would overflow runtime indexes or capacities.

The converted module passes the independent canonical verifier again. Execution creates closures,
upvalues, strings, and tables owned by the current `LuaState`; cross-state references are not
accepted.
