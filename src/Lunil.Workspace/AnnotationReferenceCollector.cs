using System.Collections.Immutable;
using Lunil.EmmyLua;
using Lunil.Core.Text;

namespace Lunil.Workspace;

/// <summary>
/// Collects the annotation elements that name a type: named type references inside any
/// type expression, and the declaration names of classes, aliases, and enums. Built-in
/// type names are skipped.
/// </summary>
internal static class AnnotationReferenceCollector
{
    private static readonly HashSet<string> BuiltIns = new(StringComparer.Ordinal)
    {
        "any", "unknown", "never", "nil", "boolean", "bool", "true", "false",
        "integer", "int", "float", "number", "string", "str", "table", "function",
        "thread", "userdata", "lightuserdata", "void", "self",
    };

    public static void Collect(LuaAnnotationDocument document, Action<(string Name, TextSpan Span)> add)
    {
        foreach (var annotation in document.Annotations)
        {
            switch (annotation)
            {
                case LuaClassAnnotationSyntax @class:
                    AddName(add, @class.Name, @class.NameSpan);
                    Walk(@class.BaseTypes, add);
                    break;
                case LuaAliasAnnotationSyntax alias:
                    AddName(add, alias.Name, alias.NameSpan);
                    if (alias.Type is not null)
                    {
                        Walk([alias.Type], add);
                    }

                    break;
                case LuaEnumAnnotationSyntax @enum:
                    AddName(add, @enum.Name, @enum.NameSpan);
                    if (@enum.KeyType is not null)
                    {
                        Walk([@enum.KeyType], add);
                    }

                    break;
                case LuaFieldAnnotationSyntax field:
                    Walk([field.Type], add);
                    break;
                case LuaParamAnnotationSyntax param:
                    Walk([param.Type], add);
                    break;
                case LuaTypeAnnotationSyntax type:
                    Walk(type.Types, add);
                    break;
                case LuaVarargAnnotationSyntax vararg:
                    Walk([vararg.Type], add);
                    break;
                case LuaReturnAnnotationSyntax @return:
                    foreach (var returned in @return.Returns)
                    {
                        Walk([returned.Type], add);
                    }

                    break;
                case LuaOverloadAnnotationSyntax overload:
                    Walk([overload.Type], add);
                    break;
                case LuaAliasContinuationAnnotationSyntax continuation:
                    Walk([continuation.Type], add);
                    break;
                case LuaCastAnnotationSyntax cast:
                    Walk([cast.Type], add);
                    break;
                case LuaOperatorAnnotationSyntax @operator:
                    if (@operator.OperandType is not null)
                    {
                        Walk([@operator.OperandType], add);
                    }

                    Walk([@operator.ResultType], add);
                    break;
                case LuaGenericAnnotationSyntax generic:
                    foreach (var parameter in generic.Parameters)
                    {
                        if (parameter.Constraint is not null)
                        {
                            Walk([parameter.Constraint], add);
                        }
                    }

                    break;
            }
        }
    }

    private static void AddName(Action<(string Name, TextSpan Span)> add, string name, TextSpan span)
    {
        if (span.Length > 0 && !BuiltIns.Contains(name))
        {
            add((name, span));
        }
    }

    private static void Walk(ImmutableArray<LuaTypeSyntax> types, Action<(string Name, TextSpan Span)> add)
    {
        foreach (var type in types)
        {
            Walk(type, add);
        }
    }

    private static void Walk(LuaTypeSyntax? type, Action<(string Name, TextSpan Span)> add)
    {
        switch (type)
        {
            case null:
                return;
            case LuaNamedTypeSyntax named:
                AddName(add, named.Name, named.Span);
                Walk(named.TypeArguments, add);
                return;
            case LuaUnionTypeSyntax union:
                Walk(union.Types, add);
                return;
            case LuaIntersectionTypeSyntax intersection:
                Walk(intersection.Types, add);
                return;
            case LuaNullableTypeSyntax nullable:
                Walk(nullable.Type, add);
                return;
            case LuaArrayTypeSyntax array:
                Walk(array.ElementType, add);
                return;
            case LuaTupleTypeSyntax tuple:
                Walk(tuple.Elements, add);
                return;
            case LuaVarargTypeSyntax vararg:
                Walk(vararg.ElementType, add);
                return;
            case LuaFunctionTypeSyntax function:
                foreach (var parameter in function.Parameters)
                {
                    Walk(parameter.Type, add);
                }

                Walk(function.Returns, add);
                return;
            case LuaTableTypeSyntax table:
                foreach (var field in table.Fields)
                {
                    if (field.KeyType is not null)
                    {
                        Walk(field.KeyType, add);
                    }

                    Walk(field.ValueType, add);
                }

                return;
        }
    }
}
