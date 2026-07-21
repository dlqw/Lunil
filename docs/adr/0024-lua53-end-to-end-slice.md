# ADR 0024: Lua 5.3 end-to-end compatibility slice

- Date: 2026-07-20
- Related issue: GitHub #83

## Context

The version contract introduced explicit Lua 5.1–5.5 identity while deliberately keeping Lua 5.4
as the default and failing closed for versions that had no implementation. The next delivery slice
is Lua 5.3. Lua 5.3 shares the integer, bitwise, floor-division, `goto`, and `_ENV` foundations
needed by the existing canonical register IR, but its source grammar, standard-library surface, and
binary chunk format are not identical to Lua 5.4.

## Decision

1. Lua 5.3 source compilation and managed execution are enabled independently of Lua 5.4. The
   compiler publishes a Lua 5.3 canonical module and the host creates a Lua 5.3 state for it.
2. Lua 5.4-only local attributes (`<const>` and `<close>`) are rejected by the Lua 5.3 parser.
   They are not silently ignored or reinterpreted.
3. Lua 5.3 standard-library installation is enabled with version-specific surface differences:
   `warn` and `coroutine.close` are not installed because they were introduced in Lua 5.4.
4. Lua 5.3 binary chunks use an independent `Lunil.IR.Lua53` reader, instruction model, opcode
   identity, and canonical converter. The Lua 5.4 reader is not changed into a format-conditional
   reader.
5. Binary loading dispatches from the runtime state's language version. Reader resource budgets
   remain enforced when the existing host/runtime reader options are supplied.
6. The zero-runtime-cost generation boundary is `Lunil.IR.Generators`. It generates both the Lua
   5.3 opcode/instruction codec and the `LuaVersionFeatures` table from version metadata. Adapter
   symbols (`LUNIL_LUA53_ADAPTER`, `LUNIL_LUA54_ADAPTER`, controlled by the matching MSBuild
   `LunilLua*AdapterEnabled` properties) are selected by conditional compilation; runtime dispatch
   consumes the generated table, so shared VM/library code is not copied per version and an
   uncompiled version cannot inherit Lua 5.4 behavior accidentally.

## Scope of this slice

The slice covers the source/compiler/runtime/hosting path, the Lua 5.3 standard-library boundary,
Lua 5.3 canonical chunk writing, and verified import of Lua 5.3 chunks into canonical IR. The
official Lua 5.3.4 test archive is pinned with selected upstream fixtures. Platform-specific,
internal-API, and unsupported-library cases are outside this compatibility contract. An opt-in
PUC Lua 5.3 differential corpus is available through
`LUNIL_PUC_LUA53` (or `lua5.3`/`lua53` on PATH).

## Consequences

- Existing callers without a version selection continue to use Lua 5.4.
- Lua 5.1, 5.2, and 5.5 still return `LUA0001`; they are not mapped to Lua 5.3 or Lua 5.4.
- The Lua 5.3 adapter can evolve its header, prototype, and opcode verification independently from
  `Lunil.IR.Lua54`.
- Future Lua 5.1, 5.2, and 5.5 adapters can be enabled by adding their profile metadata and
  compile-time capability without changing the shared runtime dispatch shape.
- The remaining work is explicit: complete opcode edge cases and grow the selected official
  conformance set as the Lua 5.3 standard-library and runtime matrix expands.
