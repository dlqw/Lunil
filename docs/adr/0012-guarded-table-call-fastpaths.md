# Guarded table and call fast paths

Table access can be accelerated only when guards validate table and key identity, storage/shape version, metatable state, and the relevant existing-entry condition. A mismatch invokes the generic operation from the same canonical PC, preserving metamethods, rehashing, and Lua errors.

A direct call guards module/function identity, generation, closure shape, argument/result window, and entry state. It is limited to a supported non-yielding callee shape; other calls create and schedule the canonical callee frame.

Inline-cache entries are bounded and weakly owned. Reload, function publication, metatable changes, and invalidation prevent stale use. Profiles are hints, never proofs; all guards pass before a Lua-visible fast-path effect commits.
