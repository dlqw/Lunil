# ADR 0028: Lua version syntax / lexical boundary audit

- Status: Accepted
- Date: 2026-07-21
- Target: `0.10.x` roadmap completion
- Related: [ADR 0023](0023-lua-language-version-contract.md), [ADR 0026](0026-lua51-lua55-version-adapters.md)

## Context

Lunil 0.10 ships explicit language-version identity through lexer, parser, binder, chunk
adapters, and stdlib surface gates. The remaining gap called out by the 0.10.0 tasklist is a
**systematic audit** of version-conditioned syntax and lexical boundaries so the pipeline is not
implicitly 5.4-centric.

## Decision

Maintain a version-by-version surface checklist, enforced by `LanguageVersionSyntaxContractTests`,
covering the differences that scripts actually trip over:

| Boundary | 5.1 | 5.2 | 5.3 | 5.4 | 5.5 |
|---|---|---|---|---|---|
| `goto` / labels | reject | accept | accept | accept | accept |
| bitwise ops (`& \| ~ << >>`) | reject | reject | accept | accept | accept |
| floor division `//` | reject | reject | accept | accept | accept |
| local attributes `<const>` / `<close>` | reject | reject | reject | accept | accept |
| `global` declarations | treat as identifier | identifier | identifier | identifier | accept |
| named vararg (`... name`) | reject | reject | reject | reject | accept |
| hex float (`0x1.8p1`) | version-gated decoder | version-gated | accept | accept | accept |
| `\z` / Unicode escapes in strings | decoder policy | decoder policy | 5.3 max | 5.4+ | 5.4+ |

Lexical numbers, string escapes, and attribute syntax keep existing specialized decoders; this ADR
records the matrix and requires regression tests rather than rewriting the 5.4 grammar wholesale.

## Consequences

- Missing version gates must fail parse/lex diagnostics with stable codes (`LUA2010` / `LUA2011` family).
- Conformance suite failures for 5.1/5.2/5.5 drive priority of additional matrix rows.
- JIT and runtime continue to consume the same version identity; syntax gates do not silently
  widen under Auto policy.
