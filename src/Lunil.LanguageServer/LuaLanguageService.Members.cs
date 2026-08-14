using System.Collections.Immutable;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Lunil.Analysis;
using Lunil.Core.Text;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Parsing;
using Lunil.Workspace;

namespace Lunil.LanguageServer;

/// <summary>
/// Member-aware navigation, hover, and completion built on the binder's unified
/// reference projection (names, dot/colon members, bracket indices) and the
/// workspace export graph. Keeps <see cref="LuaLanguageService"/> focused on
/// request plumbing; resolution heuristics live here.
/// </summary>
internal sealed partial class LuaLanguageService
{
    private const int MaximumMemberDepth = 8;

    /// <summary>Shortest unified reference whose span contains the offset.</summary>
    private static LuaCodeReference? FindCodeReference(LanguageDocumentAnalysis analysis, int offset) =>
        analysis.Compilation.SemanticModel.UnifiedReferences
            .Where(reference => Contains(reference.Span, offset))
            .OrderBy(static reference => reference.Span.Length)
            .FirstOrDefault();

    private static bool IsNamedMember(LuaCodeReference reference) =>
        reference.Kind is LuaReferenceKind.Member or LuaReferenceKind.Index &&
        !string.IsNullOrEmpty(reference.Name);

    private sealed record RequireCall(string ModuleName, TextSpan StringSpan, LuaSymbol? AliasSymbol);

    /// <summary>Every require("...") call in the document with its string span and bound alias.</summary>
    private static ImmutableArray<RequireCall> FindRequireCalls(LanguageDocumentAnalysis analysis)
    {
        var builder = ImmutableArray.CreateBuilder<RequireCall>();
        var root = analysis.Compilation.SemanticModel.Syntax.Root;
        foreach (var call in root.DescendantNodes().Where(static node =>
                     node.Kind == LuaSyntaxKind.CallExpression))
        {
            var children = call.Children;
            LuaSyntaxNode? callee = null;
            LuaSyntaxNode? firstArgument = null;
            foreach (var child in children)
            {
                if (child.Node is { } node)
                {
                    if (node.Kind == LuaSyntaxKind.ArgumentList)
                    {
                        foreach (var argument in node.Children)
                        {
                            if (argument.Node is not { } expression)
                            {
                                continue;
                            }

                            // A parenthesized argument list wraps its expressions in an
                            // ExpressionList node; bare string/table arguments appear directly.
                            if (expression.Kind == LuaSyntaxKind.ExpressionList)
                            {
                                expression = expression.Children
                                    .FirstOrDefault(static child => child.Node is not null)
                                    .Node ?? expression;
                            }

                            firstArgument = expression;
                            break;
                        }
                    }
                    else if (callee is null)
                    {
                        callee = node;
                    }
                }
            }

            if (callee is not { Kind: LuaSyntaxKind.IdentifierExpression } ||
                firstArgument is not { Kind: LuaSyntaxKind.StringLiteralExpression })
            {
                continue;
            }

            var nameToken = callee.ChildTokens().FirstOrDefault(static token =>
                token.Kind == Lunil.Syntax.Lexing.LuaTokenKind.Identifier && !token.IsMissing);
            var name = nameToken is null
                ? string.Empty
                : System.Text.Encoding.ASCII.GetString(analysis.Compilation.SemanticModel.Syntax.Source.GetSpan(nameToken.Span));
            if (!string.Equals(name, "require", StringComparison.Ordinal))
            {
                continue;
            }

            var literalToken = firstArgument.ChildTokens().FirstOrDefault();
            if (literalToken is null)
            {
                continue;
            }

            var raw = analysis.Compilation.SemanticModel.Syntax.Source.GetSpan(literalToken.Span);
            if (raw.Length < 2)
            {
                continue;
            }

            var moduleName = System.Text.Encoding.UTF8.GetString(raw[1..^1]);
            var declaration = root.DescendantNodes().FirstOrDefault(node =>
                (node.Kind == LuaSyntaxKind.LocalDeclarationStatement ||
                 node.Kind == LuaSyntaxKind.AssignmentStatement) &&
                node.Span.Start <= call.Span.Start && node.Span.End >= call.Span.End);
            var aliasToken = declaration?.DescendantTokens().FirstOrDefault(static token =>
                token.Kind == Lunil.Syntax.Lexing.LuaTokenKind.Identifier && !token.IsMissing);
            var alias = aliasToken is null ? null : analysis.Compilation.SemanticModel.Symbols.FirstOrDefault(item =>
                item.DeclaringSpan == aliasToken.Span);
            builder.Add(new RequireCall(moduleName, literalToken.Span, alias));
        }

        return builder.ToImmutable();
    }

    /// <summary>The require call whose string literal contains the offset, if any.</summary>
    private static RequireCall? FindRequireAt(LanguageDocumentAnalysis analysis, int offset)
    {
        foreach (var require in FindRequireCalls(analysis))
        {
            if (Contains(require.StringSpan, offset))
            {
                return require;
            }
        }

        return null;
    }

