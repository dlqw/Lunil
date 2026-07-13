using System.Collections.Immutable;
using Lunil.Compiler;
using Lunil.Core.Text;

namespace Lunil.Workspace;

public sealed record LuaModuleResolutionRequest(
    LuaModuleIdentity Origin,
    string RequestedName,
    TextSpan Span);

/// <summary>Resolves a logical require name without changing runtime package-loader behavior.</summary>
public interface ILuaModuleResolver
{
    ValueTask<LuaWorkspaceDocument?> ResolveAsync(
        LuaModuleResolutionRequest request,
        CancellationToken cancellationToken = default);
}

/// <summary>Deterministic resolver backed by an immutable set of workspace documents.</summary>
public sealed class LuaInMemoryModuleResolver : ILuaModuleResolver
{
    private readonly ImmutableDictionary<string, LuaWorkspaceDocument> _documents;

    public LuaInMemoryModuleResolver(IEnumerable<LuaWorkspaceDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);
        _documents = documents.ToImmutableDictionary(
            static document => document.Module.Name,
            StringComparer.Ordinal);
    }

    public ValueTask<LuaWorkspaceDocument?> ResolveAsync(
        LuaModuleResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(
            _documents.GetValueOrDefault(request.RequestedName));
    }
}

public sealed record LuaFileSystemModuleResolverOptions
{
    public ImmutableArray<string> RootDirectories { get; init; } = [];

    public ImmutableArray<string> PathPatterns { get; init; } = ["?.lua", "?/init.lua"];

    public long MaximumFileBytes { get; init; } = 64L * 1024 * 1024;
}

/// <summary>Root-confined file resolver with Lua-style <c>?</c> path templates.</summary>
public sealed class LuaFileSystemModuleResolver : ILuaModuleResolver
{
    private readonly ImmutableArray<string> _roots;
    private readonly ImmutableArray<string> _patterns;
    private readonly long _maximumFileBytes;

    public LuaFileSystemModuleResolver(LuaFileSystemModuleResolverOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.RootDirectories.IsDefaultOrEmpty)
        {
            throw new ArgumentException("At least one module root is required.", nameof(options));
        }

        if (options.PathPatterns.IsDefaultOrEmpty ||
            options.PathPatterns.Any(static pattern =>
                string.IsNullOrWhiteSpace(pattern) ||
                pattern.Count(static character => character == '?') != 1 ||
                Path.IsPathRooted(pattern)))
        {
            throw new ArgumentException(
                "Each module path pattern must be relative and contain exactly one '?'.",
                nameof(options));
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaximumFileBytes);

        _roots = [.. options.RootDirectories.Select(Path.GetFullPath)
            .Distinct(GetPathComparer())
            .OrderBy(static root => root, GetPathComparer())];
        _patterns = [.. options.PathPatterns.Distinct(StringComparer.Ordinal)];
        _maximumFileBytes = options.MaximumFileBytes;
    }

    public async ValueTask<LuaWorkspaceDocument?> ResolveAsync(
        LuaModuleResolutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsSafeRequestedName(request.RequestedName))
        {
            return null;
        }

        var replacement = request.RequestedName
            .Replace('.', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        foreach (var root in _roots)
        {
            foreach (var pattern in _patterns)
            {
                var relative = pattern.Replace("?", replacement, StringComparison.Ordinal)
                    .Replace('/', Path.DirectorySeparatorChar)
                    .Replace('\\', Path.DirectorySeparatorChar);
                var candidate = Path.GetFullPath(Path.Combine(root, relative));
                if (!IsWithinRoot(root, candidate) || !File.Exists(candidate))
                {
                    continue;
                }

                var length = new FileInfo(candidate).Length;
                if (length > _maximumFileBytes)
                {
                    throw new InvalidDataException(
                        $"Resolved module file '{candidate}' exceeds the {_maximumFileBytes}-byte limit.");
                }

                var bytes = await File.ReadAllBytesAsync(candidate, cancellationToken)
                    .ConfigureAwait(false);
                var stableRelative = Path.GetRelativePath(root, candidate).Replace('\\', '/');
                return new LuaWorkspaceDocument(
                    new LuaModuleIdentity(request.RequestedName),
                    new LuaSourceDocument(new SourceText(bytes), "@" + stableRelative));
            }
        }

        return null;
    }

    private static bool IsSafeRequestedName(string name) =>
        !string.IsNullOrWhiteSpace(name) &&
        !Path.IsPathRooted(name) &&
        name.IndexOf('\0') < 0 &&
        name.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries)
            .All(static segment => segment is not "." and not "..");

    private static bool IsWithinRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }

    private static StringComparer GetPathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;
}
