using Lunil.Cli.CommandLine;
using Lunil.Cli.IO;
using Lunil.Compiler;
using Lunil.Core.Text;
using Lunil.Workspace;

namespace Lunil.Cli.Commands;

internal sealed record CliInputDocument(
    string Input,
    string DisplayPath,
    string? FilePath,
    string ModuleName,
    string SourceIdentity,
    byte[] Bytes,
    bool IsBinaryChunk)
{
    private static ReadOnlySpan<byte> ChunkSignature => [0x1b, (byte)'L', (byte)'u', (byte)'a'];

    public LuaWorkspaceDocument ToWorkspaceDocument() => new(
        new LuaModuleIdentity(ModuleName),
        new LuaSourceDocument(new SourceText(Bytes), SourceIdentity));

    public LuaSourceDocument ToSourceDocument() =>
        new(new SourceText(Bytes), SourceIdentity);

    public static async Task<CliInputDocument> LoadAsync(
        string input,
        CliOptions options,
        string currentDirectory,
        Stream standardInput,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(input);
        byte[] bytes;
        string displayPath;
        string? filePath;
        if (input == "-")
        {
            bytes = await CliStreams.ReadBoundedAsync(
                standardInput,
                options.MaximumInputBytes,
                "Standard input",
                cancellationToken).ConfigureAwait(false);
            displayPath = "<stdin>";
            filePath = null;
        }
        else
        {
            filePath = Path.GetFullPath(input, currentDirectory);
            displayPath = filePath;
            try
            {
                var info = new FileInfo(filePath);
                if (!info.Exists)
                {
                    throw new CliInputException($"Input file '{filePath}' was not found.");
                }

                if (info.Length > options.MaximumInputBytes)
                {
                    throw new CliInputException(
                        $"Input file '{filePath}' exceeds the {options.MaximumInputBytes}-byte input limit.");
                }

                await using var stream = new FileStream(
                    filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.Read,
                    64 * 1024,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                bytes = await CliStreams.ReadBoundedAsync(
                    stream,
                    options.MaximumInputBytes,
                    $"Input file '{filePath}'",
                    cancellationToken).ConfigureAwait(false);
            }
            catch (CliInputException)
            {
                throw;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
                NotSupportedException or ArgumentException)
            {
                throw new CliInputException(
                    $"Cannot read input file '{filePath}': {exception.Message}",
                    exception);
            }
        }

        var (derivedModuleName, sourceIdentity) = DeriveIdentities(
            filePath,
            currentDirectory,
            options.ModuleRoots);
        var moduleName = options.ModuleName ?? derivedModuleName;
        return new CliInputDocument(
            input,
            displayPath,
            filePath,
            moduleName,
            sourceIdentity,
            bytes,
            bytes.AsSpan().StartsWith(ChunkSignature));
    }

    private static (string ModuleName, string SourceIdentity) DeriveIdentities(
        string? filePath,
        string currentDirectory,
        IEnumerable<string> configuredRoots)
    {
        if (filePath is null)
        {
            return ("stdin", "=stdin");
        }

        var roots = configuredRoots.Select(root => Path.GetFullPath(root, currentDirectory))
            .Where(root => IsWithinRoot(root, filePath))
            .OrderByDescending(static root => root.Length)
            .ToArray();
        var identityRoot = roots.FirstOrDefault() ?? Path.GetDirectoryName(filePath)!;
        var relative = Path.GetRelativePath(identityRoot, filePath).Replace('\\', '/');
        var sourceIdentity = "@" + relative;

        var extension = Path.GetExtension(relative);
        if (extension.Length != 0)
        {
            relative = relative[..^extension.Length];
        }

        relative = relative.Replace('/', '.');
        if (relative.EndsWith(".init", StringComparison.Ordinal))
        {
            relative = relative[..^".init".Length];
        }

        return (string.IsNullOrWhiteSpace(relative) ? "main" : relative, sourceIdentity);
    }

    private static bool IsWithinRoot(string root, string candidate)
    {
        var relative = Path.GetRelativePath(root, candidate);
        return relative != ".." &&
            !relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
            !Path.IsPathRooted(relative);
    }
}
