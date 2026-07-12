# Execution backend performance baseline

This document records local reference measurements for JIT/AOT backend work. They are regression
evidence from one machine, not portable pass/fail limits. CI publishes the same benchmark output
for every native RID without failing a build on timing variance.

## M0 Windows x64 baseline

- Date: 2026-07-12
- Commit base: `main@dc38dd2` plus the M0 benchmark corpus
- Configuration: Release, `iterations=1,000,000`
- Runtime: .NET 10.0.3
- OS: Windows 11 IoT Enterprise LTSC 10.0.26100, x64
- CPU: Intel Core i7-12700H, 14 cores / 20 logical processors
- Visible memory: approximately 15.7 GiB

| Scenario | Operations | ns/op | Allocated/op |
|---|---:|---:|---:|
| `table_integer_get_set` | 1,000,000 | 115.00 | 0.04 B |
| `interpreter_empty_numeric_for` | 10 | 4,461,710 | 5,788 B |
| `interpreter_arithmetic_numeric_for` | 10 | 11,158,830 | 5,980 B |
| `interpreter_cold_compile_execute_arithmetic` | 10 | 4,082,000 | 37,012 B |
| `interpreter_warm_arithmetic_numeric_for` | 10 | 3,649,020 | 1,588 B |
| `interpreter_warm_lua_call_vararg_multireturn` | 10 | 4,901,530 | 978,337.60 B |
| `interpreter_warm_table_array_hash` | 10 | 12,120,690 | 271,360.80 B |
| `interpreter_warm_metamethod` | 10 | 5,444,960 | 740,572 B |
| `interpreter_coroutine_yield_resume` | 10 | 25,050 | 4,252 B |
| `interpreter_warm_debug_count_hook` | 10 | 1,997,410 | 245,188 B |
| `interpreter_every_allocation_gc_stress` | 10 | 2,460,060 | 285,588 B |
| `full_gc_1000_tables` | 10 | 263,360 | 164,772 B |

The benchmark performs a short warmup, but it is intentionally a compact regression runner rather
than a statistical microbenchmark framework. Before a backend becomes the default, release
evidence must include repeated cold-start, warm-throughput, compilation latency, peak-memory, and
artifact-size measurements under controlled conditions.

Run locally with:

```powershell
dotnet run --project benchmarks/Lunil.Runtime.Benchmarks/Lunil.Runtime.Benchmarks.csproj `
  --configuration Release -- 1000000
```

## M1 execution-kernel allocation check

The same machine and command were used after introducing the shared execution kernel, Runtime
ABI v1, stack-window calls, and inline operation arguments. Representative allocation changes:

| Scenario | M0 allocated/op | M1 allocated/op | Change |
|---|---:|---:|---:|
| Lua call + vararg + multiple return | 978,337.60 B | 690,376 B | -29.4% |
| Metamethod loop | 740,572 B | 516,412 B | -30.3% |
| Debug count-hook loop | 245,188 B | 178,588 B | -27.2% |

The new fixed-argument Lua-call scenario measured 514,376 B per 2,000-call execution. It no
longer creates a parameter array for each direct Lua-to-Lua call; the remaining approximately
257 B/call is primarily the current logical frame, continuation, and close-list representation.
Vararg frames retain a persistent vararg window by design. Later frame pooling is a separate
optimization and is not required to preserve the Runtime ABI v1 contract.
