// Target Frameworks: net10.0
#nullable enable

namespace Lunil.Core
{
    public enum LuaChunkFormat
    {
        None = 0,
        Lua53 = 1,
        Lua54 = 2,
        Lua52 = 3,
        Lua51 = 4,
        Lua55 = 5
    }

    public enum LuaLanguageVersion
    {
        Lua51 = 81,
        Lua52 = 82,
        Lua53 = 83,
        Lua54 = 84,
        Lua55 = 85
    }

    public static class LuaLanguageVersions
    {
        public const Lunil.Core.LuaLanguageVersion Default = 84;
        public static bool IsKnown(Lunil.Core.LuaLanguageVersion version) => throw null;
        public static bool IsImplemented(Lunil.Core.LuaLanguageVersion version) => throw null;
        public static string GetDisplayName(Lunil.Core.LuaLanguageVersion version) => throw null;
        public static bool TryParse(string? value, out Lunil.Core.LuaLanguageVersion version) => throw null;
    }

    public static class LuaVersionFeatureTable
    {
        public static Lunil.Core.LuaVersionFeatures Get(Lunil.Core.LuaLanguageVersion version) => throw null;
    }

    public readonly struct LuaVersionFeatures : System.IEquatable<Lunil.Core.LuaVersionFeatures>
    {
        public bool IsImplemented { get => throw null; init { } }
        public Lunil.Core.LuaChunkFormat ChunkFormat { get => throw null; init { } }
        public bool SynchronousFinalizerErrors { get => throw null; init { } }
        public bool SupportsGenerationalCollection { get => throw null; init { } }
        public bool PreservesDeadThreadOpenUpvalues { get => throw null; init { } }
        public bool CachesClosuresByUpvalues { get => throw null; init { } }
        public bool HasWarnLibrary { get => throw null; init { } }
        public bool HasCoroutineClose { get => throw null; init { } }
        public bool HasUtf8Library { get => throw null; init { } }
        public bool HasBit32Library { get => throw null; init { } }
        public LuaVersionFeatures(bool IsImplemented, Lunil.Core.LuaChunkFormat ChunkFormat, bool SynchronousFinalizerErrors, bool SupportsGenerationalCollection, bool PreservesDeadThreadOpenUpvalues, bool CachesClosuresByUpvalues, bool HasWarnLibrary, bool HasCoroutineClose, bool HasUtf8Library, bool HasBit32Library) { }
        #nullable disable
        public override string ToString() => throw null;
        #nullable restore
        public static bool operator !=(Lunil.Core.LuaVersionFeatures left, Lunil.Core.LuaVersionFeatures right) => throw null;
        public static bool operator ==(Lunil.Core.LuaVersionFeatures left, Lunil.Core.LuaVersionFeatures right) => throw null;
        public override int GetHashCode() => throw null;
        #nullable disable
        public override bool Equals(object obj) => throw null;
        #nullable restore
        public bool Equals(Lunil.Core.LuaVersionFeatures other) => throw null;
        public void Deconstruct(out bool IsImplemented, out Lunil.Core.LuaChunkFormat ChunkFormat, out bool SynchronousFinalizerErrors, out bool SupportsGenerationalCollection, out bool PreservesDeadThreadOpenUpvalues, out bool CachesClosuresByUpvalues, out bool HasWarnLibrary, out bool HasCoroutineClose, out bool HasUtf8Library, out bool HasBit32Library) => throw null;
    }

    [System.AttributeUsage(System.AttributeTargets.Field, AllowMultiple = false)]
    public sealed class LuaVersionProfileAttribute : System.Attribute
    {
        public Lunil.Core.LuaChunkFormat ChunkFormat { get => throw null; init { } }
        public bool SynchronousFinalizerErrors { get => throw null; init { } }
        public bool SupportsGenerationalCollection { get => throw null; init { } }
        public bool PreservesDeadThreadOpenUpvalues { get => throw null; init { } }
        public bool CachesClosuresByUpvalues { get => throw null; init { } }
        public bool HasWarnLibrary { get => throw null; init { } }
        public bool HasCoroutineClose { get => throw null; init { } }
        public bool HasUtf8Library { get => throw null; init { } }
        public bool HasBit32Library { get => throw null; init { } }
    }
}
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