    /// <summary>Maps lexical symbols assigned from require() to the required module name.</summary>
    private static Dictionary<int, string> BuildRequireAliases(LanguageDocumentAnalysis analysis)
    {
        var result = new Dictionary<int, string>();
        foreach (var require in FindRequireCalls(analysis))
        {
            if (require.AliasSymbol is { } alias)
            {
                result[alias.Id] = require.ModuleName;
            }
        }

        return result;
    }

    /// <summary>The lexical symbol a member reference's receiver resolves to, when simple.</summary>
    private static LuaSymbol? ResolveReceiverSymbol(
        LanguageDocumentAnalysis analysis,
        LuaCodeReference member)
    {
        if (member.ReceiverSpan is not { } receiverSpan || receiverSpan.Length == 0)
        {
            return null;
        }

        var reference = analysis.Compilation.SemanticModel.References
            .Where(item => item.Span.Start == receiverSpan.Start && item.Span.End <= receiverSpan.End)
            .OrderBy(static item => item.Span.Length)
            .FirstOrDefault();
        return reference?.Symbol;
    }

    private sealed record MemberDefinition(Uri Uri, LspRange Range, string Detail);

    /// <summary>
    /// Resolves a member reference to its definition: module exports first (cross-file),
    /// then same-file member writes for locally built tables and classes.
    /// </summary>
    private MemberDefinition? ResolveMemberDefinition(
        LanguageDocumentAnalysis analysis,
        LuaCodeReference member)
    {
        var name = member.Name!;
        var receiver = ResolveReceiverSymbol(analysis, member);
        if (receiver is not null)
        {
            var aliases = BuildRequireAliases(analysis);
            if (aliases.TryGetValue(receiver.Id, out var moduleName))
            {
                var exported = FindExportSymbol(moduleName, name);
                if (exported is not null && workspace.GetUri(exported.ModuleName) is { } uri &&
                    workspace.TryGetDocument(uri, out var document))
                {
                    return new MemberDefinition(
                        uri,
                        document.ToRange(exported.DefinitionSpan),
                        $"{exported.ModuleName}.{name}");
                }
            }

            var firstWrite = analysis.Compilation.SemanticModel.UnifiedReferences
                .Where(item => string.Equals(item.Name, name, StringComparison.Ordinal) &&
                    item.Access.HasFlag(LuaReferenceAccess.Write))
                .OrderBy(static item => item.Span.Start)
                .FirstOrDefault();
            if (firstWrite is not null)
            {
                return new MemberDefinition(
                    analysis.Document.Uri,
                    analysis.Document.ToRange(firstWrite.Span),
                    name);
            }
        }

        // Receiver unresolved (chained or dynamic): jump when exactly one workspace
        // export matches the member name.
        var snapshot = workspace.GetSnapshot();
        var unique = snapshot?.ExportGraph.Symbols.Where(symbol =>
            !symbol.IsExternal &&
            (symbol.Path == name || symbol.Path.EndsWith("." + name, StringComparison.Ordinal))).ToArray();
        if (unique is { Length: 1 })
        {
            if (workspace.GetUri(unique[0].ModuleName) is { } uri &&
                workspace.TryGetDocument(uri, out var document))
            {
                return new MemberDefinition(
                    uri,
                    document.ToRange(unique[0].DefinitionSpan),
                    $"{unique[0].ModuleName}.{unique[0].Path}");
            }
        }

        return null;
    }

    private LuaWorkspaceExportSymbol? FindExportSymbol(string moduleName, string memberName)
    {
        var snapshot = workspace.GetSnapshot();
        return snapshot?.ExportGraph.Symbols.FirstOrDefault(symbol =>
            !symbol.IsExternal &&
            string.Equals(symbol.ModuleName, moduleName, StringComparison.Ordinal) &&
            (symbol.Path == memberName ||
             symbol.Path.EndsWith("." + memberName, StringComparison.Ordinal)));
    }

    /// <summary>The inferred type of a member, for hover text and completion details.</summary>
    private LuaType? ResolveMemberType(
        LanguageDocumentAnalysis analysis,
        LuaCodeReference member,
        Dictionary<int, string>? requireAliases = null)
    {
        var receiver = ResolveReceiverSymbol(analysis, member);
        if (receiver is null)
        {
            return null;
        }

        requireAliases ??= BuildRequireAliases(analysis);
        if (requireAliases.TryGetValue(receiver.Id, out var moduleName))
        {
            var exported = FindExportSymbol(moduleName, member.Name!);
            if (exported is not null)
            {
                return exported.Type;
            }
        }

        var receiverType = GetType(analysis, receiver);
        foreach (var (memberName, memberType) in CollectTypeMembers(receiverType))
        {
            if (string.Equals(memberName, member.Name, StringComparison.Ordinal))
            {
                return memberType;
            }
        }

        return null;
    }

    /// <summary>Flattens structural, prototype, and metatable shapes into named members.</summary>
    private static IEnumerable<(string Name, LuaType Type)> CollectTypeMembers(LuaType? type) =>
        CollectTypeMembers(type, []);

