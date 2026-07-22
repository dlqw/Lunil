# Bounded table and operand polymorphic inline caches

Polymorphic inline caches (PICs) accelerate a small number of recurring table and primitive operand shapes without treating observations as permanent Lua facts. Each site holds a bounded set of weak entries.

Array-key entries guard table identity, integer key shape, storage/shape version, and metatable state. String-field entries also guard interned string identity and an existing-entry handle. Primitive operands guard exact value kinds.

A miss runs the generic operation at the original canonical PC, preserving rehashing, metamethods, equality, relations, and errors. Version guards prevent stale use, bounded replacement controls memory, and entries cannot own states, heap objects, or retired backend generations.
