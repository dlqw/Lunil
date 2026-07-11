namespace Lunil.Syntax.Lexing;

public enum LuaTriviaKind : byte
{
    Whitespace,
    EndOfLine,
    Comment,
    LongComment,
    Utf8ByteOrderMark,
    Shebang,
}
