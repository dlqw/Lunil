# Tier 2 automatic promotion contract

Automatic promotion is limited to a plan whose exact-numeric generated form is known before it is queued. A plan must contain a supported specialization and be completely representable by the numeric emitter. Imported profiles and custom compilers receive the same validation.

`EnableTier2` controls Tier 2 profiling, profile import, promotion, and prewarming together. A separate managed-fallback option controls broader profile-program behavior.

Every queued plan remains tied to verified IR and owner identity. Entry guards validate current state and deoptimize at the canonical PC on mismatch. Table, closure, metamethod, coroutine, and unwind-heavy paths use their documented guarded path or the canonical executor; numeric specialization is not a second Lua implementation.
