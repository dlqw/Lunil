# Linear numeric regions

A numeric region is a verified single-entry sequence of canonical instructions that can be specialized with a precise fallback point. The analyzer tracks reaching definitions, register liveness, operand kinds, side effects, safepoints, and each region entry/exit PC.

The emitter keeps proven values in managed locals, checks assumptions before effects, and writes results through the shared ABI. It reserves the canonical instruction count and records a deoptimization PC for each guardable operation.

A guard miss materializes required register state and transfers to the instruction that has not yet executed. It cannot replay a write, skip a metamethod, or retain speculative values. Function-entry Tier 2 and loop OSR share this analyzer, plan, and emitter.
