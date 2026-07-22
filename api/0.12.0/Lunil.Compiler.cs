// Target Frameworks: net10.0
#nullable enable

namespace Lunil.Compiler
{
    public static class LuaAnnotationSymbolKeyExtensions
    {
        public static Lunil.Semantics.Binding.LuaSymbolKey GetAnnotationKey(this Lunil.Compiler.LuaCompilationResult compilation, Lunil.EmmyLua.LuaAnnotationSyntax annotation, string moduleIdentity) => throw null;
        public static Lunil.EmmyLua.LuaAnnotationSyntax? ResolveAnnotationKey(this Lunil.Compiler.LuaCompilationResult compilation, Lunil.Semantics.Binding.LuaSymbolKey key, string moduleIdentity) => throw null;
    }

    public sealed class LuaCompilationDiagnostic : System.IEquatable<Lunil.Compiler.LuaCompilationDiagnostic>
    {
        public Lunil.Compiler.LuaCompilationPhase Phase { get => throw null; init { } }
        public Lunil.Core.Diagnostics.Diagnostic Diagnostic { get => throw null; init { } }
        public string Code { get => throw null; }
        public Lunil.Core.Diagnostics.DiagnosticSeverity Severity { get => throw null; }
        public Lunil.Core.Text.TextSpan Span { get => throw null; }
        public string Message { get => throw null; }
        public LuaCompilationDiagnostic(Lunil.Compiler.LuaCompilationPhase Phase, Lunil.Core.Diagnostics.Diagnostic Diagnostic) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Compiler.LuaCompilationDiagnostic? left, Lunil.Compiler.LuaCompilationDiagnostic? right) => throw null;
        public static bool operator ==(Lunil.Compiler.LuaCompilationDiagnostic? left, Lunil.Compiler.LuaCompilationDiagnostic? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Compiler.LuaCompilationDiagnostic? other) => throw null;
        public void Deconstruct(out Lunil.Compiler.LuaCompilationPhase Phase, out Lunil.Core.Diagnostics.Diagnostic Diagnostic) => throw null;
    }

    public enum LuaCompilationPhase
    {
        Lexing = 0,
        Annotation = 1,
        Parsing = 2,
        Binding = 3,
        Analysis = 4,
        Lowering = 5,
        Verification = 6,
        Configuration = 7
    }

    public sealed class LuaCompilationResult : System.IEquatable<Lunil.Compiler.LuaCompilationResult>
    {
        public Lunil.Compiler.LuaSourceDocument Source { get => throw null; init { } }
        public Lunil.Syntax.Parsing.LuaParseResult Syntax { get => throw null; init { } }
        public Lunil.EmmyLua.LuaAnnotationDocument Annotations { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaSemanticModel SemanticModel { get => throw null; init { } }
        public Lunil.Analysis.LuaAnalysisResult Analysis { get => throw null; init { } }
        public Lunil.IR.Canonical.LuaIrModule? Module { get => throw null; init { } }
        public System.Collections.Immutable.ImmutableArray<Lunil.Compiler.LuaCompilationDiagnostic> Diagnostics { get => throw null; init { } }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; }
        public bool Succeeded { get => throw null; }
        public LuaCompilationResult(Lunil.Compiler.LuaSourceDocument Source, Lunil.Syntax.Parsing.LuaParseResult Syntax, Lunil.EmmyLua.LuaAnnotationDocument Annotations, Lunil.Semantics.Binding.LuaSemanticModel SemanticModel, Lunil.Analysis.LuaAnalysisResult Analysis, Lunil.IR.Canonical.LuaIrModule? Module, System.Collections.Immutable.ImmutableArray<Lunil.Compiler.LuaCompilationDiagnostic> Diagnostics) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Compiler.LuaCompilationResult? left, Lunil.Compiler.LuaCompilationResult? right) => throw null;
        public static bool operator ==(Lunil.Compiler.LuaCompilationResult? left, Lunil.Compiler.LuaCompilationResult? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Compiler.LuaCompilationResult? other) => throw null;
        public void Deconstruct(out Lunil.Compiler.LuaSourceDocument Source, out Lunil.Syntax.Parsing.LuaParseResult Syntax, out Lunil.EmmyLua.LuaAnnotationDocument Annotations, out Lunil.Semantics.Binding.LuaSemanticModel SemanticModel, out Lunil.Analysis.LuaAnalysisResult Analysis, out Lunil.IR.Canonical.LuaIrModule? Module, out System.Collections.Immutable.ImmutableArray<Lunil.Compiler.LuaCompilationDiagnostic> Diagnostics) => throw null;
    }

    public sealed class LuaCompiler
    {
        public Lunil.Compiler.LuaCompilerOptions Options { get => throw null; }
        public LuaCompiler(Lunil.Compiler.LuaCompilerOptions? options = null) { }
        public Lunil.Compiler.LuaCompilationResult CompileUtf8(string source, string? sourceName = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Compiler.LuaCompilationResult CompileBytes(System.ReadOnlySpan<byte> source, string? sourceName = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Compiler.LuaCompilationResult Compile(Lunil.Core.Text.SourceText source, string? sourceName = null, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Compiler.LuaCompilationResult Compile(Lunil.Compiler.LuaSourceDocument source, System.Threading.CancellationToken cancellationToken = null) => throw null;
        public Lunil.Compiler.LuaCompilationResult Compile(Lunil.Compiler.LuaSourceDocument source, Lunil.Analysis.LuaAnalysisEnvironment analysisEnvironment, System.Threading.CancellationToken cancellationToken = null) => throw null;
    }

    public sealed class LuaCompilerOptions : System.IEquatable<Lunil.Compiler.LuaCompilerOptions>
    {
        public static Lunil.Compiler.LuaCompilerOptions Default { get => throw null; }
        public Lunil.Core.LuaLanguageVersion LanguageVersion { get => throw null; init { } }
        public Lunil.Syntax.Lexing.LuaLexerOptions Lexer { get => throw null; init { } }
        public Lunil.EmmyLua.LuaAnnotationOptions Annotations { get => throw null; init { } }
        public Lunil.Analysis.LuaAnalysisOptions Analysis { get => throw null; init { } }
        public Lunil.Syntax.Parsing.LuaParserOptions Parser { get => throw null; init { } }
        public Lunil.Semantics.Binding.LuaBinderOptions Binder { get => throw null; init { } }
        public Lunil.IR.Canonical.LuaIrVerifierOptions Verifier { get => throw null; init { } }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Compiler.LuaCompilerOptions? left, Lunil.Compiler.LuaCompilerOptions? right) => throw null;
        public static bool operator ==(Lunil.Compiler.LuaCompilerOptions? left, Lunil.Compiler.LuaCompilerOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Compiler.LuaCompilerOptions? other) => throw null;
    }

    public sealed class LuaSourceDocument : System.IEquatable<Lunil.Compiler.LuaSourceDocument>
    {
        public Lunil.Core.Text.SourceText Text { get => throw null; }
        public string? SourceName { get => throw null; }
        public LuaSourceDocument(Lunil.Core.Text.SourceText text, string? sourceName = null) { }
        public static Lunil.Compiler.LuaSourceDocument FromUtf8(string text, string? sourceName = null) => throw null;
        public static Lunil.Compiler.LuaSourceDocument FromBytes(System.ReadOnlySpan<byte> bytes, string? sourceName = null) => throw null;
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Compiler.LuaSourceDocument? left, Lunil.Compiler.LuaSourceDocument? right) => throw null;
        public static bool operator ==(Lunil.Compiler.LuaSourceDocument? left, Lunil.Compiler.LuaSourceDocument? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Compiler.LuaSourceDocument? other) => throw null;
    }
}
