# Runtime ABI for compiled execution

The runtime ABI is the compatibility boundary between canonical-IR code generators and the managed runtime. A compiled entry validates function identity, register window, frame shape, backend mode, and hook/debug state before using optimized services. A failed validation returns to canonical execution.

The ABI supplies guarded primitive helpers and unchecked frame accessors. Unchecked access is permitted only after validated entry and within a verified register range. Numeric and close helpers preserve canonical PC, Lua arithmetic semantics, and budget accounting.

Generated artifacts carry an exact ABI identity together with their canonical module identity. Loaders reject an incompatible artifact instead of adapting it by guesswork. Hosts without dynamic-code support retain the same behavior through the managed path.
