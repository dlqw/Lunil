# Lazy preparation for loop specialization

The loop-specialization pipeline is prepared only after a loop has satisfied automatic exact-numeric admission. Preparation is idempotent and concurrency-safe; it does not change the frame or program counter. A failure simply leaves the loop on canonical execution.

When measuring OSR configurations, distinguish one-time pipeline preparation, per-loop compilation, and steady-state execution. Compare identical Lua workloads and semantic inputs. Measurements describe a workload and do not redefine the runtime contract.

Lazy preparation retains all normal validation: guards, instruction accounting, cancellation, bounded cache ownership, and dynamic-code capability checks. A host without dynamic code continues through the managed runtime.
