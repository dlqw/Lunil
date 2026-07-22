# Automatic loop OSR behavior

With loop OSR enabled, the runtime may select it only for a verified natural loop that crosses its hotness threshold, has an automatically accepted exact-numeric plan, and enters at a canonical loop header. It never substitutes an unguarded managed approximation for a compiled loop.

Loops containing table/metamethod-dependent behavior, calls, yielding operations, unsupported control flow, or nonnumeric observations remain on canonical execution unless another documented guarded path applies. Rejection is normal optimization behavior, not a program error.

At every guard miss and scheduler boundary, execution resumes using the canonical PC and frame. Budget accounting, hooks, GC polling, invalidation, close handlers, errors, and coroutine transfers remain scheduler-owned. Hosts can disable OSR without changing Lua results.
