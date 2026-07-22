# Lua language-version contract

Lua language versions are separate source, binary, standard-library, and runtime contracts. `LuaLanguageVersion` is selected through compiler, workspace, host, CLI, and environment configuration; Lua 5.4 is the default when no explicit selection is made.

The identity flows through lexing, parsing, binding, compilation results, canonical IR, runtime state, and execution context. It participates in module, profile, and artifact compatibility.

A state rejects a module or chunk from another selected version. Readers and library installation dispatch by language identity rather than a display string or best-effort format guess. Unsupported behavior fails with stable diagnostics and is never borrowed from the default version.
