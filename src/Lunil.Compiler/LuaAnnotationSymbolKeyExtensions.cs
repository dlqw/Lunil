using Lunil.EmmyLua;
using Lunil.Semantics.Binding;

namespace Lunil.Compiler;

/// <summary>Creates and resolves stable keys for named annotation declarations.</summary>
public static class LuaAnnotationSymbolKeyExtensions
{
    /// <summary>Gets a stable key for a class, alias, or enum annotation.</summary>
    public static LuaSymbolKey GetAnnotationKey(
        this LuaCompilationResult compilation,
        LuaAnnotationSyntax annotation,
        string moduleIdentity)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        ArgumentNullException.ThrowIfNull(annotation);
        if (!compilation.Annotations.Annotations.Any(candidate => ReferenceEquals(candidate, annotation)))
        {
            throw new ArgumentException(
                "The annotation does not belong to this compilation.",
                nameof(annotation));
        }

        var declaration = GetDeclaration(annotation);
        var matches = compilation.Annotations.Annotations
            .Select(static candidate => TryGetDeclaration(candidate, out var value)
                ? (AnnotationDeclaration?)value
                : null)
            .Where(candidate => candidate is not null &&
                string.Equals(candidate.Value.Kind, declaration.Kind, StringComparison.Ordinal) &&
                string.Equals(candidate.Value.Name, declaration.Name, StringComparison.Ordinal))
            .ToArray();
        var ordinal = matches.Length > 1
            ? compilation.Annotations.Annotations
                .TakeWhile(candidate => !ReferenceEquals(candidate, annotation))
                .Count(candidate => TryGetDeclaration(candidate, out var value) &&
                    string.Equals(value.Kind, declaration.Kind, StringComparison.Ordinal) &&
                    string.Equals(value.Name, declaration.Name, StringComparison.Ordinal))
            : 0;
        return LuaSymbolKey.CreateAnnotation(
            moduleIdentity,
            declaration.Kind,
            declaration.Name,
            ordinal);
    }

    /// <summary>Resolves a class, alias, or enum annotation key in this compilation.</summary>
    public static LuaAnnotationSyntax? ResolveAnnotationKey(
        this LuaCompilationResult compilation,
        LuaSymbolKey key,
        string moduleIdentity)
    {
        ArgumentNullException.ThrowIfNull(compilation);
        foreach (var annotation in compilation.Annotations.Annotations)
        {
            if (TryGetDeclaration(annotation, out _) &&
                compilation.GetAnnotationKey(annotation, moduleIdentity) == key)
            {
                return annotation;
            }
        }

        return null;
    }

    private static AnnotationDeclaration GetDeclaration(LuaAnnotationSyntax annotation) =>
        TryGetDeclaration(annotation, out var declaration)
            ? declaration
            : throw new ArgumentException(
                "Only class, alias, and enum annotations have stable declaration keys.",
                nameof(annotation));

    private static bool TryGetDeclaration(
        LuaAnnotationSyntax annotation,
        out AnnotationDeclaration declaration)
    {
        declaration = annotation switch
        {
            LuaClassAnnotationSyntax @class => new("class", @class.Name),
            LuaAliasAnnotationSyntax alias => new("alias", alias.Name),
            LuaEnumAnnotationSyntax @enum => new("enum", @enum.Name),
            _ => default,
        };
        return declaration.Kind is not null;
    }

    private readonly record struct AnnotationDeclaration(string Kind, string Name);
}
