using System.Collections.Immutable;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;

namespace Lunil.EmmyLua;

/// <summary>Immutable annotation front-end result over one Lua source document.</summary>
public sealed record LuaAnnotationDocument(
    SourceText Source,
    LuaAnnotationDialect Dialect,
    ImmutableArray<LuaAnnotationSyntax> Annotations,
    ImmutableArray<Diagnostic> Diagnostics,
    int ParseErrorCount)
{
    public static LuaAnnotationDocument Empty(SourceText source, LuaAnnotationDialect dialect)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new LuaAnnotationDocument(
            source,
            dialect,
            [],
            [],
            0);
    }
}
