# Multi-version official suite fixtures

Pinned official test archives and `manifest.json` files:

| Version | Archive | Notes |
|---|---|---|
| `5.1/` | `lua5.1-tests.tar.gz` | Official suite from lua.org/tests |
| `5.2/` | `lua-5.2.2-tests.tar.gz` | Latest published 5.2 suite (no 5.2.4 package); extracted as `lua-5.2.4-tests/` for harness path stability |
| `5.5/` | `lua-5.5.0-tests.tar.gz` | Official Lua 5.5.0 suite |

Each `manifest.json` uses schema `lunil.lua-conformance-manifest.v1` with:

- `archiveFileName`, `archiveSha256`, `upstreamVersion`, `upstreamUrl`
- `files[]` with `path`, `sha256`, `classification`, `reason`

Classifications: `driver-or-helper`, `executed-user-mode`, `excluded-user-mode`.

`PucMultiVersionConformanceHarnessTests` validates manifests and archive SHA-256 when present,
plus version smoke execution. Full user-mode execution of every `executed-user-mode` script is a
follow-on runner expansion; silent skips without reasons are forbidden.
