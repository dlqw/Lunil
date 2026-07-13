using System.Collections.Immutable;
using System.Text;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;

namespace Lunil.EmmyLua;

/// <summary>A shared bounded lexer for LuaLS and legacy EmmyLua annotation payloads.</summary>
public static class LuaAnnotationLexer
{
    public static LuaAnnotationLexResult Lex(
        SourceText source,
        TextSpan span,
        LuaAnnotationOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        options ??= LuaAnnotationOptions.Default;
        ValidateOptions(options);
        if (span.End > source.Length)
        {
            throw new ArgumentOutOfRangeException(nameof(span));
        }

        return Lex(source, span, options, options.MaximumDiagnosticCount);
    }

    internal static LuaAnnotationLexResult Lex(
        SourceText source,
        TextSpan span,
        LuaAnnotationOptions options,
        int maximumDiagnosticCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(maximumDiagnosticCount);
        return new Implementation(source, span, options, maximumDiagnosticCount).Lex();
    }

    internal static void ValidateOptions(LuaAnnotationOptions options)
    {
        if (!Enum.IsDefined(options.Dialect))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The annotation dialect is invalid.");
        }

        if (!Enum.IsDefined(options.SyntaxDiagnosticSeverity))
        {
            throw new ArgumentOutOfRangeException(
                nameof(options),
                "The annotation diagnostic severity is invalid.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumAnnotationCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaximumTokensPerAnnotation, 2);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumTypeDepth);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumDiagnosticCount);
        ArgumentNullException.ThrowIfNull(options.SuppressedDiagnosticCodes);
    }

    private sealed class Implementation
    {
        private readonly SourceText _source;
        private readonly TextSpan _span;
        private readonly LuaAnnotationOptions _options;
        private readonly int _maximumDiagnosticCount;
        private readonly ImmutableArray<LuaAnnotationToken>.Builder _tokens =
            ImmutableArray.CreateBuilder<LuaAnnotationToken>();
        private readonly ImmutableArray<Diagnostic>.Builder _diagnostics =
            ImmutableArray.CreateBuilder<Diagnostic>();
        private int _position;
        private int _errorCount;

        public Implementation(
            SourceText source,
            TextSpan span,
            LuaAnnotationOptions options,
            int maximumDiagnosticCount)
        {
            _source = source;
            _span = span;
            _options = options;
            _maximumDiagnosticCount = maximumDiagnosticCount;
            _position = span.Start;
        }

        public LuaAnnotationLexResult Lex()
        {
            while (true)
            {
                SkipWhitespace();
                if (_position >= _span.End)
                {
                    _tokens.Add(new LuaAnnotationToken(
                        LuaAnnotationTokenKind.EndOfFile,
                        new TextSpan(_position, 0),
                        string.Empty));
                    break;
                }

                if (_tokens.Count >= _options.MaximumTokensPerAnnotation - 1)
                {
                    AddDiagnostic(
                        "LUA5004",
                        TextSpan.FromBounds(_position, _span.End),
                        $"Annotation token count exceeds the configured " +
                        $"{_options.MaximumTokensPerAnnotation} limit.");
                    _position = _span.End;
                    continue;
                }

                var start = _position;
                var kind = ReadToken();
                var tokenSpan = TextSpan.FromBounds(start, _position);
                _tokens.Add(new LuaAnnotationToken(
                    kind,
                    tokenSpan,
                    Decode(_source.GetSpan(tokenSpan))));
            }

            return new LuaAnnotationLexResult(
                _span,
                _tokens.ToImmutable(),
                _diagnostics.ToImmutable(),
                _errorCount);
        }

        private LuaAnnotationTokenKind ReadToken()
        {
            var current = Current;
            if (IsIdentifierStart(current))
            {
                _position++;
                while (_position < _span.End && IsIdentifierPart(Current))
                {
                    _position++;
                }

                return LuaAnnotationTokenKind.Identifier;
            }

            if (IsDigit(current))
            {
                _position++;
                while (_position < _span.End &&
                       (IsDigit(Current) || Current is (byte)'.' or (byte)'x' or (byte)'X' or
                           (byte)'a' or (byte)'b' or (byte)'c' or (byte)'d' or (byte)'e' or
                           (byte)'f' or (byte)'A' or (byte)'B' or (byte)'C' or (byte)'D' or
                           (byte)'E' or (byte)'F' or (byte)'+' or (byte)'-'))
                {
                    _position++;
                }

                return LuaAnnotationTokenKind.NumericLiteral;
            }

            if (current is (byte)'\'' or (byte)'"')
            {
                return ReadString(current);
            }

            return current switch
            {
                (byte)'@' => ReadOne(LuaAnnotationTokenKind.At),
                (byte)':' => ReadOne(LuaAnnotationTokenKind.Colon),
                (byte)',' => ReadOne(LuaAnnotationTokenKind.Comma),
                (byte)'.' when Peek(1) == (byte)'.' && Peek(2) == (byte)'.' =>
                    ReadThree(LuaAnnotationTokenKind.Ellipsis),
                (byte)'.' => ReadOne(LuaAnnotationTokenKind.Dot),
                (byte)'|' => ReadOne(LuaAnnotationTokenKind.Pipe),
                (byte)'&' => ReadOne(LuaAnnotationTokenKind.Ampersand),
                (byte)'?' => ReadOne(LuaAnnotationTokenKind.Question),
                (byte)'(' => ReadOne(LuaAnnotationTokenKind.OpenParenthesis),
                (byte)')' => ReadOne(LuaAnnotationTokenKind.CloseParenthesis),
                (byte)'{' => ReadOne(LuaAnnotationTokenKind.OpenBrace),
                (byte)'}' => ReadOne(LuaAnnotationTokenKind.CloseBrace),
                (byte)'[' => ReadOne(LuaAnnotationTokenKind.OpenBracket),
                (byte)']' => ReadOne(LuaAnnotationTokenKind.CloseBracket),
                (byte)'<' => ReadOne(LuaAnnotationTokenKind.LessThan),
                (byte)'>' => ReadOne(LuaAnnotationTokenKind.GreaterThan),
                (byte)'=' => ReadOne(LuaAnnotationTokenKind.Assign),
                (byte)'+' => ReadOne(LuaAnnotationTokenKind.Plus),
                (byte)'-' => ReadOne(LuaAnnotationTokenKind.Minus),
                (byte)'*' => ReadOne(LuaAnnotationTokenKind.Star),
                (byte)'#' => ReadOne(LuaAnnotationTokenKind.Hash),
                _ => ReadBadToken(),
            };
        }

        private LuaAnnotationTokenKind ReadString(byte quote)
        {
            var start = _position++;
            while (_position < _span.End)
            {
                if (Current == quote)
                {
                    _position++;
                    return LuaAnnotationTokenKind.StringLiteral;
                }

                if (Current == (byte)'\\' && _position + 1 < _span.End)
                {
                    _position += 2;
                }
                else
                {
                    _position++;
                }
            }

            AddDiagnostic(
                "LUA5003",
                TextSpan.FromBounds(start, _position),
                "Unterminated annotation string literal.");
            return LuaAnnotationTokenKind.StringLiteral;
        }

        private LuaAnnotationTokenKind ReadOne(LuaAnnotationTokenKind kind)
        {
            _position++;
            return kind;
        }

        private LuaAnnotationTokenKind ReadThree(LuaAnnotationTokenKind kind)
        {
            _position += 3;
            return kind;
        }

        private LuaAnnotationTokenKind ReadBadToken()
        {
            var start = _position++;
            AddDiagnostic(
                "LUA5003",
                new TextSpan(start, 1),
                $"Unexpected annotation byte 0x{_source.AsSpan()[start]:X2}.");
            return LuaAnnotationTokenKind.BadToken;
        }

        private void SkipWhitespace()
        {
            while (_position < _span.End && Current is (byte)' ' or (byte)'\t')
            {
                _position++;
            }
        }

        private void AddDiagnostic(string code, TextSpan span, string message)
        {
            _errorCount++;
            if (_diagnostics.Count < _maximumDiagnosticCount)
            {
                _diagnostics.Add(new Diagnostic(
                    code,
                    _options.SyntaxDiagnosticSeverity,
                    span,
                    message));
            }
        }

        private byte Current => ByteAt(_position);

        private byte Peek(int offset) => ByteAt(_position + offset);

        private byte ByteAt(int position) =>
            position >= _span.Start && position < _span.End
                ? _source.AsSpan()[position]
                : (byte)0;

        private static bool IsIdentifierStart(byte value) =>
            value is (byte)'_' or >= (byte)'a' and <= (byte)'z' or
                >= (byte)'A' and <= (byte)'Z' or >= 0x80;

        private static bool IsIdentifierPart(byte value) =>
            IsIdentifierStart(value) || IsDigit(value) || value == (byte)'-';

        private static bool IsDigit(byte value) => value is >= (byte)'0' and <= (byte)'9';

        private static string Decode(ReadOnlySpan<byte> bytes) => Encoding.UTF8.GetString(bytes);
    }
}
