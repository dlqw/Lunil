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

    internal sealed record RequireCall(string ModuleName, TextSpan StringSpan, LuaSymbol? AliasSymbol);

    /// <summary>
    /// Lazily built per-analysis lookups shared by request handlers so repeated requests
    /// for the same document version do not rescan the whole model.
    /// </summary>
    internal sealed class ServiceCaches
    {
        private readonly object _gate = new();
        private LanguageDocumentAnalysis? _owner;
        private Dictionary<int, LuaType>? _symbolTypes;
        private ImmutableArray<RequireCall> _requireCalls;

        public static ServiceCaches Get(LanguageDocumentAnalysis analysis)
        {
            var caches = analysis.ServiceCaches;
            lock (caches._gate)
            {
                caches._owner ??= analysis;
            }

            return caches;
        }        /// <summary>Symbol id to its inferred type; replaces a linear scan per lookup.</summary>
        public Dictionary<int, LuaType> SymbolTypes
        {
            get
            {
                lock (_gate)
                {
                    if (_symbolTypes is null)
                    {
                        var builder = new Dictionary<int, LuaType>();
                        foreach (var info in _owner!.Compilation.Analysis.Symbols)
                        {
                            // First info wins, matching the previous FirstOrDefault lookup.
                            builder.TryAdd(info.Symbol.Id, info.InferredType);
                        }

                        _symbolTypes = builder;
                    }

                    return _symbolTypes;
                }
            }
        }

        /// <summary>Every require("...") call, computed once per analysis.</summary>
        public ImmutableArray<RequireCall> RequireCalls
        {
            get
            {
                lock (_gate)
                {
                    if (_requireCalls.IsDefault)
                    {
                        _requireCalls = FindRequireCallsUncached(_owner!);
                    }

                    return _requireCalls;
                }
            }
        }
    }

    /// <summary>Every require("...") call in the document with its string span and bound alias.</summary>
    private static ImmutableArray<RequireCall> FindRequireCalls(LanguageDocumentAnalysis analysis) =>
        ServiceCaches.Get(analysis).RequireCalls;

    private static ImmutableArray<RequireCall> FindRequireCallsUncached(LanguageDocumentAnalysis analysis)
    {
        var builder = ImmutableArray.CreateBuilder<RequireCall>();
        var root = analysis.Compilation.SemanticModel.Syntax.Root;
        // Declaration statements are collected once and kept in document order so each
        // require call finds its alias without re-walking the whole syntax tree.
        var declarations = root.DescendantNodes()
            .Where(static node => node.Kind is LuaSyntaxKind.LocalDeclarationStatement or
                LuaSyntaxKind.AssignmentStatement)
            .OrderBy(static node => node.Span.Start)
            .ToArray();
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
            LuaSyntaxNode? declaration = null;
            foreach (var candidate in declarations)
            {
                if (candidate.Span.Start > call.Span.Start)
                {
                    break;
                }

                if (candidate.Span.End >= call.Span.End)
                {
                    declaration = candidate;
                    break;
                }
            }

            var aliasToken = declaration?.DescendantTokens().FirstOrDefault(static token =>
                token.Kind == Lunil.Syntax.Lexing.LuaTokenKind.Identifier && !token.IsMissing);
            var alias = aliasToken is null ? null : analysis.Compilation.SemanticModel.Symbols.FirstOrDefault(item =>
                item.DeclaringSpan == aliasToken.Span);
            builder.Add(new RequireCall(moduleName, literalToken.Span, alias));
        }

        return builder.ToImmutable();
    }

    internal enum AnnotationElementKind
    {
        TypeName,
        PrimitiveTypeName,
        ClassDeclaration,
        AliasDeclaration,
        EnumDeclaration,
        FieldName,
        ParamName,
    }

    internal readonly record struct AnnotationElement(
        AnnotationElementKind Kind,
        string Name,
        TextSpan Span,
        Lunil.EmmyLua.LuaAnnotationSyntax Owner);

    private static readonly HashSet<string> AnnotationBuiltIns = new(StringComparer.Ordinal)
    {
        "any", "unknown", "never", "nil", "boolean", "bool", "true", "false",
        "integer", "int", "float", "number", "string", "str", "table", "function",
        "thread", "userdata", "lightuserdata", "void", "self",
    };

    /// <summary>
    /// The annotation element under the cursor: a type name inside any type expression,
    /// a declared class/alias/enum name, or a field/param name. Annotation text is not
    /// bound, so navigation resolves it structurally.
    /// </summary>
    private static AnnotationElement? FindAnnotationElementAt(
        LanguageDocumentAnalysis analysis,
        int offset)
    {
        AnnotationElement? best = null;
        var bestLength = int.MaxValue;
        foreach (var annotation in analysis.Compilation.Annotations.Annotations)
        {
            Consider(annotation, AnnotationElementKind.ClassDeclaration, NameOf(annotation), offset, analysis, ref best, ref bestLength);
            WalkAnnotationTypes(annotation, type =>
            {
                if (type is Lunil.EmmyLua.LuaNamedTypeSyntax named &&
                    Contains(type.Span, offset) && type.Span.Length < bestLength)
                {
                    // Primitive names (`number`, `string`, ...) get their own hover card;
                    // named user types navigate to their declaration.
                    best = new AnnotationElement(
                        AnnotationBuiltIns.Contains(named.Name)
                            ? AnnotationElementKind.PrimitiveTypeName
                            : AnnotationElementKind.TypeName,
                        named.Name,
                        type.Span,
                        annotation);
                    bestLength = type.Span.Length;
                }
            });
        }

        return best;

        static string? NameOf(Lunil.EmmyLua.LuaAnnotationSyntax annotation) => annotation switch
        {
            Lunil.EmmyLua.LuaClassAnnotationSyntax @class => @class.Name,
            Lunil.EmmyLua.LuaAliasAnnotationSyntax alias => alias.Name,
            Lunil.EmmyLua.LuaEnumAnnotationSyntax @enum => @enum.Name,
            _ => null,
        };

        void Consider(
            Lunil.EmmyLua.LuaAnnotationSyntax annotation,
            AnnotationElementKind kind,
            string? name,
            int at,
            LanguageDocumentAnalysis a,
            ref AnnotationElement? current,
            ref int length)
        {
            var span = annotation switch
            {
                Lunil.EmmyLua.LuaClassAnnotationSyntax @class => @class.NameSpan,
                Lunil.EmmyLua.LuaAliasAnnotationSyntax alias => alias.NameSpan,
                Lunil.EmmyLua.LuaEnumAnnotationSyntax @enum => @enum.NameSpan,
                _ => default,
            };
            if (name is not null && span.Length > 0 && Contains(span, at) && span.Length < length)
            {
                current = new AnnotationElement(kind, name, span, annotation);
                length = span.Length;
            }
        }
    }

    private static void WalkAnnotationTypes(
        Lunil.EmmyLua.LuaAnnotationSyntax annotation,
        Action<Lunil.EmmyLua.LuaTypeSyntax> visit)
    {
        switch (annotation)
        {
            case Lunil.EmmyLua.LuaClassAnnotationSyntax @class:
                WalkTypes(@class.BaseTypes, visit);
                break;
            case Lunil.EmmyLua.LuaAliasAnnotationSyntax alias:
                if (alias.Type is not null) WalkTypes([alias.Type], visit);
                break;
            case Lunil.EmmyLua.LuaEnumAnnotationSyntax @enum:
                if (@enum.KeyType is not null) WalkTypes([@enum.KeyType], visit);
                break;
            case Lunil.EmmyLua.LuaFieldAnnotationSyntax field:
                WalkTypes([field.Type], visit);
                break;
            case Lunil.EmmyLua.LuaParamAnnotationSyntax param:
                WalkTypes([param.Type], visit);
                break;
            case Lunil.EmmyLua.LuaTypeAnnotationSyntax type:
                WalkTypes(type.Types, visit);
                break;
            case Lunil.EmmyLua.LuaVarargAnnotationSyntax vararg:
                WalkTypes([vararg.Type], visit);
                break;
            case Lunil.EmmyLua.LuaReturnAnnotationSyntax @return:
                foreach (var returned in @return.Returns)
                {
                    WalkTypes([returned.Type], visit);
                }

                break;
            case Lunil.EmmyLua.LuaOverloadAnnotationSyntax overload:
                WalkTypes([overload.Type], visit);
                break;
            case Lunil.EmmyLua.LuaAliasContinuationAnnotationSyntax continuation:
                WalkTypes([continuation.Type], visit);
                break;
            case Lunil.EmmyLua.LuaCastAnnotationSyntax cast:
                WalkTypes([cast.Type], visit);
                break;
            case Lunil.EmmyLua.LuaOperatorAnnotationSyntax @operator:
                if (@operator.OperandType is not null) WalkTypes([@operator.OperandType], visit);
                WalkTypes([@operator.ResultType], visit);
                break;
            case Lunil.EmmyLua.LuaGenericAnnotationSyntax generic:
                foreach (var parameter in generic.Parameters)
                {
                    if (parameter.Constraint is not null) WalkTypes([parameter.Constraint], visit);
                }

                break;
        }
    }

    private static void WalkTypes(
        System.Collections.Immutable.ImmutableArray<Lunil.EmmyLua.LuaTypeSyntax> types,
        Action<Lunil.EmmyLua.LuaTypeSyntax> visit)
    {
        foreach (var type in types)
        {
            WalkType(type, visit);
        }
    }

    private static void WalkType(Lunil.EmmyLua.LuaTypeSyntax? type, Action<Lunil.EmmyLua.LuaTypeSyntax> visit)
    {
        switch (type)
        {
            case null:
                return;
            case Lunil.EmmyLua.LuaNamedTypeSyntax:
                visit(type);
                return;
            case Lunil.EmmyLua.LuaUnionTypeSyntax union:
                WalkTypes(union.Types, visit);
                return;
            case Lunil.EmmyLua.LuaIntersectionTypeSyntax intersection:
                WalkTypes(intersection.Types, visit);
                return;
            case Lunil.EmmyLua.LuaNullableTypeSyntax nullable:
                WalkType(nullable.Type, visit);
                return;
            case Lunil.EmmyLua.LuaArrayTypeSyntax array:
                WalkType(array.ElementType, visit);
                return;
            case Lunil.EmmyLua.LuaTupleTypeSyntax tuple:
                WalkTypes(tuple.Elements, visit);
                return;
            case Lunil.EmmyLua.LuaVarargTypeSyntax vararg:
                WalkType(vararg.ElementType, visit);
                return;
            case Lunil.EmmyLua.LuaFunctionTypeSyntax function:
                foreach (var parameter in function.Parameters)
                {
                    WalkType(parameter.Type, visit);
                }

                WalkTypes(function.Returns, visit);
                return;
            case Lunil.EmmyLua.LuaTableTypeSyntax table:
                foreach (var field in table.Fields)
                {
                    if (field.KeyType is not null) WalkType(field.KeyType, visit);
                    WalkType(field.ValueType, visit);
                }

                return;
            default:
                visit(type);
                return;
        }
    }

    /// <summary>
    /// The symbol whose declaration contains the offset, when no reference does: the
    /// cursor sits on a declaration token (`local Movable = {}`), which the binder does
    /// not record as a reference.
    /// </summary>
    private static LuaSymbol? FindDeclaredSymbolAt(LanguageDocumentAnalysis analysis, int offset)
    {
        LuaSymbol? best = null;
        var bestLength = int.MaxValue;
        foreach (var symbol in analysis.Compilation.SemanticModel.Symbols)
        {
            if (symbol.Kind == LuaSymbolKind.Environment || symbol.DeclaringSpan.Length == 0)
            {
                continue;
            }

            var span = NormalizeDeclaringSpan(symbol, analysis.Document);
            if (Contains(span, offset) && span.Length < bestLength)
            {
                best = symbol;
                bestLength = span.Length;
            }
        }

        return best;
    }

    /// <summary>
    /// The module behind a class value: a require alias resolves to the required module,
    /// and the defining module's exported class local (its root export declares a class
    /// named like the symbol) resolves to that module.
    /// </summary>
    private bool TryResolveClassValueModule(
        LanguageDocumentAnalysis analysis,
        LuaSymbol symbol,
        out string module)
    {
        var aliases = BuildRequireAliases(analysis);
        if (aliases.TryGetValue(symbol.Id, out var required))
        {
            module = required;
            return true;
        }

        module = analysis.Module.Name;
        var root = FindModuleRootExport(module);
        if (root?.Type is LuaPrototypeType prototype &&
            string.Equals(prototype.Name, symbol.Name, StringComparison.Ordinal))
        {
            return true;
        }

        module = string.Empty;
        return false;
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
    /// Resolves a member reference to its definition: module exports first (cross-file,
    /// including base-class modules through the declared inheritance chain), then
    /// same-file member writes for locally built tables and classes.
    /// </summary>
    private MemberDefinition? ResolveMemberDefinition(
        LanguageDocumentAnalysis analysis,
        LuaCodeReference member)
    {
        var name = member.Name!;
        foreach (var resolution in ResolveMemberExportChain(analysis, member))
        {
            if (workspace.GetUri(resolution.ModuleName) is { } uri &&
                workspace.TryGetDocument(uri, out var document))
            {
                return new MemberDefinition(
                    uri,
                    document.ToRange(resolution.Symbol.DefinitionSpan),
                    $"{resolution.ModuleName}.{name}");
            }
        }

        var receiver = ResolveReceiverSymbol(analysis, member);
        if (receiver is not null)
        {
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

        return null;
    }

    private LuaWorkspaceExportSymbol? FindExportSymbol(string moduleName, string memberName) =>
        workspace.GetSnapshot() is { } snapshot
            ? FindExportSymbolIn(snapshot, moduleName, memberName)
            : null;

    /// <summary>
    /// Resolves a member reference to the workspace export symbol it belongs to, if any:
    /// a require-alias receiver maps to the required module's exports, a resolved local
    /// receiver maps to the current module's own exports (the definition-site pattern
    /// <c>function M.f</c>), and an unresolved receiver accepts a unique same-named export.
    /// Member lookup then follows the <c>@class</c> inheritance chain declared on the
    /// module's class, so inherited members (<c>Base:extend</c> on a subclass) resolve to
    /// the base module that declares them.
    /// </summary>
    private LuaWorkspaceExportSymbol? TryResolveMemberExport(
        LanguageDocumentAnalysis analysis,
        LuaCodeReference member)
    {
        foreach (var resolution in ResolveMemberExportChain(analysis, member))
        {
            return resolution.Symbol;
        }

        return null;
    }

    private sealed record MemberExportResolution(LuaWorkspaceExportSymbol Symbol, string ModuleName);

    private IEnumerable<MemberExportResolution> ResolveMemberExportChain(
        LanguageDocumentAnalysis analysis,
        LuaCodeReference member,
        int maximumHops = 8)
    {
        var snapshot = workspace.GetSnapshot();
        if (snapshot is null)
        {
            yield break;
        }

        var name = member.Name!;
        var receiver = ResolveReceiverSymbol(analysis, member);
        if (receiver is not null)
        {
            var aliases = BuildRequireAliases(analysis);
            var moduleName = aliases.TryGetValue(receiver.Id, out var required)
                ? required
                : analysis.Module.Name;
            foreach (var symbol in FindChainExports(
                         workspace.GetClassDeclarations(), snapshot, moduleName, name, maximumHops))
            {
                yield return symbol;
            }

            yield break;
        }

        // Receiver unresolved (chained or dynamic): treat like definition's unique-export
        // fallback.
        var unique = snapshot.ExportGraph.Symbols.Where(symbol =>
            !symbol.IsExternal &&
            (symbol.Path == name || symbol.Path.EndsWith("." + name, StringComparison.Ordinal))).ToArray();
        if (unique is { Length: 1 })
        {
            yield return new MemberExportResolution(unique[0], unique[0].ModuleName);
        }
    }

    /// <summary>
    /// Walks a module's export graph and the <c>@class</c> inheritance chain declared in
    /// the workspace: the module's own exports first, then the modules declaring each
    /// base class, so inherited members resolve where they are defined.
    /// </summary>
    private static IEnumerable<MemberExportResolution> FindChainExports(
        ImmutableArray<WorkspaceClassDeclaration> classDeclarations,
        LuaWorkspaceCompactSnapshot snapshot,
        string moduleName,
        string memberName,
        int maximumHops)
    {
        var chain = CollectChainModules(classDeclarations, moduleName, maximumHops);
        foreach (var module in chain)
        {
            var exported = FindExportSymbolIn(snapshot, module, memberName);
            if (exported is not null)
            {
                yield return new MemberExportResolution(exported, module);
            }
        }
    }

    /// <summary>
    /// A module and every module declaring a base class of its classes, nearest first.
    /// Member completion offers the exports of all of them so inherited members appear.
    /// </summary>
    private static IEnumerable<string> CollectChainModules(
        ImmutableArray<WorkspaceClassDeclaration> classDeclarations,
        string moduleName,
        int maximumHops = 8)
    {
        var classesByModule = classDeclarations
            .GroupBy(static item => item.ModuleName, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.ToArray(),
                StringComparer.Ordinal);
        var modulesByClass = classDeclarations
            .GroupBy(static item => item.Name, StringComparer.Ordinal)
            .ToDictionary(
                static group => group.Key,
                static group => group.First().ModuleName,
                StringComparer.Ordinal);
        var visitedModules = new HashSet<string>(StringComparer.Ordinal);
        var visitedClasses = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Queue<string>();
        pending.Enqueue(moduleName);
        var processed = 0;
        while (pending.Count > 0 && processed < maximumHops)
        {
            var module = pending.Dequeue();
            if (!visitedModules.Add(module))
            {
                continue;
            }

            processed++;
            yield return module;
            foreach (var @class in classesByModule.GetValueOrDefault(module) ?? [])
            {
                foreach (var baseName in @class.BaseNames)
                {
                    if (visitedClasses.Add(baseName) &&
                        modulesByClass.TryGetValue(baseName, out var baseModule))
                    {
                        pending.Enqueue(baseModule);
                    }
                }
            }
        }
    }

    private static LuaWorkspaceExportSymbol? FindExportSymbolIn(
        LuaWorkspaceCompactSnapshot snapshot,
        string moduleName,
        string memberName)
    {
        return snapshot.ExportGraph.Symbols.FirstOrDefault(symbol =>
            !symbol.IsExternal &&
            string.Equals(symbol.ModuleName, moduleName, StringComparison.Ordinal) &&
            (symbol.Path == memberName ||
             symbol.Path.EndsWith("." + memberName, StringComparison.Ordinal)));
    }

    /// <summary>The identifier a member reference's receiver names, when simple (`string`).</summary>
    private static string? GetReceiverName(LanguageDocumentAnalysis analysis, LuaCodeReference member)
    {
        if (member.ReceiverSpan is not { Length: > 0 } receiver)
        {
            return null;
        }

        var start = analysis.Document.ToCharOffset(analysis.Document.ToPosition(receiver.Start));
        var end = analysis.Document.ToCharOffset(analysis.Document.ToPosition(receiver.End));
        var text = analysis.Document.Text[start..Math.Min(end, analysis.Document.Text.Length)];
        var separator = text.IndexOfAny(['.', ':']);
        var name = (separator < 0 ? text : text[..separator]).Trim();
        return name.Length == 0 ? null : name;
    }

    /// <summary>The inferred type of a member, for hover text and completion details.</summary>
    private LuaType? ResolveMemberType(
        LanguageDocumentAnalysis analysis,
        LuaCodeReference member,
        Dictionary<int, string>? requireAliases = null)
    {
        // Stdlib library tables (`string.format`, `math.floor`) and host globals declared
        // by library stubs (`Game.connect`) are known globals whose members carry
        // annotated signatures.
        if (GetReceiverName(analysis, member) is { } globalReceiver &&
            workspace.TryGetKnownGlobalType(globalReceiver, out var libraryType))
        {
            foreach (var (memberName, memberType) in CollectTypeMembers(libraryType))
            {
                if (string.Equals(memberName, member.Name, StringComparison.Ordinal))
                {
                    return memberType;
                }
            }

            return null;
        }

        foreach (var resolution in ResolveMemberExportChain(analysis, member))
        {
            return resolution.Symbol.Type;
        }

        var receiver = ResolveReceiverSymbol(analysis, member);
        if (receiver is null)
        {
            return null;
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
        if (receiver is null && workspace.TryGetKnownGlobalType(receiverName, out var stdlibType))
        {
            // Stdlib library tables (`table.`, `string.`) and host globals from library
            // stubs have no lexical symbol; their members complete from declared types.
            foreach (var (name, type) in CollectTypeMembers(stdlibType))
            {
                AddMemberItem(items, name, type, methodsOnly);
            }

            return new CompletionContext(
                items.Values.OrderBy(static item => item.SortText, StringComparer.Ordinal).ToImmutableArray(),
                true);
        }

        if (receiver is not null)
        {
            var aliases = BuildRequireAliases(analysis);
            if (aliases.TryGetValue(receiver.Id, out var moduleName) && snapshot is not null)
            {
                foreach (var chainModule in CollectChainModules(
                             workspace.GetClassDeclarations(), moduleName))
                {
                    foreach (var symbol in snapshot.ExportGraph.Symbols.Where(symbol =>
                                 string.Equals(symbol.ModuleName, chainModule, StringComparison.Ordinal) &&
                                 !symbol.Path.Contains('.', StringComparison.Ordinal)))
                    {
                        AddMemberItem(items, symbol.Path, symbol.Type, methodsOnly);
                    }
                }
            }
            else if (workspace.TryGetKnownGlobalType(receiverName, out var builtinLibraryType))
            {
                // Stdlib library tables and host-injected globals complete from their
                // declared types (embedded definitions or library stubs).
                foreach (var (name, type) in CollectTypeMembers(builtinLibraryType))
                {
                    AddMemberItem(items, name, type, methodsOnly);
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
