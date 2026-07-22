# Loop OSR hotness and observation model

Loop OSR tracks inexpensive verified backedges before it performs natural-loop, dominator, liveness, or specialization analysis. Analysis begins at the configured backedge threshold; the triggering edge seeds the matching loop counter without requiring another full hotness interval.

A structurally supported loop becomes a compilation candidate only after observed operands satisfy the exact-numeric contract. Table, metamethod, closure, and other nonnumeric values are never treated as numeric facts merely because their control flow can be compiled.

Rejected functions remain on canonical execution. Guard misses resume at the canonical PC, while the scheduler continues to own calls, yields, errors, close handling, hooks, and GC safepoints.
