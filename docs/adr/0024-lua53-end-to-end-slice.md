# ADR 0024: Lua 5.3 end-to-end compatibility slice

- Status: Accepted
- Date: 2026-07-20
- Target: `0.10.0-alpha.1`
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

## Scope of this slice

The slice covers the source/compiler/runtime/hosting path, the Lua 5.3 standard-library boundary,
and verified import of Lua 5.3 chunks into canonical IR. Canonical Lua 5.3 chunk writing and the
complete PUC Lua 5.3 conformance corpus remain follow-up work in the same task; until then the
existing Lua 5.4 build target must not emit a mislabeled Lua 5.3 chunk.

## Consequences

- Existing callers without a version selection continue to use Lua 5.4.
- Lua 5.1, 5.2, and 5.5 still return `LUA0001`; they are not mapped to Lua 5.3 or Lua 5.4.
- The Lua 5.3 adapter can evolve its header, prototype, and opcode verification independently from
  `Lunil.IR.Lua54`.
- The remaining work is explicit: complete opcode edge cases, add a canonical Lua 5.3 writer, and
  register official Lua 5.3 conformance/differential fixtures.
