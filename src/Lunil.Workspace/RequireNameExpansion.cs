using System.Collections.Immutable;

namespace Lunil.Workspace;

/// <summary>
/// Expands a <c>require</c> string into candidate module identities using a list of
/// search-path roots. The raw name is always the first candidate; each root contributes a
/// dotted prefix, so <c>require("A.B.C")</c> under root <c>Libs/client</c> yields
/// <c>Libs.client.A.B.C</c>. Identity matching elsewhere stays exact (Ordinal), so callers
/// probe candidates in order until one hits a known module.
/// </summary>
public static class RequireNameExpansion
{
    public static ImmutableArray<string> Expand(
        string requestedName,
        ImmutableArray<string> roots)
    {
        LunilGuard.NotNull(requestedName);
        var normalizedName = NormalizeSeparators(requestedName);
        var builder = ImmutableArray.CreateBuilder<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string candidate)
        {
            if (candidate.Length > 0 && seen.Add(candidate))
            {
                builder.Add(candidate);
            }
        }

        Add(normalizedName);
        foreach (var root in roots)
        {
            var prefix = NormalizeRoot(root);
            if (prefix.Length > 0)
            {
                Add(prefix + "." + normalizedName);
            }
        }

        return builder.ToImmutable();
    }

    private static string NormalizeSeparators(string value) =>
        value.Replace('\\', '.').Replace('/', '.');

    private static string NormalizeRoot(string root) =>
        root.Trim().Replace('\\', '.').Replace('/', '.').Trim('.');
}
