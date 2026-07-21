# Optional cross-runtime engine hosts

Protocol hosts used by `Lunil.Runtime.CrossRuntimeBenchmarks` for optional engines.
Each host accepts `workload.lua operations warmup` (or Luau host with extra `luau.exe` arg)
and prints a `cross_runtime_result\t...` line.

| Host | Engine |
|---|---|
| `luau-host.cjs` | Luau CLI (rewrite workload; `load` is sandboxed) |
| `wasmoon-host.cjs` | Wasmoon 1.16.0 via Node |
| `gopherlua/main.go` | GopherLua 1.1.1 (`go build -o gopherlua-host.exe .`) |

Provision pinned tools with `scripts/Install-OptionalCrossRuntimeEngines.ps1`.
NeoLua / UniLua use separate net8 harness projects under `benchmarks/`.
