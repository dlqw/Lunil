# Canonical IR execution instead of Lua AOT artifacts

Lunil uses canonical IR and a runtime-selected backend rather than distributing persisted or static Lua AOT artifacts. Canonical IR has one verifier, scheduler boundary, accounting model, and language-version identity.

Hosts select managed execution or an available dynamic-code backend. NativeAOT and trimmed deployments retain the managed semantic path. Generated code is an implementation detail beneath the same hosting API.

Source and binary chunks are validated against their selected Lua version and lowered to canonical IR. Backend caches invalidate when IR or ABI identity changes. Integrations should store source, bytecode, or canonical module inputs under their own compatibility policy, not backend-generated executables.
