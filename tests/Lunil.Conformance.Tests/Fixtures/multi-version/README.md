# Multi-version official suite fixtures

Pinned official test archives and `manifest.json` files:

| Version | Archive | Notes |
|---|---|---|
| `5.1/` | `lua5.1-tests.tar.gz` | Official suite from lua.org/tests |
| `5.2/` | `lua-5.2.2-tests.tar.gz` | Latest published 5.2 suite (no 5.2.4 package); extracted as `lua-5.2.4-tests/` for harness path stability |
| `5.3/` | semantic source fixture | Integer division, bitwise operators, and goto control flow |
| `5.4/` | semantic source fixture | Integer division, bitwise operators, goto, and to-be-closed locals |
| `5.5/` | `lua-5.5.0-tests.tar.gz` | Official Lua 5.5.0 suite |

The 5.1, 5.2, and 5.5 manifests use schema `lunil.lua-conformance-manifest.v1` with:

- `archiveFileName`, `archiveSha256`, `upstreamVersion`, `upstreamUrl`
- `files[]` with `path`, `sha256`, `classification`, `reason`

Classifications: `driver-or-helper`, `executed-user-mode`, `excluded-user-mode`.

`PucMultiVersionConformanceHarnessTests` validates manifests and archive SHA-256 when present,
plus version smoke execution. The 5.3 and 5.4 manifests use
`lunil.lua-semantic-fixture.v1`; their checked-in source cases are executed with value-level
assertions for version-specific semantics. Official archive execution remains a separate gate and
does not pretend that these semantic fixtures are upstream PUC suites.
