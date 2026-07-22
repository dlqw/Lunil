# Versioned profile-guided numeric regions

Profile data can reduce repeated planning work, but it is never proof that a later execution has the same values or module identity. Each record binds to canonical IR content, Lua language version, runtime ABI, and specialization format. Loaders validate the complete identity and discard mismatches.

Profiles identify candidate numeric regions and likely operand kinds. The compiler still runs verification and emits runtime guards before using generated code. Profile data cannot authorize table, metamethod, closure, or call assumptions without their matching guards.

Records are bounded and deterministic and contain no live state, closure, frame, or heap-object references. Invalid, oversized, or incompatible data fails closed and leaves normal planning intact. Hosts choose storage and retention policies appropriate to their deployment.
