namespace Lunil.Syntax.Lexing;

public static class LuaSyntaxFacts
{
    public static LuaTokenKind GetIdentifierOrKeywordKind(ReadOnlySpan<byte> text) =>
        text.Length switch
        {
            2 when text.SequenceEqual("do"u8) => LuaTokenKind.DoKeyword,
            2 when text.SequenceEqual("if"u8) => LuaTokenKind.IfKeyword,
            2 when text.SequenceEqual("in"u8) => LuaTokenKind.InKeyword,
            2 when text.SequenceEqual("or"u8) => LuaTokenKind.OrKeyword,
            3 when text.SequenceEqual("and"u8) => LuaTokenKind.AndKeyword,
            3 when text.SequenceEqual("end"u8) => LuaTokenKind.EndKeyword,
            3 when text.SequenceEqual("for"u8) => LuaTokenKind.ForKeyword,
            3 when text.SequenceEqual("nil"u8) => LuaTokenKind.NilKeyword,
            3 when text.SequenceEqual("not"u8) => LuaTokenKind.NotKeyword,
            4 when text.SequenceEqual("else"u8) => LuaTokenKind.ElseKeyword,
            4 when text.SequenceEqual("goto"u8) => LuaTokenKind.GotoKeyword,
            4 when text.SequenceEqual("then"u8) => LuaTokenKind.ThenKeyword,
            4 when text.SequenceEqual("true"u8) => LuaTokenKind.TrueKeyword,
            5 when text.SequenceEqual("break"u8) => LuaTokenKind.BreakKeyword,
            5 when text.SequenceEqual("false"u8) => LuaTokenKind.FalseKeyword,
            5 when text.SequenceEqual("local"u8) => LuaTokenKind.LocalKeyword,
            5 when text.SequenceEqual("until"u8) => LuaTokenKind.UntilKeyword,
            5 when text.SequenceEqual("while"u8) => LuaTokenKind.WhileKeyword,
            6 when text.SequenceEqual("elseif"u8) => LuaTokenKind.ElseIfKeyword,
            6 when text.SequenceEqual("repeat"u8) => LuaTokenKind.RepeatKeyword,
            6 when text.SequenceEqual("return"u8) => LuaTokenKind.ReturnKeyword,
            8 when text.SequenceEqual("function"u8) => LuaTokenKind.FunctionKeyword,
            _ => LuaTokenKind.Identifier,
        };

    public static bool IsKeyword(LuaTokenKind kind) =>
        kind is >= LuaTokenKind.AndKeyword and <= LuaTokenKind.WhileKeyword;
}
