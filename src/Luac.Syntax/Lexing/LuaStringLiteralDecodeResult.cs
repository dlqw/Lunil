using System.Collections.Immutable;
using Luac.Core.Diagnostics;

namespace Luac.Syntax.Lexing;

public sealed record LuaStringLiteralDecodeResult(
    LuaStringTokenValue Value,
    ImmutableArray<Diagnostic> Diagnostics);
