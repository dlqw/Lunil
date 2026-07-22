# Generation-bound direct compiled calls

A compiled site may directly invoke a supported Lua closure only when generation-bound guards prove that it is still the function that was planned. The binding checks module content identity, function identity, function generation, closure shape, fixed argument/result shape, and weak cache entries.

The direct form is limited to non-yielding callees with a supported fixed shape: constants, moves, integer operations, branches, numeric loops, and fixed returns. Varargs, unsupported upvalues, open results, native continuations, metamethods, errors, and yields use canonical calls.

The entry shares frame conventions and canonical accounting. It commits no callee-visible mutation before guards pass and returns the caller at its canonical continuation. Reload or function publication changes the generation and invalidates the binding.
