# Lua conformance and differential testing

Lunil separates embedded semantic fixtures, official PUC Lua oracle comparisons, and
cross-runtime performance measurements. These gates have different trust and availability
contracts and must not be described as interchangeable.

## Default CI gates

The main CI workflow runs two multi-version correctness layers:

1. `PucMultiVersionConformanceHarnessTests` validates pinned official-suite archives for Lua 5.1,
   5.2, and 5.5. It also executes checked-in semantic fixtures for Lua 5.3 and 5.4 with typed result
   assertions. The semantic fixtures cover integer division, bitwise operators, goto, and the Lua
   5.4 to-be-closed protocol; they are not presented as upstream PUC suites.
2. `puc-multi-version-differential` downloads the official Lua 5.1.5, 5.2.4, 5.3.6, 5.4.8, and
   5.5.0 source archives, verifies pinned SHA-256 hashes, builds `lua` and `luac`, and runs the
   versioned source/imported-chunk/produced-chunk comparisons in `Lunil.Runtime.Tests`.

The existing six-RID conformance evidence continues to execute the selected official Lua 5.4.8
user-mode suite and deterministic stability corpus. The five-version PUC differential job is a
separate required Linux job because its purpose is semantic comparison, not platform coverage.

## Local PUC Lua comparisons

The versioned differential tests read these environment variables:

| Version | Interpreter | Compiler |
| --- | --- | --- |
| 5.1 | `LUNIL_PUC_LUA51` | `LUNIL_PUC_LUAC51` |
| 5.2 | `LUNIL_PUC_LUA52` | `LUNIL_PUC_LUAC52` |
| 5.3 | `LUNIL_PUC_LUA53` | `LUNIL_PUC_LUAC53` |
| 5.4 | `LUNIL_PUC_LUA54` | `LUNIL_PUC_LUAC54` |
| 5.5 | `LUNIL_PUC_LUA55` | `LUNIL_PUC_LUAC55` |

Without a matching executable, the external-oracle portion of a local test is a no-op. Use
`scripts/Install-PucLuaOracles.ps1` on Unix to reproduce the CI configuration; it never accepts an
unverified archive.

## Cross-runtime measurements

The `cross-runtime-performance.yml` workflow and `benchmarks/cross-runtime/semantic-matrix.json`
describe performance workloads and semantic compatibility groups. NeoLua, UniLua, Luau, Wasmoon,
and GopherLua entries are optional benchmark engines. Their absence does not count as correctness
evidence, and timing rows are never semantic assertions.

Only result comparisons implemented as tests are correctness gates. The semantic matrix records
which comparisons would be meaningful when an engine is provisioned; it does not claim that every
listed engine runs in default CI.
