# Idle module reload and invalidation

An idle module can be replaced after new source or canonical IR has been validated and assigned a new content identity. Publication is atomic: readers use either the old module identity or the new identity, never a mixture.

Compiled entries, direct-call bindings, inline caches, and profiles bind to module and function generations. A new publication invalidates or bypasses entries whose identity no longer matches. Weak ownership prevents optimization caches from retaining retired modules.

A running frame continues under its established code identity until a documented scheduler boundary or explicit host update policy applies. Reload does not mutate an instruction already in progress. Hosts should surface explicit module identities and diagnostics rather than model reload as arbitrary in-place closure mutation.
