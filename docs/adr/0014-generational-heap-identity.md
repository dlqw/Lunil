# Generational heap identity and backend ownership

Lunil logical-heap identity is independent of CLR object lifetime. A heap object has a stable logical identity and semantic generation/version information that changes when relevant contents change. Guards use the appropriate identity and version; reference equality is not sufficient for mutable Lua objects.

Writes that can create references use the shared write barrier. Safepoints remain visible in generated paths, and weak tables, ephemerons, finalizers, and close behavior retain their Lua-defined ordering.

Caches may retain immutable descriptors and weak references, but must not root states, coroutines, closures, tables, metatables, or retired backend generations. A cache hit always revalidates its identity/version.
