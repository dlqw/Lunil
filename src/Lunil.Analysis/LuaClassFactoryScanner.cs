using System.Collections.Immutable;

namespace Lunil.Analysis;

/// <summary>
/// Byte-level scanner for the class-factory source patterns (extend/mixin/factory
/// definitions) that the managed runtime class model is built from. It works directly on
/// UTF-8 spans so callers can scan the byte-canonical document representation without
/// materializing text, and it is shared by the language server workspace.
/// </summary>
public static class LuaClassFactoryScanner
{
    /// <summary>
    /// Finds <c>local X = Y[:.]extend(</c> edges and configured class-factory definitions
    /// (<c>local X = class("Name", Base, ...)</c>) in raw UTF-8 source. A hand scan keeps
    /// the byte-canonical document representation usable (no Regex over materialized
    /// strings), skips recompiling a regex per rebuild, and rejects the keyword when
    /// it is embedded in a longer identifier — which the old pattern silently accepted.
    /// </summary>
    public static void ScanRuntimeClassEdges(
        ReadOnlySpan<byte> source,
        ImmutableDictionary<string, bool> factoryCalls,
        List<(string ClassName, string? BaseName)> edges)
    {
        var index = 0;
        while (index < source.Length)
        {
            var remaining = source[index..];
            var local = remaining.IndexOf("local"u8);
            if (local < 0)
            {
                return;
            }

            var cursor = index + local;
            index = cursor + 1;
            if (cursor > 0 && IsIdentifierByte(source[cursor - 1]))
            {
                continue;
            }

            cursor += 5;
            if (!SkipWhitespace(source, ref cursor, minimum: 1))
            {
                continue;
            }

            if (!TryReadIdentifier(source, ref cursor, out var className))
            {
                continue;
            }

            if (!SkipWhitespace(source, ref cursor, minimum: 0) || !At(source, ref cursor, (byte)'='))
            {
                continue;
            }

            if (!SkipWhitespace(source, ref cursor, minimum: 0) ||
                !TryReadIdentifier(source, ref cursor, out var rhs))
            {
                continue;
            }

            SkipWhitespace(source, ref cursor, minimum: 0);
            if (At(source, ref cursor, (byte)'(') && factoryCalls.TryGetValue(rhs, out var takesBases))
            {
                // A factory definition registers the class so cross-file chains can
                // resolve the module that declares it — under the local name (the shape
                // base arguments and extend edges reference) AND the declared string
                // name (the analyzer's prototypes are named by that string; the two
                // differ for dotted names like "AI.AbstractMoveAIAction").
                // "bases" factories also record each bare-identifier argument after the
                // name string as an inheritance edge under both names.
                if (ScanFactoryNameArgument(source, ref cursor, out var declaredName))
                {
                    var hasDeclared = declaredName.Length > 0 &&
                        !string.Equals(declaredName, className, StringComparison.Ordinal);
                    edges.Add((className, null));
                    if (hasDeclared)
                    {
                        edges.Add((declaredName, null));
                    }

                    if (takesBases)
                    {
                        foreach (var baseName in ScanFactoryBaseArguments(source, ref cursor))
                        {
                            edges.Add((className, baseName));
                            if (hasDeclared)
                            {
                                edges.Add((declaredName, baseName));
                            }
                        }
                    }
                }
            }
            else
            {
                if (!At(source, ref cursor, (byte)'.') && !At(source, ref cursor, (byte)':'))
                {
                    continue;
                }

                if (!source[cursor..].StartsWith("extend"u8))
                {
                    continue;
                }

                cursor += 6;
                SkipWhitespace(source, ref cursor, minimum: 0);
                if (At(source, ref cursor, (byte)'('))
                {
                    edges.Add((className, rhs));
                }
            }
        }
    }

    /// <summary>
    /// Consumes a factory call's first argument — a double-quoted string literal — and
    /// returns its value. The cursor sits on the first argument (the opening
    /// parenthesis was already consumed by <see cref="At"/>) and is advanced past the
    /// closing quote.
    /// </summary>
    public static bool ScanFactoryNameArgument(ReadOnlySpan<byte> source, ref int cursor, out string name)
    {
        name = string.Empty;
        if (!SkipWhitespace(source, ref cursor, minimum: 0) || !At(source, ref cursor, (byte)'"'))
        {
            return false;
        }

        var closing = source[cursor..].IndexOf((byte)'"');
        if (closing < 0)
        {
            return false;
        }

        name = System.Text.Encoding.UTF8.GetString(source.Slice(cursor, closing));
        cursor += closing + 1;
        return true;
    }

