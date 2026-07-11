# Luac

Luac is a pure C# Lua 5.4.8 compiler and runtime targeting .NET 10. The planned
execution stack includes a reference interpreter, CoreCLR CIL JIT, persisted
CIL AOT, and build-time .NET NativeAOT integration.

The repository is in its compiler/runtime-foundation stage. The current implementation
contains:

- immutable byte-oriented Lua source text and byte/UTF-16 location mapping;
- a lossless, bounded Lua 5.4 lexer with complete trivia and literal decoding;
- a complete error-tolerant Lua 5.4 parser with immutable syntax trees;
- lexical semantic binding for locals, captures, `_ENV`, attributes, labels, and gotos;
- verified canonical register IR and syntax/semantic-model lowering;
- a 16-byte value representation, binary strings, heap-owned collectable objects,
  explicit Lua stacks/frames, closures, and identity-bearing open/closed upvalues;
- an incremental/generational tri-color logical GC with barriers, remembered sets,
  weak tables, ephemerons, finalizers, resurrection, quotas, handles, and GC stress;
- array plus open-addressed-hash Lua tables with tombstones, `next` continuation,
  randomized hashing, and storage/shape/metatable versions;
- a baseline canonical IR interpreter with Lua/native calls, multiple results,
  varargs, control flow, numeric-string coercion, resource budgets, and tail calls;
- an explicit non-recursive coroutine scheduler, resumable native continuation ABI,
  owner-aware native closures, and the complete Lua 5.4 `coroutine` module;
- shared type/object metatable dispatch for core metamethods, protected Lua-value
  errors, `pcall`/`xpcall`, and resumable reverse-order `__close` unwinding;
- all PUC Lua 5.4 opcodes and binary-compatible 32-bit instruction layouts;
- bounded PUC Lua 5.4 binary chunk reading and writing;
- complete PUC prototype-to-canonical-IR conversion and direct binary chunk execution;
- immutable prototype, constant, upvalue, and debug-information models;
- an execution-grade chunk verifier covering operands, associated instructions,
  control flow, open stack windows, debug tables, and to-be-closed state;
- round-trip and PUC Lua 5.4.8 interoperability fixtures;
- deterministic table/GC fuzzing, malformed-IR fuzzing, PUC Lua 5.4.8 runtime
  differential fixtures, GC-stress tests, and a runtime benchmark harness.

The approved architecture and compatibility contract are documented in
[`docs/compiler-design.md`](docs/compiler-design.md).
The frozen 0.3.0 runtime ABI is documented in
[`docs/runtime-continuation-abi.md`](docs/runtime-continuation-abi.md).

## Build and test

```powershell
dotnet restore Luac.sln
dotnet test Luac.sln --configuration Release
dotnet format Luac.sln --verify-no-changes --no-restore
dotnet run --configuration Release --project benchmarks/Luac.Runtime.Benchmarks -- 1000000
```
