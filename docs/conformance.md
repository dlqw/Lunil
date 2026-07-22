# Lua conformance and differential testing

This guide distinguishes semantic conformance from performance comparison and shows how to reproduce a PUC Lua differential run.

## What is compared

Lunil validates supported Lua language profiles with two complementary sources:

1. checked-in semantic fixtures exercise version-specific behavior such as integer division, bitwise operations, `goto`, and Lua 5.4 to-be-closed variables;
2. differential comparisons run the same source and binary-chunk cases against the matching official PUC Lua interpreter and compiler.

A differential result is meaningful only when the source, selected Lua version, and chunk format match. Performance data and cross-runtime timing are not semantic assertions.

## Configure local PUC Lua tools

The differential harness reads these variables:

| Version | Interpreter | Compiler |
| --- | --- | --- |
| 5.1 | `LUNIL_PUC_LUA51` | `LUNIL_PUC_LUAC51` |
| 5.2 | `LUNIL_PUC_LUA52` | `LUNIL_PUC_LUAC52` |
| 5.3 | `LUNIL_PUC_LUA53` | `LUNIL_PUC_LUAC53` |
| 5.4 | `LUNIL_PUC_LUA54` | `LUNIL_PUC_LUAC54` |
| 5.5 | `LUNIL_PUC_LUA55` | `LUNIL_PUC_LUAC55` |

Install matching tools from verified official source archives. On Unix, `scripts/Install-PucLuaOracles.ps1` provisions the pinned archives and their hashes. If a matching executable is not configured, the external-oracle portion of a local run is skipped; that is not a conformance result.

## Cross-runtime comparison

`benchmarks/cross-runtime/semantic-matrix.json` identifies the Lua dialect or semantic group for each optional comparison engine. Compare results only within a compatible group, and treat absent optional engines as unavailable data rather than success or failure. Use the [performance guide](performance.md) for the measurement protocol.
