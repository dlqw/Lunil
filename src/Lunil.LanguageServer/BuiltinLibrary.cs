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
/// One readonly page of the embedded Lua standard library (`base`, `math`, `string`,
/// ...): its annotated Lua source plus the member spans and doc comments extracted
/// from it. Served to editors as the virtual document <c>lunil-builtin:&lt;name&gt;.lua</c>.
/// </summary>
internal sealed record BuiltinDocument(
    string Name,
    string Source,
    ImmutableDictionary<string, TextSpan> MemberSpans,
    ImmutableDictionary<string, string> Docs,
    int[] LineStarts)
{
    public string Uri => $"lunil-builtin:{Name}.lua";

    /// <summary>Converts a span in this document to a line/character pair.</summary>
    public (int Line, int Character) ToPosition(TextSpan span)
    {
        var line = Array.BinarySearch(LineStarts, span.Start);
        if (line < 0)
        {
            line = Math.Max(0, ~line - 1);
        }

        return (line, span.Start - LineStarts[line]);
    }
}

/// <summary>
/// The embedded Lua standard library definitions: one annotated Lua source per page,
/// each analyzed with the same front end as user code. Provides global types for
/// analysis, per-member spans for navigation into the readonly builtin documents,
/// and doc comments for hover.
/// </summary>
internal sealed class BuiltinLibrary
{
    /// <summary>Page names in a stable display order; `base` holds the global functions.</summary>
    private static readonly string[] PageNames =
    [
        "base", "coroutine", "debug", "io", "math", "os", "string", "table", "utf8",
    ];

    /// <summary>
    /// The process-wide library instance, loaded eagerly. Declared after
    /// <see cref="PageNames"/>: static initializers run in declaration order.
    /// </summary>
    internal static BuiltinLibrary Value { get; } = Load();

    private BuiltinLibrary(
        BuiltinDocument basePage,
        ImmutableArray<BuiltinDocument> documents,
        ImmutableDictionary<string, LuaType> globals)
    {
        Base = basePage;
        Documents = documents;
        Globals = globals;
    }

    public BuiltinDocument Base { get; }

    public ImmutableArray<BuiltinDocument> Documents { get; }

    public ImmutableDictionary<string, LuaType> Globals { get; }

    public static BuiltinLibrary Load()
    {
        var frontEnd = new LuaFrontEndSession(new LuaCompilerOptions
        {
            Binder = LuaBinderOptions.Default with { CollectCodeReferences = true },
        });

        var globals = ImmutableDictionary.CreateBuilder<string, LuaType>(StringComparer.Ordinal);
        var pages = new List<BuiltinDocument>(PageNames.Length);
        foreach (var name in PageNames)
        {
            var source = ReadSource(name);
            var snapshot = frontEnd.Process(
                LuaSourceDocument.FromUtf8(source, $"lunil-builtin:{name}.lua"),
                LuaFrontEndStage.Analysis,
                LuaAnalysisEnvironment.Empty);

            // Each page returns its surface: the globals table for `base`, the library
            // table itself for library pages. Its fields become the page's globals.
            var exported = snapshot.Analysis!.Functions.FirstOrDefault(static function => function.FunctionId == 0)
                ?.InferredReturns.GetElementOrNil(0);
            if (exported is LuaStructuralTableType shape)
            {
                if (name == "base")
                {
                    foreach (var field in shape.Fields)
                    {
                        if (field.Name is not null)
                        {
                            globals[field.Name] = field.ValueType;
                        }
                    }
                }
                else
                {
                    globals[name] = exported;
                }
            }

            pages.Add(LoadPage(name, source, snapshot, globals));
        }

        return new BuiltinLibrary(
            pages[0],
            [.. pages],
            globals.ToImmutable());
    }

    /// <summary>Resolves a page by name, with or without the `.lua` suffix.</summary>
    public bool TryGetDocument(string name, out BuiltinDocument document)
    {
        var normalized = name.EndsWith(".lua", StringComparison.Ordinal)
            ? name[..^4]
            : name;
        document = Documents.FirstOrDefault(page =>
            string.Equals(page.Name, normalized, StringComparison.Ordinal))!;
        return document is not null;
    }

    /// <summary>Finds the page and span defining a member path (`string.format`, `print`).</summary>
    public bool TryGetMemberLocation(string path, out BuiltinDocument document, out TextSpan span)
    {
        foreach (var page in Documents)
        {
            if (page.MemberSpans.TryGetValue(path, out span))
            {
                document = page;
                return true;
            }
        }

        document = null!;
        span = default;
        return false;
    }

    /// <summary>The doc comment prose for a member path, when its page carries one.</summary>
    public string? FindDoc(string path)
    {
        foreach (var page in Documents)
        {
            if (page.Docs.TryGetValue(path, out var doc))
            {
                return doc;
            }
        }

        return null;
    }

    /// <summary>The `owner.member` path for a member access on a named receiver, when known.</summary>
    public bool TryGetMemberPath(string receiverName, string memberName, out string path)
    {
        path = receiverName + "." + memberName;
        var known = path;
        return Documents.Any(page => page.MemberSpans.ContainsKey(known));
    }

    private static BuiltinDocument LoadPage(
        string name,
        string source,
        LuaFrontEndSnapshot snapshot,
        ImmutableDictionary<string, LuaType>.Builder globals)
    {
        var memberSpans = ImmutableDictionary.CreateBuilder<string, TextSpan>(StringComparer.Ordinal);

        // Global function declarations (`function print(...)`) write without a
        // receiver; library members (`function string.format`) write with a receiver
        // span that includes the trailing separator.
        foreach (var reference in snapshot.SemanticModel!.UnifiedReferences)
        {
            if (reference.Name is not { Length: > 0 } ||
                !reference.Access.HasFlag(LuaReferenceAccess.Write))
            {
                continue;
            }

            if (reference.ReceiverSpan is not { Length: > 0 } receiver)
            {
                if (globals.ContainsKey(reference.Name) && !memberSpans.ContainsKey(reference.Name))
                {
                    memberSpans[reference.Name] = reference.Span;
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

            var path = receiverName + "." + reference.Name;
            if (!memberSpans.ContainsKey(path))
            {
                memberSpans[path] = reference.Span;
            }
        }

        // Library tables (`local math = {}`) are local declarations the unified-reference
        // walk above never sees; their declaring spans anchor F12/hover on `math`,
        // `string`, and friends.
        foreach (var symbol in snapshot.SemanticModel.Symbols)
        {
            if (symbol.Kind == LuaSymbolKind.Local &&
                globals.ContainsKey(symbol.Name) &&
                !memberSpans.ContainsKey(symbol.Name))
            {
                memberSpans[symbol.Name] = symbol.DeclaringSpan;
            }
        }

        var docs = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
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

        return new BuiltinDocument(
            name,
            source,
            memberSpans.ToImmutable(),
            docs.ToImmutable(),
            [.. lineStarts]);
    }

    private static string ReadSource(string name)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var resourceName = $"Lunil.LanguageServer.BuiltinLibrary.builtin-{name}.lua";
        using var stream = assembly.GetManifestResourceStream(resourceName) ??
            throw new InvalidOperationException($"The builtin Lua library resource is missing: {resourceName}.");
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
        if (doc.Length > 200)
        {
            doc = doc[..200] + "…";
        }

        return true;
    }
}
