// Target Frameworks: net10.0
#nullable enable

namespace Lunil.Core.Diagnostics
{
    public sealed class Diagnostic : System.IEquatable<Lunil.Core.Diagnostics.Diagnostic>
    {
        public string Code { get => throw null; }
        public Lunil.Core.Diagnostics.DiagnosticSeverity Severity { get => throw null; }
        public Lunil.Core.Text.TextSpan Span { get => throw null; }
        public string Message { get => throw null; }
        public Diagnostic(string code, Lunil.Core.Diagnostics.DiagnosticSeverity severity, Lunil.Core.Text.TextSpan span, string message) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Core.Diagnostics.Diagnostic? left, Lunil.Core.Diagnostics.Diagnostic? right) => throw null;
        public static bool operator ==(Lunil.Core.Diagnostics.Diagnostic? left, Lunil.Core.Diagnostics.Diagnostic? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Core.Diagnostics.Diagnostic? other) => throw null;
    }

    public enum DiagnosticSeverity
    {
        Hidden = 0,
        Information = 1,
        Warning = 2,
        Error = 3
    }
}
namespace Lunil.Core.Numerics
{
    public readonly struct LuaNumber : System.IEquatable<Lunil.Core.Numerics.LuaNumber>
    {
        public Lunil.Core.Numerics.LuaNumberKind Kind { get => throw null; }
        public long Integer { get => throw null; }
        public double Float { get => throw null; }
        public static Lunil.Core.Numerics.LuaNumber FromInteger(long value) => throw null;
        public static Lunil.Core.Numerics.LuaNumber FromFloat(double value) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.Core.Numerics.LuaNumber left, Lunil.Core.Numerics.LuaNumber right) => throw null;
        public static bool operator ==(Lunil.Core.Numerics.LuaNumber left, Lunil.Core.Numerics.LuaNumber right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.Core.Numerics.LuaNumber other) => throw null;
    }

    public enum LuaNumberKind
    {
        Integer = 0,
        Float = 1
    }

    public static class LuaNumberParser
    {
        public static bool TryParseLiteral(System.ReadOnlySpan<byte> text, out Lunil.Core.Numerics.LuaNumber value) => throw null;
        public static bool TryParseString(System.ReadOnlySpan<byte> text, out Lunil.Core.Numerics.LuaNumber value) => throw null;
    }
}
namespace Lunil.Core.Text
{
    public readonly struct SourceLocation : System.IEquatable<Lunil.Core.Text.SourceLocation>
    {
        public int ByteOffset { get => throw null; init { } }
        public int Line { get => throw null; init { } }
        public int ByteColumn { get => throw null; init { } }
        public int Utf16Column { get => throw null; init { } }
        public SourceLocation(int ByteOffset, int Line, int ByteColumn, int Utf16Column) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.Core.Text.SourceLocation left, Lunil.Core.Text.SourceLocation right) => throw null;
        public static bool operator ==(Lunil.Core.Text.SourceLocation left, Lunil.Core.Text.SourceLocation right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.Core.Text.SourceLocation other) => throw null;
        public void Deconstruct(out int ByteOffset, out int Line, out int ByteColumn, out int Utf16Column) => throw null;
    }

    public sealed class SourceText
    {
        public int Length { get => throw null; }
        public int LineCount { get => throw null; }
        public SourceText(System.ReadOnlySpan<byte> bytes) { }
        public static Lunil.Core.Text.SourceText FromUtf8(string text) => throw null;
        public System.ReadOnlySpan<byte> AsSpan() => throw null;
        public System.ReadOnlySpan<byte> GetSpan(Lunil.Core.Text.TextSpan span) => throw null;
        public byte[] ToArray() => throw null;
        public Lunil.Core.Text.TextSpan GetLineSpan(int line) => throw null;
        public Lunil.Core.Text.SourceLocation GetLocation(int byteOffset) => throw null;
    }

    public readonly struct TextSpan : System.IEquatable<Lunil.Core.Text.TextSpan>
    {
        public int Start { get => throw null; }
        public int Length { get => throw null; }
        public int End { get => throw null; }
        public TextSpan(int start, int length) { }
        public static Lunil.Core.Text.TextSpan FromBounds(int start, int end) => throw null;
        public bool Contains(int byteOffset) => throw null;
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.Core.Text.TextSpan left, Lunil.Core.Text.TextSpan right) => throw null;
        public static bool operator ==(Lunil.Core.Text.TextSpan left, Lunil.Core.Text.TextSpan right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.Core.Text.TextSpan other) => throw null;
    }
}
