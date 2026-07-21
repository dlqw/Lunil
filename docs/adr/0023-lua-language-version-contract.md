# ADR 0023: Explicit Lua language-version contract

- Date: 2026-07-20
- Related: [0.10.0 roadmap](../roadmap.md), [version management](../versioning.md)

## Context

Lunil 0.9.x exposes a complete Lua 5.4.8 compiler and managed runtime. The 0.10.0 roadmap expands
the compatibility target to Lua 5.1, 5.2, 5.3, 5.4, and 5.5. These versions are separate language
and runtime contracts; selecting one must not silently apply another version's syntax, chunk format,
standard-library behavior, or execution semantics.

The existing pipeline has independent lexer, parser, binder, compiler, workspace, canonical IR,
runtime, hosting, and CLI boundaries. A version identity that exists only at the CLI or host layer
would be lost before lowering, and a version identity that is only a display string would not safely
partition compiled/JIT artifacts.

## Decision

1. Define `LuaLanguageVersion` and its validation/display helpers in `Lunil.Core`, which has no
   project dependencies and can therefore be consumed by every lower layer.
2. Default every version-aware option and result to Lua 5.4. `LuaCompilerOptions.LanguageVersion`,
   `LuaWorkspaceOptions.LanguageVersion`, and `LuaHostOptions.LanguageVersion` are authoritative;
   nested options are aligned to the owning boundary's value.
3. Preserve the selected identity in lexer, parser, semantic model, compilation result, canonical
   IR module, runtime state, and execution context.
4. Increase the canonical IR format version when the module identity becomes part of the module
   contract. The deterministic JIT module serializer includes the identity, so persisted profiles
   cannot be reused across language contracts.
5. Reject module/state version mismatches at closure creation. A Lua 5.4 chunk reader/writer and
   Lua 5.4 standard-library entry points reject non-5.4 semantic modules/states rather than
   pretending to support another version.
6. Expose explicit CLI configuration through `--lua-version`, `luaVersion`, and
   `LUNIL_LUA_VERSION`.
7. Until a version's complete semantics are implemented, compilation returns a stable configuration
   diagnostic (`LUA0001`) and does not publish executable IR. This is intentional: a selected but
   incomplete version must fail closed rather than fall back to Lua 5.4.
8. Add future versions as vertical source/chunk/runtime/standard-library/conformance slices. Do not
   turn the existing Lua 5.4 chunk reader/writer into one conditional implementation for all formats.

## Consequences

- Existing source and host callers remain source-compatible and continue to select Lua 5.4 by
  default.
- Version selection is observable and testable at every pipeline boundary.
- The canonical IR format and persisted JIT profile compatibility line advance in this alpha.
- The first alpha can expose the complete version-selection API without claiming incomplete
  versions execute correctly.
- Future version work can add semantics without changing the meaning of an already-published Lua
  version or allowing cross-version cache reuse.

## Rejected alternatives

- **Keep one implicit global Lua version:** rejected because hosts, workspaces, and cached modules
  could silently disagree.
- **Treat version as a CLI-only setting:** rejected because the identity would be lost at compiler,
  IR, and runtime boundaries.
- **Map all currently unimplemented versions to Lua 5.4:** rejected because it violates the 0.10.0
  compatibility contract and makes incorrect behavior appear successful.
- **Use one version-conditional Lua 5.4 chunk reader/writer:** rejected because binary formats and
  opcode contracts differ materially and would create a fragile, difficult-to-verify boundary.
