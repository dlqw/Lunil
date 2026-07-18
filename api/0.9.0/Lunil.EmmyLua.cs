// Target Frameworks: net10.0
#nullable enable

namespace Lunil.EmmyLua
{
    public static class AnnotationCompatibilityResolver
    {
        public static Lunil.EmmyLua.LuaAnnotationDocument Parse(Lunil.Syntax.Lexing.LuaLexResult lexing, Lunil.EmmyLua.LuaAnnotationOptions? options = null) => throw null;
    }

    public static class LegacyEmmyAnnotationParser
    {
        public static Lunil.EmmyLua.LuaAnnotationDocument Parse(Lunil.Syntax.Lexing.LuaLexResult lexing, Lunil.EmmyLua.LuaAnnotationOptions? options = null) => throw null;
    }

    public sealed class LuaAliasAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaAliasAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax? Type { get => throw null; init { } }
        public LuaAliasAnnotationSyntax(string Name, Lunil.EmmyLua.LuaTypeSyntax? Type, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaAliasAnnotationSyntax? left, Lunil.EmmyLua.LuaAliasAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaAliasAnnotationSyntax? left, Lunil.EmmyLua.LuaAliasAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaAliasAnnotationSyntax? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.EmmyLua.LuaTypeSyntax? Type, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaAliasContinuationAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaAliasContinuationAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.EmmyLua.LuaTypeSyntax Type { get => throw null; init { } }
        public LuaAliasContinuationAnnotationSyntax(Lunil.EmmyLua.LuaTypeSyntax Type, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaAliasContinuationAnnotationSyntax? left, Lunil.EmmyLua.LuaAliasContinuationAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaAliasContinuationAnnotationSyntax? left, Lunil.EmmyLua.LuaAliasContinuationAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaAliasContinuationAnnotationSyntax? other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaTypeSyntax Type, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public enum LuaAnnotationDialect
    {
        LuaLs = 0,
        LegacyEmmyLua = 1,
        Compatible = 2
    }

    public sealed class LuaAnnotationDocument : System.IEquatable<Lunil.EmmyLua.LuaAnnotationDocument>
    {
        public Lunil.Core.Text.SourceText Source { get => throw null; init { } }
        public Lunil.EmmyLua.LuaAnnotationDialect Dialect { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> Annotations { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics { get => throw null; init { } }
        public int ParseErrorCount { get => throw null; init { } }
        public LuaAnnotationDocument(Lunil.Core.Text.SourceText Source, Lunil.EmmyLua.LuaAnnotationDialect Dialect, System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> Annotations, System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics, int ParseErrorCount) { }
        public static Lunil.EmmyLua.LuaAnnotationDocument Empty(Lunil.Core.Text.SourceText source, Lunil.EmmyLua.LuaAnnotationDialect dialect) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaAnnotationDocument? left, Lunil.EmmyLua.LuaAnnotationDocument? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaAnnotationDocument? left, Lunil.EmmyLua.LuaAnnotationDocument? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaAnnotationDocument? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.SourceText Source, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaAnnotationSyntax> Annotations, out System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics, out int ParseErrorCount) => throw null;
    }

    public sealed class LuaAnnotationLexResult : System.IEquatable<Lunil.EmmyLua.LuaAnnotationLexResult>
    {
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaAnnotationToken> Tokens { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics { get => throw null; init { } }
        public int ErrorCount { get => throw null; init { } }
        public LuaAnnotationLexResult(Lunil.Core.Text.TextSpan Span, System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaAnnotationToken> Tokens, System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics, int ErrorCount) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaAnnotationLexResult? left, Lunil.EmmyLua.LuaAnnotationLexResult? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaAnnotationLexResult? left, Lunil.EmmyLua.LuaAnnotationLexResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaAnnotationLexResult? other) => throw null;
        public void Deconstruct(out Lunil.Core.Text.TextSpan Span, out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaAnnotationToken> Tokens, out System.Collections.Immutable.ImmutableArray<Lunil.Core.Diagnostics.Diagnostic> Diagnostics, out int ErrorCount) => throw null;
    }

    public static class LuaAnnotationLexer
    {
        public static Lunil.EmmyLua.LuaAnnotationLexResult Lex(Lunil.Core.Text.SourceText source, Lunil.Core.Text.TextSpan span, Lunil.EmmyLua.LuaAnnotationOptions? options = null) => throw null;
    }

    public sealed class LuaAnnotationOptions : System.IEquatable<Lunil.EmmyLua.LuaAnnotationOptions>
    {
        public static Lunil.EmmyLua.LuaAnnotationOptions Default { get => throw null; }
        public bool Enabled { get => throw null; init { } }
        public Lunil.EmmyLua.LuaAnnotationDialect Dialect { get => throw null; init { } }
        public bool EnableLegacyFallback { get => throw null; init { } }
        public bool ReportDialectAmbiguity { get => throw null; init { } }
        public bool ReportUnknownTags { get => throw null; init { } }
        public Lunil.Core.Diagnostics.DiagnosticSeverity SyntaxDiagnosticSeverity { get => throw null; init { } }
        public int MaximumAnnotationCount { get => throw null; init { } }
        public int MaximumTokensPerAnnotation { get => throw null; init { } }
        public int MaximumTypeDepth { get => throw null; init { } }
        public int MaximumDiagnosticCount { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableHashSet<string> SuppressedDiagnosticCodes { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaAnnotationOptions? left, Lunil.EmmyLua.LuaAnnotationOptions? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaAnnotationOptions? left, Lunil.EmmyLua.LuaAnnotationOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaAnnotationOptions? other) => throw null;
    }

    public static class LuaAnnotationParser
    {
        public static Lunil.EmmyLua.LuaAnnotationDocument Parse(Lunil.Syntax.Lexing.LuaLexResult lexing, Lunil.EmmyLua.LuaAnnotationOptions? options = null) => throw null;
    }

    public abstract class LuaAnnotationSyntax : System.IEquatable<Lunil.EmmyLua.LuaAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Tag { get => throw null; init { } }
        public Lunil.EmmyLua.LuaAnnotationDialect Dialect { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        protected LuaAnnotationSyntax(string Tag, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) { }
        public override string ToString() => throw null;
        protected virtual bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaAnnotationSyntax? left, Lunil.EmmyLua.LuaAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaAnnotationSyntax? left, Lunil.EmmyLua.LuaAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public virtual bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        protected LuaAnnotationSyntax(Lunil.EmmyLua.LuaAnnotationSyntax original) { }
        public void Deconstruct(out string Tag, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public readonly struct LuaAnnotationToken : System.IEquatable<Lunil.EmmyLua.LuaAnnotationToken>
    {
        public Lunil.EmmyLua.LuaAnnotationTokenKind Kind { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public string Text { get => throw null; init { } }
        public LuaAnnotationToken(Lunil.EmmyLua.LuaAnnotationTokenKind Kind, Lunil.Core.Text.TextSpan Span, string Text) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaAnnotationToken left, Lunil.EmmyLua.LuaAnnotationToken right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaAnnotationToken left, Lunil.EmmyLua.LuaAnnotationToken right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object obj) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaAnnotationToken other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaAnnotationTokenKind Kind, out Lunil.Core.Text.TextSpan Span, out string Text) => throw null;
    }

    public enum LuaAnnotationTokenKind
    {
        EndOfFile = 0,
        Identifier = 1,
        StringLiteral = 2,
        NumericLiteral = 3,
        At = 4,
        Colon = 5,
        Comma = 6,
        Dot = 7,
        Ellipsis = 8,
        Pipe = 9,
        Ampersand = 10,
        Question = 11,
        OpenParenthesis = 12,
        CloseParenthesis = 13,
        OpenBrace = 14,
        CloseBrace = 15,
        OpenBracket = 16,
        CloseBracket = 17,
        LessThan = 18,
        GreaterThan = 19,
        Assign = 20,
        Plus = 21,
        Minus = 22,
        Star = 23,
        Hash = 24,
        BadToken = 25
    }

    public enum LuaAnnotationVisibility
    {
        Unspecified = 0,
        Public = 1,
        Protected = 2,
        Private = 3,
        Package = 4
    }

    public sealed class LuaArrayTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaArrayTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.EmmyLua.LuaTypeSyntax ElementType { get => throw null; init { } }
        public LuaArrayTypeSyntax(Lunil.EmmyLua.LuaTypeSyntax ElementType, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaArrayTypeSyntax? left, Lunil.EmmyLua.LuaArrayTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaArrayTypeSyntax? left, Lunil.EmmyLua.LuaArrayTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaArrayTypeSyntax? other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaTypeSyntax ElementType, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaCastAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaCastAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax Type { get => throw null; init { } }
        public Lunil.EmmyLua.LuaCastOperation Operation { get => throw null; init { } }
        public LuaCastAnnotationSyntax(string Name, Lunil.EmmyLua.LuaTypeSyntax Type, Lunil.EmmyLua.LuaCastOperation Operation, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaCastAnnotationSyntax? left, Lunil.EmmyLua.LuaCastAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaCastAnnotationSyntax? left, Lunil.EmmyLua.LuaCastAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaCastAnnotationSyntax? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.EmmyLua.LuaTypeSyntax Type, out Lunil.EmmyLua.LuaCastOperation Operation, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public enum LuaCastOperation
    {
        Replace = 0,
        Add = 1,
        Remove = 2
    }

    public sealed class LuaClassAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaClassAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<string> TypeParameters { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> BaseTypes { get => throw null; init { } }
        public LuaClassAnnotationSyntax(string Name, System.Collections.Immutable.ImmutableArray<string> TypeParameters, System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> BaseTypes, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaClassAnnotationSyntax? left, Lunil.EmmyLua.LuaClassAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaClassAnnotationSyntax? left, Lunil.EmmyLua.LuaClassAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaClassAnnotationSyntax? other) => throw null;
        public void Deconstruct(out string Name, out System.Collections.Immutable.ImmutableArray<string> TypeParameters, out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> BaseTypes, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public enum LuaDiagnosticAction
    {
        Disable = 0,
        DisableNextLine = 1,
        Enable = 2
    }

    public sealed class LuaDiagnosticAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaDiagnosticAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.EmmyLua.LuaDiagnosticAction Action { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableHashSet<string> Codes { get => throw null; init { } }
        public LuaDiagnosticAnnotationSyntax(Lunil.EmmyLua.LuaDiagnosticAction Action, System.Collections.Immutable.ImmutableHashSet<string> Codes, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaDiagnosticAnnotationSyntax? left, Lunil.EmmyLua.LuaDiagnosticAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaDiagnosticAnnotationSyntax? left, Lunil.EmmyLua.LuaDiagnosticAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaDiagnosticAnnotationSyntax? other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaDiagnosticAction Action, out System.Collections.Immutable.ImmutableHashSet<string> Codes, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaEnumAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaEnumAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax? KeyType { get => throw null; init { } }
        public LuaEnumAnnotationSyntax(string Name, Lunil.EmmyLua.LuaTypeSyntax? KeyType, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaEnumAnnotationSyntax? left, Lunil.EmmyLua.LuaEnumAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaEnumAnnotationSyntax? left, Lunil.EmmyLua.LuaEnumAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaEnumAnnotationSyntax? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.EmmyLua.LuaTypeSyntax? KeyType, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaFieldAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaFieldAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax Type { get => throw null; init { } }
        public Lunil.EmmyLua.LuaAnnotationVisibility Visibility { get => throw null; init { } }
        public bool IsOptional { get => throw null; init { } }
        public LuaFieldAnnotationSyntax(string Name, Lunil.EmmyLua.LuaTypeSyntax Type, Lunil.EmmyLua.LuaAnnotationVisibility Visibility, bool IsOptional, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaFieldAnnotationSyntax? left, Lunil.EmmyLua.LuaFieldAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaFieldAnnotationSyntax? left, Lunil.EmmyLua.LuaFieldAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaFieldAnnotationSyntax? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.EmmyLua.LuaTypeSyntax Type, out Lunil.EmmyLua.LuaAnnotationVisibility Visibility, out bool IsOptional, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaFunctionParameterTypeSyntax : System.IEquatable<Lunil.EmmyLua.LuaFunctionParameterTypeSyntax>
    {
        public string? Name { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax Type { get => throw null; init { } }
        public bool IsOptional { get => throw null; init { } }
        public bool IsVararg { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public LuaFunctionParameterTypeSyntax(string? Name, Lunil.EmmyLua.LuaTypeSyntax Type, bool IsOptional, bool IsVararg, Lunil.Core.Text.TextSpan Span) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaFunctionParameterTypeSyntax? left, Lunil.EmmyLua.LuaFunctionParameterTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaFunctionParameterTypeSyntax? left, Lunil.EmmyLua.LuaFunctionParameterTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaFunctionParameterTypeSyntax? other) => throw null;
        public void Deconstruct(out string? Name, out Lunil.EmmyLua.LuaTypeSyntax Type, out bool IsOptional, out bool IsVararg, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaFunctionTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaFunctionTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaFunctionParameterTypeSyntax> Parameters { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Returns { get => throw null; init { } }
        public LuaFunctionTypeSyntax(System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaFunctionParameterTypeSyntax> Parameters, System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Returns, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaFunctionTypeSyntax? left, Lunil.EmmyLua.LuaFunctionTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaFunctionTypeSyntax? left, Lunil.EmmyLua.LuaFunctionTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaFunctionTypeSyntax? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaFunctionParameterTypeSyntax> Parameters, out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Returns, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaGenericAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaGenericAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaGenericParameterSyntax> Parameters { get => throw null; init { } }
        public LuaGenericAnnotationSyntax(System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaGenericParameterSyntax> Parameters, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaGenericAnnotationSyntax? left, Lunil.EmmyLua.LuaGenericAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaGenericAnnotationSyntax? left, Lunil.EmmyLua.LuaGenericAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaGenericAnnotationSyntax? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaGenericParameterSyntax> Parameters, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaGenericParameterSyntax : System.IEquatable<Lunil.EmmyLua.LuaGenericParameterSyntax>
    {
        public string Name { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax? Constraint { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public LuaGenericParameterSyntax(string Name, Lunil.EmmyLua.LuaTypeSyntax? Constraint, Lunil.Core.Text.TextSpan Span) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaGenericParameterSyntax? left, Lunil.EmmyLua.LuaGenericParameterSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaGenericParameterSyntax? left, Lunil.EmmyLua.LuaGenericParameterSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaGenericParameterSyntax? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.EmmyLua.LuaTypeSyntax? Constraint, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaIntersectionTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaIntersectionTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Types { get => throw null; init { } }
        public LuaIntersectionTypeSyntax(System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Types, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaIntersectionTypeSyntax? left, Lunil.EmmyLua.LuaIntersectionTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaIntersectionTypeSyntax? left, Lunil.EmmyLua.LuaIntersectionTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaIntersectionTypeSyntax? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Types, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaLiteralTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaLiteralTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.EmmyLua.LuaTypeLiteralKind Kind { get => throw null; init { } }
        public string Text { get => throw null; init { } }
        public LuaLiteralTypeSyntax(Lunil.EmmyLua.LuaTypeLiteralKind Kind, string Text, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaLiteralTypeSyntax? left, Lunil.EmmyLua.LuaLiteralTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaLiteralTypeSyntax? left, Lunil.EmmyLua.LuaLiteralTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaLiteralTypeSyntax? other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaTypeLiteralKind Kind, out string Text, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public static class LuaLsAnnotationParser
    {
        public static Lunil.EmmyLua.LuaAnnotationDocument Parse(Lunil.Syntax.Lexing.LuaLexResult lexing, Lunil.EmmyLua.LuaAnnotationOptions? options = null) => throw null;
    }

    public sealed class LuaMarkerAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaMarkerAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Marker { get => throw null; init { } }
        public string Arguments { get => throw null; init { } }
        public LuaMarkerAnnotationSyntax(string Marker, string Arguments, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaMarkerAnnotationSyntax? left, Lunil.EmmyLua.LuaMarkerAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaMarkerAnnotationSyntax? left, Lunil.EmmyLua.LuaMarkerAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaMarkerAnnotationSyntax? other) => throw null;
        public void Deconstruct(out string Marker, out string Arguments, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaNamedTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaNamedTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> TypeArguments { get => throw null; init { } }
        public LuaNamedTypeSyntax(string Name, System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> TypeArguments, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaNamedTypeSyntax? left, Lunil.EmmyLua.LuaNamedTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaNamedTypeSyntax? left, Lunil.EmmyLua.LuaNamedTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaNamedTypeSyntax? other) => throw null;
        public void Deconstruct(out string Name, out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> TypeArguments, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaNullableTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaNullableTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.EmmyLua.LuaTypeSyntax Type { get => throw null; init { } }
        public LuaNullableTypeSyntax(Lunil.EmmyLua.LuaTypeSyntax Type, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaNullableTypeSyntax? left, Lunil.EmmyLua.LuaNullableTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaNullableTypeSyntax? left, Lunil.EmmyLua.LuaNullableTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaNullableTypeSyntax? other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaTypeSyntax Type, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaOperatorAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaOperatorAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Operator { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax? OperandType { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax ResultType { get => throw null; init { } }
        public LuaOperatorAnnotationSyntax(string Operator, Lunil.EmmyLua.LuaTypeSyntax? OperandType, Lunil.EmmyLua.LuaTypeSyntax ResultType, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaOperatorAnnotationSyntax? left, Lunil.EmmyLua.LuaOperatorAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaOperatorAnnotationSyntax? left, Lunil.EmmyLua.LuaOperatorAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaOperatorAnnotationSyntax? other) => throw null;
        public void Deconstruct(out string Operator, out Lunil.EmmyLua.LuaTypeSyntax? OperandType, out Lunil.EmmyLua.LuaTypeSyntax ResultType, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaOverloadAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaOverloadAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.EmmyLua.LuaFunctionTypeSyntax Type { get => throw null; init { } }
        public LuaOverloadAnnotationSyntax(Lunil.EmmyLua.LuaFunctionTypeSyntax Type, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaOverloadAnnotationSyntax? left, Lunil.EmmyLua.LuaOverloadAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaOverloadAnnotationSyntax? left, Lunil.EmmyLua.LuaOverloadAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaOverloadAnnotationSyntax? other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaFunctionTypeSyntax Type, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaParamAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaParamAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string Name { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax Type { get => throw null; init { } }
        public bool IsOptional { get => throw null; init { } }
        public LuaParamAnnotationSyntax(string Name, Lunil.EmmyLua.LuaTypeSyntax Type, bool IsOptional, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaParamAnnotationSyntax? left, Lunil.EmmyLua.LuaParamAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaParamAnnotationSyntax? left, Lunil.EmmyLua.LuaParamAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaParamAnnotationSyntax? other) => throw null;
        public void Deconstruct(out string Name, out Lunil.EmmyLua.LuaTypeSyntax Type, out bool IsOptional, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaReturnAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaReturnAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaReturnTypeSyntax> Returns { get => throw null; init { } }
        public LuaReturnAnnotationSyntax(System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaReturnTypeSyntax> Returns, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaReturnAnnotationSyntax? left, Lunil.EmmyLua.LuaReturnAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaReturnAnnotationSyntax? left, Lunil.EmmyLua.LuaReturnAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaReturnAnnotationSyntax? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaReturnTypeSyntax> Returns, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaReturnTypeSyntax : System.IEquatable<Lunil.EmmyLua.LuaReturnTypeSyntax>
    {
        public Lunil.EmmyLua.LuaTypeSyntax Type { get => throw null; init { } }
        public string? Name { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public LuaReturnTypeSyntax(Lunil.EmmyLua.LuaTypeSyntax Type, string? Name, Lunil.Core.Text.TextSpan Span) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaReturnTypeSyntax? left, Lunil.EmmyLua.LuaReturnTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaReturnTypeSyntax? left, Lunil.EmmyLua.LuaReturnTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaReturnTypeSyntax? other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaTypeSyntax Type, out string? Name, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaTableFieldTypeSyntax : System.IEquatable<Lunil.EmmyLua.LuaTableFieldTypeSyntax>
    {
        public string? Name { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax? KeyType { get => throw null; init { } }
        public Lunil.EmmyLua.LuaTypeSyntax ValueType { get => throw null; init { } }
        public bool IsOptional { get => throw null; init { } }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        public LuaTableFieldTypeSyntax(string? Name, Lunil.EmmyLua.LuaTypeSyntax? KeyType, Lunil.EmmyLua.LuaTypeSyntax ValueType, bool IsOptional, Lunil.Core.Text.TextSpan Span) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaTableFieldTypeSyntax? left, Lunil.EmmyLua.LuaTableFieldTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaTableFieldTypeSyntax? left, Lunil.EmmyLua.LuaTableFieldTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaTableFieldTypeSyntax? other) => throw null;
        public void Deconstruct(out string? Name, out Lunil.EmmyLua.LuaTypeSyntax? KeyType, out Lunil.EmmyLua.LuaTypeSyntax ValueType, out bool IsOptional, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaTableTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaTableTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTableFieldTypeSyntax> Fields { get => throw null; init { } }
        public LuaTableTypeSyntax(System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTableFieldTypeSyntax> Fields, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaTableTypeSyntax? left, Lunil.EmmyLua.LuaTableTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaTableTypeSyntax? left, Lunil.EmmyLua.LuaTableTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaTableTypeSyntax? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTableFieldTypeSyntax> Fields, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaTupleTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaTupleTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Elements { get => throw null; init { } }
        public LuaTupleTypeSyntax(System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Elements, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaTupleTypeSyntax? left, Lunil.EmmyLua.LuaTupleTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaTupleTypeSyntax? left, Lunil.EmmyLua.LuaTupleTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaTupleTypeSyntax? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Elements, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaTypeAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaTypeAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Types { get => throw null; init { } }
        public LuaTypeAnnotationSyntax(System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Types, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaTypeAnnotationSyntax? left, Lunil.EmmyLua.LuaTypeAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaTypeAnnotationSyntax? left, Lunil.EmmyLua.LuaTypeAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaTypeAnnotationSyntax? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Types, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public enum LuaTypeLiteralKind
    {
        Nil = 0,
        Boolean = 1,
        Number = 2,
        Text = 3
    }

    public abstract class LuaTypeSyntax : System.IEquatable<Lunil.EmmyLua.LuaTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.Core.Text.TextSpan Span { get => throw null; init { } }
        protected LuaTypeSyntax(Lunil.Core.Text.TextSpan Span) { }
        public override string ToString() => throw null;
        protected virtual bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaTypeSyntax? left, Lunil.EmmyLua.LuaTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaTypeSyntax? left, Lunil.EmmyLua.LuaTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public virtual bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        protected LuaTypeSyntax(Lunil.EmmyLua.LuaTypeSyntax original) { }
        public void Deconstruct(out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaUnionTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaUnionTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Types { get => throw null; init { } }
        public LuaUnionTypeSyntax(System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Types, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaUnionTypeSyntax? left, Lunil.EmmyLua.LuaUnionTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaUnionTypeSyntax? left, Lunil.EmmyLua.LuaUnionTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaUnionTypeSyntax? other) => throw null;
        public void Deconstruct(out System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> Types, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaUnknownAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaUnknownAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public string UnknownTag { get => throw null; init { } }
        public string RawText { get => throw null; init { } }
        public LuaUnknownAnnotationSyntax(string UnknownTag, string RawText, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaUnknownAnnotationSyntax? left, Lunil.EmmyLua.LuaUnknownAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaUnknownAnnotationSyntax? left, Lunil.EmmyLua.LuaUnknownAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaUnknownAnnotationSyntax? other) => throw null;
        public void Deconstruct(out string UnknownTag, out string RawText, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaVarargAnnotationSyntax : Lunil.EmmyLua.LuaAnnotationSyntax, System.IEquatable<Lunil.EmmyLua.LuaVarargAnnotationSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.EmmyLua.LuaTypeSyntax Type { get => throw null; init { } }
        public LuaVarargAnnotationSyntax(Lunil.EmmyLua.LuaTypeSyntax Type, Lunil.EmmyLua.LuaAnnotationDialect Dialect, Lunil.Core.Text.TextSpan Span) : base(default(string), default(Lunil.EmmyLua.LuaAnnotationDialect), default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaVarargAnnotationSyntax? left, Lunil.EmmyLua.LuaVarargAnnotationSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaVarargAnnotationSyntax? left, Lunil.EmmyLua.LuaVarargAnnotationSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaAnnotationSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaVarargAnnotationSyntax? other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaTypeSyntax Type, out Lunil.EmmyLua.LuaAnnotationDialect Dialect, out Lunil.Core.Text.TextSpan Span) => throw null;
    }

    public sealed class LuaVarargTypeSyntax : Lunil.EmmyLua.LuaTypeSyntax, System.IEquatable<Lunil.EmmyLua.LuaVarargTypeSyntax>
    {
        protected System.Type EqualityContract { get => throw null; }
        public Lunil.EmmyLua.LuaTypeSyntax? ElementType { get => throw null; init { } }
        public LuaVarargTypeSyntax(Lunil.EmmyLua.LuaTypeSyntax? ElementType, Lunil.Core.Text.TextSpan Span) : base(default(Lunil.Core.Text.TextSpan)) { }
        public override string ToString() => throw null;
        protected override bool PrintMembers(System.Text.StringBuilder builder) => throw null;
        public static bool operator !=(Lunil.EmmyLua.LuaVarargTypeSyntax? left, Lunil.EmmyLua.LuaVarargTypeSyntax? right) => throw null;
        public static bool operator ==(Lunil.EmmyLua.LuaVarargTypeSyntax? left, Lunil.EmmyLua.LuaVarargTypeSyntax? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public sealed override bool Equals(Lunil.EmmyLua.LuaTypeSyntax? other) => throw null;
        public bool Equals(Lunil.EmmyLua.LuaVarargTypeSyntax? other) => throw null;
        public void Deconstruct(out Lunil.EmmyLua.LuaTypeSyntax? ElementType, out Lunil.Core.Text.TextSpan Span) => throw null;
    }
}