    /// <summary>
    /// Collects the bare-identifier base arguments of a "bases" factory call. Stops at
    /// the first compound or non-identifier argument — bases collected before it are
    /// kept. <see cref="At"/> consumes the separators it matches, so the loop
    /// deliberately leaves the cursor ON a separator for the next iteration.
    /// </summary>
    public static List<string> ScanFactoryBaseArguments(ReadOnlySpan<byte> source, ref int cursor)
    {
        var bases = new List<string>();
        while (cursor < source.Length)
        {
            SkipWhitespace(source, ref cursor, minimum: 0);
            if (At(source, ref cursor, (byte)')'))
            {
                return bases;
            }

            if (At(source, ref cursor, (byte)','))
            {
                continue;
            }

            if (TryReadIdentifier(source, ref cursor, out var baseName))
            {
                var after = cursor;
                SkipWhitespace(source, ref after, minimum: 0);
                if (after < source.Length &&
                    (source[after] == (byte)',' || source[after] == (byte)')'))
                {
                    bases.Add(baseName);
                    // Sit on the separator so the loop top consumes it.
                    cursor = after;
                    continue;
                }
            }

            return bases;
        }

        return bases;
    }

    /// <summary>
    /// Finds <c>[:.]mixin( X, Y</c> pairs in raw UTF-8 source, matching the previous
    /// mixin regex without materializing document text.
    /// </summary>
    public static void ScanClassMixins(
        ReadOnlySpan<byte> source,
        List<(string Target, string Source)> mixins)
    {
        var index = 0;
        while (index < source.Length)
        {
            var remaining = source[index..];
            var mixin = remaining.IndexOf("mixin"u8);
            if (mixin < 0)
            {
                return;
            }

            var cursor = index + mixin;
            index = cursor + 1;
            if (cursor == 0 || source[cursor - 1] is not ((byte)'.') and not ((byte)':'))
            {
                continue;
            }

            cursor += 5;
            if (!SkipWhitespace(source, ref cursor, minimum: 0) || !At(source, ref cursor, (byte)'('))
            {
                continue;
            }

            SkipWhitespace(source, ref cursor, minimum: 0);
            if (!TryReadIdentifier(source, ref cursor, out var target))
            {
                continue;
            }

            SkipWhitespace(source, ref cursor, minimum: 0);
            if (!At(source, ref cursor, (byte)','))
            {
                continue;
            }

            SkipWhitespace(source, ref cursor, minimum: 0);
            if (TryReadIdentifier(source, ref cursor, out var mixinSource))
            {
                mixins.Add((target, mixinSource));
            }
        }
    }

    public static bool IsIdentifierByte(byte value) =>
        value is >= (byte)'0' and <= (byte)'9' or >= (byte)'A' and <= (byte)'Z' or
            >= (byte)'a' and <= (byte)'z' or (byte)'_';

    public static bool SkipWhitespace(ReadOnlySpan<byte> source, ref int cursor, int minimum)
    {
        var skipped = 0;
        while (cursor < source.Length && source[cursor] is (byte)' ' or (byte)'\t' or (byte)'\n' or (byte)'\r')
        {
            cursor++;
            skipped++;
        }

        return skipped >= minimum && cursor < source.Length;
    }

    public static bool At(ReadOnlySpan<byte> source, ref int cursor, byte value)
    {
        if (cursor >= source.Length || source[cursor] != value)
        {
            return false;
        }

        cursor++;
        return true;
    }

    public static bool TryReadIdentifier(ReadOnlySpan<byte> source, ref int cursor, out string identifier)
    {
        var start = cursor;
        if (cursor >= source.Length ||
            source[cursor] is not ((byte)'_' or >= (byte)'A' and <= (byte)'Z' or >= (byte)'a' and <= (byte)'z'))
        {
            identifier = string.Empty;
            return false;
        }

        cursor++;
        while (cursor < source.Length && IsIdentifierByte(source[cursor]))
        {
            cursor++;
        }

        identifier = System.Text.Encoding.UTF8.GetString(source[start..cursor]);
        return true;
    }
}