    private static IEnumerable<(string Name, LuaType Type)> CollectTypeMembers(
        LuaType? type,
        HashSet<LuaType> visited,
        int depth = 0)
    {
        if (type is null || depth >= MaximumMemberDepth || !visited.Add(type))
        {
            yield break;
        }

        switch (type)
        {
            case LuaUnionType union:
                foreach (var member in union.Types
                             .Where(static item => item.Kind is not LuaTypeKind.Nil)
                             .SelectMany(item => CollectTypeMembers(item, visited, depth + 1)))
                {
                    yield return member;
                }

                break;
            case LuaMetatableType metatable:
                foreach (var member in CollectTypeMembers(metatable.BaseType, visited, depth + 1))
                {
                    yield return member;
                }

                foreach (var member in CollectTypeMembers(metatable.MetatableType, visited, depth + 1))
                {
                    yield return member;
                }

                break;
            case LuaPrototypeType prototype:
                foreach (var member in CollectTypeMembers(prototype.Shape, visited, depth + 1))
                {
                    yield return member;
                }

                break;
            case LuaStructuralTableType table:
                foreach (var field in table.Fields)
                {
                    if (field.Name is null)
                    {
                        continue;
                    }

                    if (field.Name == "__index" &&
                        field.ValueType.Kind is not (LuaTypeKind.Nil or LuaTypeKind.Any or LuaTypeKind.Unknown))
                    {
                        // Inheritance: an __index field delegates missing members to its
                        // target table, so inherited members stay reachable.
                        foreach (var inherited in CollectTypeMembers(field.ValueType, visited, depth + 1))
                        {
                            yield return inherited;
                        }

                        continue;
                    }

                    yield return (field.Name, field.ValueType);
                }

                break;
        }
    }

    private sealed record CompletionContext(
        ImmutableArray<(string Label, int Kind, string Detail, string SortText)> Items,
        bool Handled);

    /// <summary>
    /// Detects cursor-sensitive completion contexts: member access after `.` or `:`
    /// and module names inside require("...").
    /// </summary>
    private CompletionContext BuildContextualCompletion(
        LanguageDocumentAnalysis analysis,
        int byteOffset,
        string textBeforeCursor)
    {
        var snapshot = workspace.GetSnapshot();

        var requireMatch = RequireStringRegex().Match(textBeforeCursor);
        if (requireMatch.Success)
        {
            if (snapshot is null)
            {
                return new CompletionContext([], true);
            }

            var builder = ImmutableArray.CreateBuilder<(string, int, string, string)>();
            foreach (var module in snapshot.Modules)
            {
                builder.Add((module.Identity.Name, 9, "module", "0" + module.Identity.Name));
            }

            return new CompletionContext(builder.ToImmutable(), true);
        }

        var memberMatch = MemberAccessRegex().Match(textBeforeCursor);
        if (!memberMatch.Success)
        {
            return new CompletionContext([], false);
        }

        var receiverName = memberMatch.Groups["receiver"].Value;
        var separator = memberMatch.Groups["separator"].Value;
        var methodsOnly = separator == ":";
        var items = new Dictionary<string, (string Label, int Kind, string Detail, string SortText)>(
            StringComparer.Ordinal);

        var receiver = analysis.Compilation.SemanticModel.Symbols.FirstOrDefault(symbol =>
            symbol.Name == receiverName);
        if (receiver is not null)
        {
            var aliases = BuildRequireAliases(analysis);
            if (aliases.TryGetValue(receiver.Id, out var moduleName) && snapshot is not null)
            {
                foreach (var symbol in snapshot.ExportGraph.Symbols.Where(symbol =>
                             string.Equals(symbol.ModuleName, moduleName, StringComparison.Ordinal) &&
                             !symbol.Path.Contains('.', StringComparison.Ordinal)))
                {
                    AddMemberItem(items, symbol.Path, symbol.Type, methodsOnly);
                }
            }
            else
            {
                foreach (var (name, type) in CollectTypeMembers(GetType(analysis, receiver)))
                {
                    AddMemberItem(items, name, type, methodsOnly);
                }
            }
        }

        return new CompletionContext(
            items.Values.OrderBy(static item => item.SortText, StringComparer.Ordinal).ToImmutableArray(),
            true);
    }

    private static void AddMemberItem(
        Dictionary<string, (string Label, int Kind, string Detail, string SortText)> items,
        string name,
        LuaType? type,
        bool methodsOnly)
    {
        if (methodsOnly && type is not (LuaFunctionType or LuaOverloadType))
        {
            return;
        }

        var isFunction = type is LuaFunctionType or LuaOverloadType;
        items.TryAdd(name, (
            name,
            isFunction ? 3 : 5,
            type?.DisplayName ?? "unknown",
            (isFunction ? "0" : "1") + name));
    }

    [GeneratedRegex(@"require\s*\(\s*[""'][^""']*$")]
    private static partial Regex RequireStringRegex();

    [GeneratedRegex(@"(?<receiver>[A-Za-z_][A-Za-z0-9_]*)\s*(?<sep>[.:])\s*[A-Za-z0-9_]*$")]
    private static partial Regex MemberAccessRegex();
}
