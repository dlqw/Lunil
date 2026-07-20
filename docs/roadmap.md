# Lunil roadmap

[简体中文](roadmap.zh-CN.md)

This document contains the active roadmap for the next three release lines. Each release has one
primary scope; later releases do not change the compatibility contract of earlier releases.

## History

| Release line | Delivered scope |
| --- | --- |
| `0.1.0` | Established the Lua 5.4.8/.NET compiler foundation: lossless lexer/parser, semantic binding, canonical register IR, verifier, baseline interpreter, closures, tables, and Lua value representation. |
| `0.2.0` | Added the logical heap and incremental/generational GC, weak tables, ephemerons, finalizers, metatables, protected errors, `<close>`, table storage, and runtime allocation baselines. |
| `0.3.0` | Added the continuation ABI, resumable native calls, activation-stack coroutine scheduling, the Lua coroutine library, and GC-visible coroutine state. |
| `0.4.0` | Added PUC Lua 5.4 prototype conversion, binary-chunk loading/execution, complete opcode conversion, debug provenance, and chunk verification. |
| `0.5.0-alpha.1` | Established the Lunil product identity, six-RID packaging, public documentation, SemVer release channels, NuGet distribution, and repository release boundaries. |
| `0.6.0-alpha` | Expanded the standard library and execution kernel, introduced the typed CIL/JIT and persisted-CIL experiments, and unified compiled-backend accounting and fallback contracts. |
| `0.7.0` | Published the stable compiler, analysis, hosting, workspace, CLI, Lua 5.4.8 chunk support, standard library, and first stable managed execution product. |
| `0.8.0` | Delivered the stable runtime/JIT performance line, guarded table and direct-call paths, exact 64-bit accounting, NativeAOT/trimming support, and removed the persisted/static Lua AOT product. |
| `0.9.0` | Delivered the shared guarded Tier 2 string/table paths, adaptive JIT baseline, versioned performance dataset, six-RID release bundles, and the stable .NET 10/Lua 5.4.8 release. |

## 0.10.0 — Lua version compatibility and runtime comparisons

Lunil 0.10.0 targets complete support for Lua 5.1, Lua 5.2, Lua 5.3, Lua 5.4, and Lua 5.5. Lua
5.2 is available as an explicit `0.10.0-alpha.2` adapter; the remaining versions are delivered
through the same generated profile/adapter boundary. Each version has its own language and runtime
contract:

- version-specific syntax, lexical rules, operators, and multiple-result behavior;
- version-specific VM instructions and binary-chunk formats with explicit version validation;
- the complete standard library surface and error behavior for that version;
- coroutines, metatables, weak tables, finalizers, debug facilities, resource accounting, and
  close/yield behavior;
- version-aware source, chunk, compiler, interpreter, and host configuration APIs;
- conformance and differential coverage for every supported version.

Lua 5.4 remains the 0.9.x compatibility baseline. The 0.10.0 contract must let a host select each
version without silently applying another version's semantics.

The performance dataset adds these independent runtimes:

| Runtime | Primary semantics | Implementation | Comparison group |
| --- | --- | --- | --- |
| [NeoLua](https://github.com/neolithos/NeoLua) | Lua 5.3-style | C# / .NET DLR | Managed .NET |
| [UniLua](https://github.com/xebecnan/UniLua) | Lua 5.2 | Pure C# | Managed / Unity |
| [Luau](https://github.com/luau-lang/luau) | Lua 5.1-compatible dialect | C++ VM | Production Lua dialect |
| [Wasmoon](https://github.com/ceifa/wasmoon) | Official Lua 5.4 | WebAssembly with JavaScript bindings | Lua 5.4 semantic peer |
| [GopherLua](https://github.com/yuin/gopher-lua) | Lua 5.1 | Go VM and compiler | Embedded foreign-runtime VM |

Published rows identify the exact runtime version and source identity. Lua 5.1–5.5, LuaJIT, and
dialect-specific runtimes remain in separate semantic groups rather than one combined score.

## 0.11.0 — complete CLR interoperation

The 0.11.0 line targets a complete, capability-controlled CLR bridge:

- CLR type discovery and construction;
- static and instance members, properties, indexers, fields, methods, operators, and events;
- overload resolution, optional and named arguments, generic methods and types, arrays, enums,
  nullable values, tuples, and value/reference conversions;
- Lua function to CLR delegate conversion and CLR delegate/event callbacks into Lua;
- CLR exceptions, cancellation, async/task values, `ref`/`out` parameters, and deterministic error
  translation;
- explicit lifetime, disposal, ownership, threading, reflection, and sandbox capability rules;
- consistent behavior in the interpreter, JIT, NativeAOT, trimming, and all supported Lua versions.

The bridge is part of the public hosting contract and is not an unrestricted reflection escape hatch.

## 0.12.0 — complete hot-update support

The 0.12.0 line targets safe live updates for long-running hosts:

- module and source replacement without restarting the host;
- function and closure replacement with explicit capture/upvalue migration rules;
- generation-aware invalidation for interpreter, JIT, CLR interop, and cached call sites;
- active-frame behavior for running calls, coroutines, yields, callbacks, and pending tasks;
- transactional update, validation before publication, rollback, and failure isolation;
- state migration hooks for tables, userdata, resources, and host-owned values;
- observable update status, diagnostics, version identity, and capability restrictions.

An update never changes the meaning of an already-published Lua version or leaves an active frame
with an invalid code or value identity.
