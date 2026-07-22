# 64-bit instruction accounting

Instruction budgets provide deterministic resource accounting across interpreter and compiled execution. Counters use signed 64-bit values so long-running workloads cannot wrap a narrower counter into an unintended allowance.

Each canonical instruction contributes exactly once. Compiled regions reserve and consume the same logical count as the interpreter, and a budget boundary returns through the scheduler before the next observable instruction. Compact Tier 0, Tier 1, Tier 2, and OSR use shared or equivalent verified arithmetic.

Deoptimization restores accounting consistent with the canonical PC. Exhaustion follows the documented resource-limit diagnostic path; it is never represented as unchecked overflow or a generated-code exception.
