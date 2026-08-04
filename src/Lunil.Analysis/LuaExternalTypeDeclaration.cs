using System.Collections.Immutable;
using Lunil.EmmyLua;

namespace Lunil.Analysis;

/// <summary>
/// A type declaration (<c>---@class</c>, <c>---@alias</c>, or <c>---@enum</c>) together with its
/// attached member annotations, collected from a document that is external to the one being analyzed.
/// </summary>
public sealed record LuaExternalTypeDeclaration(
    string Name,
    LuaAnnotationSyntax Root,
    ImmutableArray<LuaAnnotationSyntax> Extras);

/// <summary>Extracts cross-file type declarations from an annotation document.</summary>
public static class LuaExternalTypeDeclarations
{
    public static ImmutableDictionary<string, LuaExternalTypeDeclaration> Collect(
        LuaAnnotationDocument document)
    {
        var builder = ImmutableDictionary.CreateBuilder<string, LuaExternalTypeDeclaration>(
            StringComparer.Ordinal);
        LuaAnnotationSyntax? current = null;
        var extras = new List<LuaAnnotationSyntax>();
        var previousEnd = 0;
        foreach (var annotation in document.Annotations)
        {
            if (current is not null && ContainsCode(document.Source, previousEnd, annotation.Span.Start))
            {
                var name = GetDeclarationName(current);
                if (name is not null)
                {
                    builder[name] = new LuaExternalTypeDeclaration(name, current, [.. extras]);
                }

                current = null;
                extras.Clear();
            }

            switch (annotation)
            {
                case LuaClassAnnotationSyntax or LuaAliasAnnotationSyntax or LuaEnumAnnotationSyntax:
                    Flush(builder, current, extras);
                    current = annotation;
                    extras.Clear();
                    break;
                case LuaFieldAnnotationSyntax or LuaOperatorAnnotationSyntax or
                    LuaOverloadAnnotationSyntax when current is LuaClassAnnotationSyntax:
                    extras.Add(annotation);
                    break;
                case LuaAliasContinuationAnnotationSyntax when
                    current is LuaAliasAnnotationSyntax or LuaEnumAnnotationSyntax:
                    extras.Add(annotation);
                    break;
                case LuaGenericAnnotationSyntax when current is LuaAliasAnnotationSyntax:
                    extras.Add(annotation);
                    break;
                default:
                    Flush(builder, current, extras);
                    current = null;
                    extras.Clear();
                    break;
            }

            previousEnd = annotation.Span.End;
        }

        Flush(builder, current, extras);
        return builder.ToImmutable();
    }

    private static void Flush(
        ImmutableDictionary<string, LuaExternalTypeDeclaration>.Builder builder,
        LuaAnnotationSyntax? current,
        List<LuaAnnotationSyntax> extras)
    {
        if (current is null)
        {
            return;
        }

        var name = GetDeclarationName(current);
        if (name is not null)
        {
            builder[name] = new LuaExternalTypeDeclaration(name, current, [.. extras]);
        }
    }

    private static string? GetDeclarationName(LuaAnnotationSyntax annotation) => annotation switch
    {
        LuaClassAnnotationSyntax @class => @class.Name,
        LuaAliasAnnotationSyntax alias => alias.Name,
        LuaEnumAnnotationSyntax @enum => @enum.Name,
        _ => null,
    };

    private static bool ContainsCode(Lunil.Core.Text.SourceText source, int start, int end)
    {
        var bytes = source.AsSpan()[start..end];
        foreach (var value in bytes)
        {
            if (value is not ((byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n'))
            {
                return true;
            }
        }

        return false;
    }
}
