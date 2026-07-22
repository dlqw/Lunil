# Versioned invalidation and function slots

Function slots are stable indirection points between call sites and replaceable implementations. A slot has a stable identity and a monotonically changing implementation generation. Call sites read a coherent entry and generation.

A compiled binding is valid only while module identity, function identity, generation, and entry guards agree. Publishing a replacement creates a new implementation identity and updates the slot atomically. Cancellation and errors never publish a partial entry.

Registries and call caches use bounded weak entries and cannot retain states, modules, closures, retired entries, or compilation tasks unnecessarily. A generation mismatch exits at a canonical PC, then resolves the current slot through normal Lua call semantics.
