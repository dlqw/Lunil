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

Lunil 0.10.0 delivers explicit, selectable contracts for Lua 5.1, Lua 5.2, Lua 5.3, Lua 5.4,
and Lua 5.5. Stable `0.10.0` ships independent binary-chunk codecs, Lua 5.1 function-environment
compatibility (`getfenv`/`setfenv`/`module`), multi-version semantic/JIT fixtures, and
cross-runtime performance wiring. The 0.10.x CI line additionally builds pinned official PUC Lua
oracles for five-version source and chunk differential tests. Optional NeoLua, Luau, GopherLua,
Wasmoon, and UniLua benchmark rows are not correctness evidence. Each version has its own language
and runtime contract:

- version-specific syntax, lexical rules, operators, and multiple-result behavior;
- version-specific VM instructions and binary-chunk formats with explicit version validation;
- the version-scoped standard library surface and tested error behavior for that version;
- coroutines, metatables, weak tables, finalizers, debug facilities, resource accounting, and
  close/yield behavior;
- version-aware source, chunk, compiler, interpreter, and host configuration APIs;
- checked-in semantic fixtures and PUC differential coverage for every supported version.

Lua 5.4 remains the default compatibility baseline. The 0.10.0 contract lets a host select each
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

## 0.11.0 — capability-controlled CLR interoperation

The 0.11.0 line adds a host-owned CLR bridge without changing the meaning of any Lua 5.1–5.5
language contract. The bridge is opt-in, capability-controlled, and unavailable to a restricted host
unless the host explicitly supplies an allowlist.

| Milestone | User-visible scope | Dependencies | Acceptance criteria | Public surfaces and verification |
| --- | --- | --- | --- | --- |
| `0.11.0-alpha.1` | Discover allowlisted CLR types, construct allowlisted objects, and represent them as Lua userdata with explicit ownership. | Existing host profiles, userdata ownership, language-version contract. | Restricted hosts deny discovery and construction by default; configured assemblies and type names are matched exactly; constructors use deterministic conversion and reject unsupported values; no arbitrary assembly loading occurs; interpreter and JIT observe the same results. | Hosting API, interop guide, capability examples, host tests, trimming analysis, and package-consumer smoke. |
| `0.11.0-alpha.2` | Invoke allowlisted static/instance methods, properties, fields, indexers, and operators. | Alpha.1 object identity and conversion rules. | Member lookup is allowlist-scoped and cached by type identity; overload resolution is deterministic; optional/named arguments, enums, nullable values, arrays, tuples, and numeric conversions have explicit rules; inaccessible members fail with stable Lua diagnostics. | Hosting API reference, conversion matrix, member-resolution tests, and NativeAOT/trimming fixture coverage. |
| `0.11.0-alpha.3` | Convert Lua functions to CLR delegates and route CLR callbacks/events back into Lua. | Alpha.2 invocation and host scheduling boundaries. | Delegate signatures are validated before subscription; callbacks preserve state ownership, error boundaries, cancellation, and coroutine rules; event subscriptions are disposable and cannot retain an unrooted Lua closure. | Callback guide, lifecycle examples, delegate/event tests, GC and reentrancy coverage. |
| `0.11.0-beta.1` | Add task/`ValueTask`, cancellation, `ref`/`out`, exception translation, disposal, and thread policy. | Alpha.3 callback lifetime and scheduler contracts. | Async results have one documented Lua representation; cancellation and CLR exceptions map to stable diagnostics; `ref`/`out` results preserve ordering; disposal is idempotent; calls from unsupported threads fail closed. | Migration notes, error contract, async and failure-path tests, package and publish-mode validation. |
| `0.11.0-rc.1` | Freeze the interop contract across all supported Lua versions and runtime publish modes. | Beta.1 complete behavior and compatibility baselines. | Lua 5.1–5.5 behavior is matrix-tested; interpreter/JIT/NativeAOT/trimming/ReadyToRun agree; allowlist and lifetime rules are documented; public API and package baselines are reproducible. | API/package baselines, release notes, compatibility matrix, six-RID bundle smoke, and CLI/consumer examples. |

The bridge remains part of the public hosting contract, but it is never an unrestricted reflection
escape hatch. A host must declare the assemblies, types, members, construction policy, and lifetime
policy that Lua may observe.

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
