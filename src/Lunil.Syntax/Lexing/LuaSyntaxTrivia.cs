using Lunil.Core.Text;

namespace Lunil.Syntax.Lexing;

public readonly record struct LuaSyntaxTrivia(LuaTriviaKind Kind, TextSpan Span);
