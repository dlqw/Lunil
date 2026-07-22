# Lua version syntax and lexical boundaries

Source compatibility requires explicit lexer and parser gates rather than a Lua 5.4 default. The table records selected boundaries.

| Boundary | 5.1 | 5.2 | 5.3 | 5.4 | 5.5 |
| --- | --- | --- | --- | --- | --- |
| `goto` and labels | reject | accept | accept | accept | accept |
| Bitwise operators | reject | reject | accept | accept | accept |
| Floor division `//` | reject | reject | accept | accept | accept |
| Local attributes | reject | reject | reject | accept | accept |
| `global` declarations | identifier | identifier | identifier | identifier | accept |
| Named vararg | reject | reject | reject | reject | accept |

Version-gated constructs report stable parse or lexical diagnostics instead of silent rewrites. Numeric and string decoders enforce their own grammar under the selected version. Parse and runtime selection share the same `LuaLanguageVersion`, so a parsed module cannot execute in a differently-versioned state.
