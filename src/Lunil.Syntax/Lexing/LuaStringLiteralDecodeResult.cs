using System.Collections.Immutable;
using Lunil.Core.Diagnostics;

namespace Lunil.Syntax.Lexing;

public sealed record LuaStringLiteralDecodeResult(
    LuaStringTokenValue Value,
    ImmutableArray<Diagnostic> Diagnostics);
