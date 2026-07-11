using Luac.Core.Text;

namespace Luac.Syntax.Lexing;

public readonly record struct LuaSyntaxTrivia(LuaTriviaKind Kind, TextSpan Span);
