using System.Collections.Immutable;
using Luac.Core.Diagnostics;
using Luac.Core.Text;

namespace Luac.Syntax.Lexing;

/// <summary>A lossless, byte-oriented lexer for Lua 5.4 source chunks.</summary>
public static class LuaLexer
{
    public static LuaLexResult Lex(SourceText source, LuaLexerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= LuaLexerOptions.Default;
        ValidateOptions(options);

        var implementation = new Implementation(source, options);
        return implementation.Lex();
    }

    private static void ValidateOptions(LuaLexerOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumTokenCount, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDiagnosticCount);
    }

    private sealed class Implementation
    {
        private readonly SourceText _source;
        private readonly LuaLexerOptions _options;
        private readonly ImmutableArray<LuaSyntaxToken>.Builder _tokens =
            ImmutableArray.CreateBuilder<LuaSyntaxToken>();
        private readonly ImmutableArray<Diagnostic>.Builder _diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        private int _position;
        private bool _canReadShebang = true;

        public Implementation(SourceText source, LuaLexerOptions options)
        {
            _source = source;
            _options = options;
        }

        public LuaLexResult Lex()
        {
            while (true)
            {
                var leadingTrivia = ReadLeadingTrivia();
                if (_position == _source.Length)
                {
                    _tokens.Add(new LuaSyntaxToken(
                        LuaTokenKind.EndOfFile,
                        new TextSpan(_position, 0),
                        leadingTrivia));
                    break;
                }

                if (_tokens.Count >= _options.MaximumTokenCount - 1)
                {
                    AddDiagnostic(
                        "LUA1007",
                        new TextSpan(_position, _source.Length - _position),
                        $"Token count exceeds the configured {_options.MaximumTokenCount} limit.");
                    _position = _source.Length;
                    _tokens.Add(new LuaSyntaxToken(
                        LuaTokenKind.EndOfFile,
                        new TextSpan(_position, 0),
                        leadingTrivia));
                    break;
                }

                _canReadShebang = false;
                var start = _position;
                var kind = ReadToken();
                if (_position <= start)
                {
                    throw new InvalidOperationException("The Lua lexer failed to make progress.");
                }

                var token = new LuaSyntaxToken(
                    kind,
                    TextSpan.FromBounds(start, _position),
                    leadingTrivia);

                if (kind is LuaTokenKind.StringLiteral or LuaTokenKind.LongStringLiteral)
                {
                    var decoded = LuaStringLiteralDecoder.Decode(_source, token);
                    token = token with { Value = decoded.Value };
                    foreach (var diagnostic in decoded.Diagnostics)
                    {
                        AddDiagnostic(diagnostic);
                    }
                }
                else if (kind == LuaTokenKind.NumericLiteral &&
                         LuaNumericLiteralDecoder.TryDecode(_source.GetSpan(token.Span), out var number))
                {
                    token = token with { Value = number };
                }

                _tokens.Add(token);
            }

            return new LuaLexResult(
                _source,
                _tokens.ToImmutable(),
                _diagnostics.ToImmutable());
        }

        private ImmutableArray<LuaSyntaxTrivia> ReadLeadingTrivia()
        {
            var trivia = ImmutableArray.CreateBuilder<LuaSyntaxTrivia>();
            while (_position < _source.Length)
            {
                var start = _position;

                if (_position == 0 &&
                    _options.AcceptUtf8ByteOrderMark &&
                    Current == 0xef && Peek(1) == 0xbb && Peek(2) == 0xbf)
                {
                    _position += 3;
                    trivia.Add(CreateTrivia(LuaTriviaKind.Utf8ByteOrderMark, start));
                    continue;
                }

                if (_canReadShebang && _options.AcceptShebang && Current == (byte)'#')
                {
                    while (_position < _source.Length && !IsNewLine(Current))
                    {
                        _position++;
                    }

                    _canReadShebang = false;
                    trivia.Add(CreateTrivia(LuaTriviaKind.Shebang, start));
                    continue;
                }

                if (IsHorizontalWhitespace(Current))
                {
                    do
                    {
                        _position++;
                    }
                    while (_position < _source.Length && IsHorizontalWhitespace(Current));

                    _canReadShebang = false;
                    trivia.Add(CreateTrivia(LuaTriviaKind.Whitespace, start));
                    continue;
                }

                if (IsNewLine(Current))
                {
                    ReadNewLine();
                    _canReadShebang = false;
                    trivia.Add(CreateTrivia(LuaTriviaKind.EndOfLine, start));
                    continue;
                }

                if (Current == (byte)'-' && Peek(1) == (byte)'-')
                {
                    _position += 2;
                    if (TryReadLongBracketOpener(_position, out var equalsCount, out var openerLength))
                    {
                        _position += openerLength;
                        ReadLongBracketBody(
                            equalsCount,
                            start,
                            "LUA1005",
                            "Unterminated long comment.");
                        trivia.Add(CreateTrivia(LuaTriviaKind.LongComment, start));
                    }
                    else
                    {
                        while (_position < _source.Length && !IsNewLine(Current))
                        {
                            _position++;
                        }

                        trivia.Add(CreateTrivia(LuaTriviaKind.Comment, start));
                    }

                    _canReadShebang = false;
                    continue;
                }

                break;
            }

            return trivia.ToImmutable();
        }

        private LuaTokenKind ReadToken()
        {
            if (IsIdentifierStart(Current))
            {
                return ReadIdentifierOrKeyword();
            }

            if (IsDecimalDigit(Current) || Current == (byte)'.' && IsDecimalDigit(Peek(1)))
            {
                return ReadNumericLiteral();
            }

            if (Current is (byte)'\'' or (byte)'"')
            {
                return ReadQuotedString();
            }

            if (Current == (byte)'[' &&
                TryReadLongBracketOpener(_position, out var equalsCount, out var openerLength))
            {
                var start = _position;
                _position += openerLength;
                ReadLongBracketBody(
                    equalsCount,
                    start,
                    "LUA1004",
                    "Unterminated long string literal.");
                return LuaTokenKind.LongStringLiteral;
            }

            if (Current == (byte)'[' && Peek(1) == (byte)'=')
            {
                var start = _position;
                _position += 2;
                while (_position < _source.Length && Current == (byte)'=')
                {
                    _position++;
                }

                AddDiagnostic(
                    "LUA1008",
                    TextSpan.FromBounds(start, _position),
                    "Invalid long string delimiter.");
                return LuaTokenKind.BadToken;
            }

            return Current switch
            {
                (byte)'+' => ReadOne(LuaTokenKind.Plus),
                (byte)'-' => ReadOne(LuaTokenKind.Minus),
                (byte)'*' => ReadOne(LuaTokenKind.Star),
                (byte)'/' when Peek(1) == (byte)'/' => ReadTwo(LuaTokenKind.FloorDivide),
                (byte)'/' => ReadOne(LuaTokenKind.Slash),
                (byte)'%' => ReadOne(LuaTokenKind.Percent),
                (byte)'^' => ReadOne(LuaTokenKind.Caret),
                (byte)'#' => ReadOne(LuaTokenKind.Length),
                (byte)'&' => ReadOne(LuaTokenKind.Ampersand),
                (byte)'~' when Peek(1) == (byte)'=' => ReadTwo(LuaTokenKind.NotEqual),
                (byte)'~' => ReadOne(LuaTokenKind.Tilde),
                (byte)'|' => ReadOne(LuaTokenKind.Pipe),
                (byte)'<' when Peek(1) == (byte)'<' => ReadTwo(LuaTokenKind.ShiftLeft),
                (byte)'<' when Peek(1) == (byte)'=' => ReadTwo(LuaTokenKind.LessThanOrEqual),
                (byte)'<' => ReadOne(LuaTokenKind.LessThan),
                (byte)'>' when Peek(1) == (byte)'>' => ReadTwo(LuaTokenKind.ShiftRight),
                (byte)'>' when Peek(1) == (byte)'=' => ReadTwo(LuaTokenKind.GreaterThanOrEqual),
                (byte)'>' => ReadOne(LuaTokenKind.GreaterThan),
                (byte)'=' when Peek(1) == (byte)'=' => ReadTwo(LuaTokenKind.Equal),
                (byte)'=' => ReadOne(LuaTokenKind.Assign),
                (byte)'(' => ReadOne(LuaTokenKind.OpenParenthesis),
                (byte)')' => ReadOne(LuaTokenKind.CloseParenthesis),
                (byte)'{' => ReadOne(LuaTokenKind.OpenBrace),
                (byte)'}' => ReadOne(LuaTokenKind.CloseBrace),
                (byte)'[' => ReadOne(LuaTokenKind.OpenBracket),
                (byte)']' => ReadOne(LuaTokenKind.CloseBracket),
                (byte)':' when Peek(1) == (byte)':' => ReadTwo(LuaTokenKind.DoubleColon),
                (byte)':' => ReadOne(LuaTokenKind.Colon),
                (byte)';' => ReadOne(LuaTokenKind.Semicolon),
                (byte)',' => ReadOne(LuaTokenKind.Comma),
                (byte)'.' when Peek(1) == (byte)'.' && Peek(2) == (byte)'.' =>
                    ReadThree(LuaTokenKind.VarArg),
                (byte)'.' when Peek(1) == (byte)'.' => ReadTwo(LuaTokenKind.Concatenate),
                (byte)'.' => ReadOne(LuaTokenKind.Dot),
                _ => ReadBadToken(),
            };
        }

        private LuaTokenKind ReadIdentifierOrKeyword()
        {
            var start = _position++;
            while (_position < _source.Length && IsIdentifierPart(Current))
            {
                _position++;
            }

            return LuaSyntaxFacts.GetIdentifierOrKeywordKind(
                _source.GetSpan(TextSpan.FromBounds(start, _position)));
        }

        private LuaTokenKind ReadNumericLiteral()
        {
            var start = _position;
            var first = Current;
            _position++;

            var hexadecimal = first == (byte)'0' && Current is (byte)'x' or (byte)'X';
            if (hexadecimal)
            {
                _position++;
            }

            var exponentLower = hexadecimal ? (byte)'p' : (byte)'e';
            var exponentUpper = hexadecimal ? (byte)'P' : (byte)'E';

            while (_position < _source.Length)
            {
                if (Current == exponentLower || Current == exponentUpper)
                {
                    _position++;
                    if (_position < _source.Length && Current is (byte)'+' or (byte)'-')
                    {
                        _position++;
                    }

                    continue;
                }

                if (IsHexadecimalDigit(Current) || Current == (byte)'.')
                {
                    _position++;
                    continue;
                }

                break;
            }

            // PUC Lua consumes one alphabetic byte after a numeral to force a
            // useful malformed-number diagnostic (for example, "123x").
            if (_position < _source.Length && IsIdentifierStart(Current))
            {
                _position++;
            }

            var span = TextSpan.FromBounds(start, _position);
            if (!LuaNumericLiteralValidator.IsValid(_source.GetSpan(span)))
            {
                AddDiagnostic("LUA1006", span, "Malformed numeric literal.");
            }

            return LuaTokenKind.NumericLiteral;
        }

        private LuaTokenKind ReadQuotedString()
        {
            var start = _position;
            var quote = Current;
            _position++;

            while (_position < _source.Length)
            {
                if (Current == quote)
                {
                    _position++;
                    return LuaTokenKind.StringLiteral;
                }

                if (IsNewLine(Current))
                {
                    AddDiagnostic(
                        "LUA1003",
                        TextSpan.FromBounds(start, _position),
                        "Unfinished string literal before the end of the line.");
                    return LuaTokenKind.StringLiteral;
                }

                if (Current != (byte)'\\')
                {
                    _position++;
                    continue;
                }

                _position++;
                if (_position == _source.Length)
                {
                    break;
                }

                if (Current == (byte)'z')
                {
                    _position++;
                    while (_position < _source.Length && IsLuaWhitespace(Current))
                    {
                        if (IsNewLine(Current))
                        {
                            ReadNewLine();
                        }
                        else
                        {
                            _position++;
                        }
                    }

                    continue;
                }

                if (IsNewLine(Current))
                {
                    ReadNewLine();
                }
                else
                {
                    _position++;
                }
            }

            AddDiagnostic(
                "LUA1002",
                TextSpan.FromBounds(start, _position),
                "Unterminated string literal.");
            return LuaTokenKind.StringLiteral;
        }

        private void ReadLongBracketBody(
            int equalsCount,
            int start,
            string diagnosticCode,
            string diagnosticMessage)
        {
            while (_position < _source.Length)
            {
                if (Current == (byte)']' && IsLongBracketCloser(_position, equalsCount))
                {
                    _position += equalsCount + 2;
                    return;
                }

                _position++;
            }

            AddDiagnostic(
                diagnosticCode,
                TextSpan.FromBounds(start, _position),
                diagnosticMessage);
        }

        private bool TryReadLongBracketOpener(
            int position,
            out int equalsCount,
            out int openerLength)
        {
            equalsCount = 0;
            openerLength = 0;
            if (ByteAt(position) != (byte)'[')
            {
                return false;
            }

            var cursor = position + 1;
            while (ByteAt(cursor) == (byte)'=')
            {
                equalsCount++;
                cursor++;
            }

            if (ByteAt(cursor) != (byte)'[')
            {
                equalsCount = 0;
                return false;
            }

            openerLength = cursor - position + 1;
            return true;
        }

        private bool IsLongBracketCloser(int position, int equalsCount)
        {
            var cursor = position + 1;
            for (var index = 0; index < equalsCount; index++)
            {
                if (ByteAt(cursor++) != (byte)'=')
                {
                    return false;
                }
            }

            return ByteAt(cursor) == (byte)']';
        }

        private void ReadNewLine()
        {
            var first = Current;
            if (Peek(1) is (byte)'\r' or (byte)'\n' && Peek(1) != first)
            {
                _position += 2;
            }
            else
            {
                _position++;
            }
        }

        private LuaTokenKind ReadOne(LuaTokenKind kind)
        {
            _position++;
            return kind;
        }

        private LuaTokenKind ReadTwo(LuaTokenKind kind)
        {
            _position += 2;
            return kind;
        }

        private LuaTokenKind ReadThree(LuaTokenKind kind)
        {
            _position += 3;
            return kind;
        }

        private LuaTokenKind ReadBadToken()
        {
            var start = _position++;
            AddDiagnostic(
                "LUA1001",
                new TextSpan(start, 1),
                $"Unexpected source byte 0x{_source.AsSpan()[start]:X2}.");
            return LuaTokenKind.BadToken;
        }

        private LuaSyntaxTrivia CreateTrivia(LuaTriviaKind kind, int start) =>
            new(kind, TextSpan.FromBounds(start, _position));

        private void AddDiagnostic(string code, TextSpan span, string message)
        {
            if (_diagnostics.Count < _options.MaximumDiagnosticCount)
            {
                _diagnostics.Add(new Diagnostic(code, DiagnosticSeverity.Error, span, message));
            }
        }

        private void AddDiagnostic(Diagnostic diagnostic)
        {
            if (_diagnostics.Count < _options.MaximumDiagnosticCount)
            {
                _diagnostics.Add(diagnostic);
            }
        }

        private byte Current => ByteAt(_position);

        private byte Peek(int offset) => ByteAt(_position + offset);

        private byte ByteAt(int position) =>
            (uint)position < (uint)_source.Length ? _source.AsSpan()[position] : (byte)0;

        private static bool IsIdentifierStart(byte value) =>
            value is (byte)'_' or >= (byte)'a' and <= (byte)'z' or >= (byte)'A' and <= (byte)'Z';

        private static bool IsIdentifierPart(byte value) =>
            IsIdentifierStart(value) || IsDecimalDigit(value);

        private static bool IsDecimalDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

        private static bool IsHexadecimalDigit(byte value) =>
            IsDecimalDigit(value) ||
            value is >= (byte)'a' and <= (byte)'f' or >= (byte)'A' and <= (byte)'F';

        private static bool IsHorizontalWhitespace(byte value) =>
            value is (byte)' ' or (byte)'\t' or (byte)'\v' or (byte)'\f';

        private static bool IsNewLine(byte value) => value is (byte)'\r' or (byte)'\n';

        private static bool IsLuaWhitespace(byte value) =>
            IsHorizontalWhitespace(value) || IsNewLine(value);
    }
}
