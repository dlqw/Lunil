using System.Collections.Immutable;
using System.Reflection;
using Lunil.Analysis;
using Lunil.Compiler;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.LanguageServer;

/// <summary>
/// The embedded Lua standard library definitions: one annotated Lua source analyzed
/// with the same front end as user code. Provides global types for analysis, per-member
/// spans for navigation into the readonly builtin document, and doc comments for hover.
/// </summary>
internal sealed class BuiltinLibrary
{
    private BuiltinLibrary(
        string source,
        ImmutableDictionary<string, LuaType> globals,
        ImmutableDictionary<string, TextSpan> memberSpans,
        ImmutableDictionary<string, string> docs,
        int[] lineStarts)
    {
        Source = source;
        Globals = globals;
        MemberSpans = memberSpans;
        Docs = docs;
        LineStarts = lineStarts;
    }

    public string Source { get; }

    public ImmutableDictionary<string, LuaType> Globals { get; }

    /// <summary>Member paths (`string.format`, `print`) to their defining spans.</summary>
    public ImmutableDictionary<string, TextSpan> MemberSpans { get; }

    /// <summary>Member paths to their leading doc comment prose.</summary>
    public ImmutableDictionary<string, string> Docs { get; }

    private int[] LineStarts { get; }

    public static BuiltinLibrary Load()
    {
        var source = ReadSource();
        var frontEnd = new LuaFrontEndSession(new LuaCompilerOptions
        {
            Binder = LuaBinderOptions.Default with { CollectCodeReferences = true },
        });
        var snapshot = frontEnd.Process(
            LuaSourceDocument.FromUtf8(source, "lunil-builtin://lua"),
            LuaFrontEndStage.Analysis,
            LuaAnalysisEnvironment.Empty);

        var globals = ImmutableDictionary.CreateBuilder<string, LuaType>(StringComparer.Ordinal);
        var memberSpans = ImmutableDictionary.CreateBuilder<string, TextSpan>(StringComparer.Ordinal);
        var docs = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        // Global functions and libraries are the fields of the returned table; their
        // member writes carry the spans the readonly document navigates to.
        var exported = snapshot.Analysis!.Functions.FirstOrDefault(static function => function.FunctionId == 0)
            ?.InferredReturns.GetElementOrNil(0);
        if (exported is LuaStructuralTableType shape)
        {
            foreach (var field in shape.Fields)
            {
                if (field.Name is not null)
                {
                    globals[field.Name] = field.ValueType;
                }
            }
        }

        foreach (var reference in snapshot.SemanticModel!.UnifiedReferences)
        {
            if (reference.Name is not { Length: > 0 } name ||
                !reference.Access.HasFlag(LuaReferenceAccess.Write))
            {
                continue;
            }

            // Global function declarations (`function print(...)`) write without a
            // receiver; library members (`function string.format`) write with a receiver
            // span that includes the trailing separator.
            if (reference.ReceiverSpan is not { Length: > 0 } receiver)
            {
                if (globals.ContainsKey(name) && !memberSpans.ContainsKey(name))
                {
                    memberSpans[name] = reference.Span;
                }

                continue;
            }

            var receiverText = source[
                receiver.Start..Math.Min(receiver.End, source.Length)].TrimEnd('.', ':');
            var receiverName = receiverText.Split('.', ':')[^1].Trim();
            if (receiverName.Length == 0)
            {
                continue;
            }

            var path = receiverName + "." + name;
            if (!memberSpans.ContainsKey(path))
            {
                memberSpans[path] = reference.Span;
            }
        }

        foreach (var path in memberSpans.Keys)
        {
            if (TryReadDocComment(source, memberSpans[path], out var doc))
            {
                docs[path] = doc;
            }
        }

        var lineStarts = new List<int> { 0 };
        for (var index = 0; index < source.Length; index++)
        {
            if (source[index] == '\n')
            {
                lineStarts.Add(index + 1);
            }
        }

        return new BuiltinLibrary(
            source,
            globals.ToImmutable(),
            memberSpans.ToImmutable(),
            docs.ToImmutable(),
            [.. lineStarts]);
    }

    /// <summary>Converts a builtin source span to a line/character pair.</summary>
    public (int Line, int Character) ToPosition(TextSpan span)
    {
        var line = Array.BinarySearch(LineStarts, span.Start);
        if (line < 0)
        {
            line = Math.Max(0, ~line - 1);
        }

        return (line, span.Start - LineStarts[line]);
    }

    /// <summary>The `owner.member` path for a member access on a named receiver, when known.</summary>
    public bool TryGetMemberPath(string receiverName, string memberName, out string path)
    {
        path = receiverName + "." + memberName;
        return MemberSpans.ContainsKey(path);
    }

    private static string ReadSource()
    {
        var assembly = Assembly.GetExecutingAssembly();
        const string resourceName = "Lunil.LanguageServer.BuiltinLibrary.lua-builtin.lua";
        using var stream = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException("The builtin Lua library resource is missing.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static bool TryReadDocComment(string text, TextSpan span, out string doc)
    {
        doc = string.Empty;
        if (span.Start <= 0 || span.Start > text.Length)
        {
            return false;
        }

        var cursor = text.LastIndexOf('\n', Math.Min(span.Start, text.Length - 1)) + 1;
        var prose = new List<string>();
        while (cursor > 0 && text[cursor - 1] == '\n')
        {
            var lineStart = text.LastIndexOf('\n', Math.Max(0, cursor - 2)) + 1;
            var line = text[lineStart..(cursor - 1)].TrimEnd('\r').TrimStart();
            if (!line.StartsWith("---", StringComparison.Ordinal))
            {
                break;
            }

            if (!line.StartsWith("---@", StringComparison.Ordinal))
            {
                var content = line[3..].Trim();
                if (content.Length > 0)
                {
                    prose.Insert(0, content);
                }
            }

            cursor = lineStart;
        }

        if (prose.Count == 0)
        {
            return false;
        }

        doc = string.Join(' ', prose);
        if (doc.Length > 160)
        {
            doc = doc[..160] + "…";
        }

        return true;
    }
}
