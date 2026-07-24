using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lunil.Cli.CommandLine;
using Lunil.Cli.IO;
using Lunil.Hosting;

namespace Lunil.Cli.Commands;

internal static class PatchCommand
{
    private const int MaximumPemBytes = 64 * 1024;

    public static async Task<CliExitCode> ExecuteAsync(CliCommandContext context)
    {
        Validate(context.Options);
        return context.Options.PatchAction switch
        {
            CliPatchAction.Pack => await PackAsync(context).ConfigureAwait(false),
            CliPatchAction.Verify => await ReadAsync(context, inspect: false, dryRun: false)
                .ConfigureAwait(false),
            CliPatchAction.Inspect => await ReadAsync(context, inspect: true, dryRun: false)
                .ConfigureAwait(false),
            CliPatchAction.DryRun => await ReadAsync(context, inspect: true, dryRun: true)
                .ConfigureAwait(false),
            CliPatchAction.Diff => await DiffAsync(context).ConfigureAwait(false),
            _ => throw new CliUsageException("A patch action is required."),
        };
    }

    private static async Task<CliExitCode> PackAsync(CliCommandContext context)
    {
        var manifestPath = ResolvePath(context, context.Options.Inputs[0]);
        var payloadRoot = ResolvePath(context, context.Options.Inputs[1]);
        var manifestBytes = await ReadBoundedAsync(
            manifestPath,
            context.Options.MaximumInputBytes,
            context.CancellationToken).ConfigureAwait(false);
        var manifest = LuaPatchManifestSerializer.Deserialize(manifestBytes);
        var rootWithSeparator = Path.TrimEndingDirectorySeparator(payloadRoot) +
            Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        var entries = new List<LuaPatchEntry>(manifest.Entries.Length);
        foreach (var descriptor in manifest.Entries)
        {
            var path = Path.GetFullPath(
                descriptor.Name.Replace('/', Path.DirectorySeparatorChar),
                payloadRoot);
            if (!path.StartsWith(rootWithSeparator, comparison))
            {
                throw new CliInputException(
                    $"Patch entry '{descriptor.Name}' resolves outside the payload root.");
            }

            var content = await ReadBoundedAsync(
                path,
                context.Options.MaximumInputBytes,
                context.CancellationToken).ConfigureAwait(false);
            entries.Add(new LuaPatchEntry(
                descriptor.Name,
                descriptor.ModuleName,
                descriptor.Kind,
                content,
                descriptor.Dependencies));
        }

        using var key = ECDsa.Create();
        ImportPem(
                key,
                ResolvePath(context, context.Options.PatchPrivateKeyPath!),
                Math.Min(context.Options.MaximumInputBytes, MaximumPemBytes));
        var bundle = LuaPatchBundle.Create(
            manifest with { Entries = [] },
            entries,
            new LuaPatchEcdsaSigner(context.Options.PatchKeyId!, key));
        var output = ResolvePath(context, context.Options.OutputPath!);
        var directory = Path.GetDirectoryName(output)!;
        Directory.CreateDirectory(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                bundle.Write(stream);
                await stream.FlushAsync(context.CancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, output, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        await CliStreams.WriteTextAsync(
            context.StandardOutput,
            output + "\n",
            context.CancellationToken).ConfigureAwait(false);
        return CliExitCode.Success;
    }

    private static async Task<CliExitCode> ReadAsync(
        CliCommandContext context,
        bool inspect,
        bool dryRun)
    {
        var trust = await LoadTrustStoreAsync(context).ConfigureAwait(false);
        var bundle = LoadBundle(context, context.Options.Inputs[0], trust);
        LuaPatchPreflightResult? preflight = null;
        if (dryRun)
        {
            preflight = LuaPatchPreflight.Analyze(
                bundle,
                LuaHostOptions.Default with { LanguageVersion = bundle.Manifest.LanguageVersion },
                cancellationToken: context.CancellationToken);
        }

        if (inspect)
        {
            WriteInspection(context.StandardOutput, bundle, preflight);
            await context.StandardOutput.FlushAsync(context.CancellationToken).ConfigureAwait(false);
        }
        else
        {
            await CliStreams.WriteTextAsync(
                context.StandardOutput,
                $"verified {bundle.Manifest.PatchId} {bundle.Manifest.TargetRevision}\n",
                context.CancellationToken).ConfigureAwait(false);
        }

        return preflight is null || preflight.Succeeded
            ? CliExitCode.Success
            : CliExitCode.Diagnostics;
    }

    private static async Task<CliExitCode> DiffAsync(CliCommandContext context)
    {
        var trust = await LoadTrustStoreAsync(context).ConfigureAwait(false);
        var before = LoadBundle(context, context.Options.Inputs[0], trust);
        var after = LoadBundle(context, context.Options.Inputs[1], trust);
        var beforeModules = before.Manifest.Entries
            .Where(static entry => entry.ModuleName is not null)
            .ToDictionary(static entry => entry.ModuleName!, StringComparer.Ordinal);
        var afterModules = after.Manifest.Entries
            .Where(static entry => entry.ModuleName is not null)
            .ToDictionary(static entry => entry.ModuleName!, StringComparer.Ordinal);
        using (var writer = new Utf8JsonWriter(
            context.StandardOutput,
            new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "lunil.patch.diff.v1");
            writer.WriteString("baseRevision", before.Manifest.TargetRevision);
            writer.WriteString("targetRevision", after.Manifest.TargetRevision);
            WriteNames(writer, "added", afterModules.Keys.Except(beforeModules.Keys, StringComparer.Ordinal));
            WriteNames(writer, "removed", beforeModules.Keys.Except(afterModules.Keys, StringComparer.Ordinal));
            WriteNames(writer, "changed", beforeModules.Keys.Intersect(afterModules.Keys, StringComparer.Ordinal)
                .Where(name => !string.Equals(
                    beforeModules[name].ContentHash,
                    afterModules[name].ContentHash,
                    StringComparison.Ordinal) ||
                    beforeModules[name].Kind != afterModules[name].Kind ||
                    !beforeModules[name].Dependencies.SequenceEqual(afterModules[name].Dependencies)));
            writer.WriteEndObject();
            writer.Flush();
        }

        context.StandardOutput.Write("\n"u8);
        await context.StandardOutput.FlushAsync(context.CancellationToken).ConfigureAwait(false);
        return CliExitCode.Success;
    }

    private static void WriteNames(
        Utf8JsonWriter writer,
        string propertyName,
        IEnumerable<string> names)
    {
        writer.WriteStartArray(propertyName);
        foreach (var name in names.Order(StringComparer.Ordinal))
        {
            writer.WriteStringValue(name);
        }

        writer.WriteEndArray();
    }

    private static LuaPatchBundle LoadBundle(
        CliCommandContext context,
        string input,
        ILuaPatchSignatureVerifier trust)
    {
        var bundlePath = ResolvePath(context, input);
        using var stream = new FileStream(
            bundlePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return LuaPatchBundle.Read(stream, trust, new LuaPatchBundleReadOptions
        {
            MaximumBundleBytes = context.Options.MaximumInputBytes,
            MaximumEntryBytes = context.Options.MaximumInputBytes,
            MaximumTotalEntryBytes = context.Options.MaximumInputBytes,
        });
    }

    private static void WriteInspection(
        Stream output,
        LuaPatchBundle bundle,
        LuaPatchPreflightResult? preflight)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true });
        writer.WriteStartObject();
        writer.WriteString("schema", "lunil.patch.inspect.v1");
        writer.WriteString("patchId", bundle.Manifest.PatchId);
        writer.WriteString("channel", bundle.Manifest.Channel);
        writer.WriteString("targetBuild", bundle.Manifest.TargetBuild);
        writer.WriteString("baseRevision", bundle.Manifest.BaseRevision);
        writer.WriteString("targetRevision", bundle.Manifest.TargetRevision);
        writer.WriteString("updateIntent", bundle.Manifest.UpdateIntent.ToString());
        writer.WriteString("luaVersion", bundle.Manifest.LanguageVersion.ToString());
        writer.WriteString("runtimeAbi", bundle.Manifest.RuntimeAbi);
        writer.WriteString("signingKey", bundle.Signature.KeyId);
        writer.WriteStartArray("requiredCapabilities");
        foreach (var capability in bundle.Manifest.RequiredCapabilities)
        {
            writer.WriteStringValue(capability);
        }
        writer.WriteEndArray();
        writer.WriteStartArray("requiredTargetLabels");
        foreach (var label in bundle.Manifest.RequiredTargetLabels)
        {
            writer.WriteStartObject();
            writer.WriteString("name", label.Name);
            writer.WriteString("value", label.Value);
            writer.WriteEndObject();
        }
        writer.WriteEndArray();
        writer.WriteStartArray("modules");
        foreach (var entry in bundle.Entries.Where(static entry => entry.ModuleName is not null))
        {
            writer.WriteStartObject();
            writer.WriteString("name", entry.ModuleName);
            writer.WriteString("entry", entry.Name);
            writer.WriteString("kind", entry.Kind.ToString());
            if (preflight is not null)
            {
                var result = preflight.Modules.Single(module =>
                    string.Equals(module.ModuleName, entry.ModuleName, StringComparison.Ordinal));
                writer.WriteString("preflight", result.Status.ToString());
                if (result.Message is not null)
                {
                    writer.WriteString("message", result.Message);
                }
            }

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteBoolean("ready", preflight?.Succeeded ?? true);
        writer.WriteEndObject();
        writer.Flush();
        output.Write("\n"u8);
    }

    private static void Validate(CliOptions options)
    {
        if (options.PatchAction == CliPatchAction.None)
        {
            throw new CliUsageException("A patch action is required: pack, verify, inspect, dry-run, or diff.");
        }

        if (options.PatchAction == CliPatchAction.Pack)
        {
            if (options.Inputs.Length != 2 || string.IsNullOrWhiteSpace(options.OutputPath) ||
                string.IsNullOrWhiteSpace(options.PatchPrivateKeyPath) ||
                string.IsNullOrWhiteSpace(options.PatchKeyId) ||
                !string.IsNullOrWhiteSpace(options.PatchTrustStorePath))
            {
                throw new CliUsageException(
                    "Patch pack requires <manifest.json> <payload-root>, --output, --private-key, and --key-id.");
            }

            return;
        }

        var expectedInputs = options.PatchAction == CliPatchAction.Diff ? 2 : 1;
        var hasTrustStore = !string.IsNullOrWhiteSpace(options.PatchTrustStorePath);
        var hasSingleKey = !string.IsNullOrWhiteSpace(options.PatchPublicKeyPath) &&
            !string.IsNullOrWhiteSpace(options.PatchKeyId);
        if (options.Inputs.Length != expectedInputs || hasTrustStore == hasSingleKey ||
            hasTrustStore && (!string.IsNullOrWhiteSpace(options.PatchPublicKeyPath) ||
                !string.IsNullOrWhiteSpace(options.PatchKeyId)))
        {
            throw new CliUsageException(
                "Patch verify, inspect, dry-run, and diff require either --trust-store <json> " +
                "or both --public-key <pem> and --key-id <id>.");
        }
    }

    private static async Task<LuaPatchEcdsaTrustStore> LoadTrustStoreAsync(
        CliCommandContext context)
    {
        if (string.IsNullOrWhiteSpace(context.Options.PatchTrustStorePath))
        {
            using var key = ECDsa.Create();
            ImportPem(
                key,
                ResolvePath(context, context.Options.PatchPublicKeyPath!),
                Math.Min(context.Options.MaximumInputBytes, MaximumPemBytes));
            return new LuaPatchEcdsaTrustStore([
                new LuaPatchTrustedEcdsaKey(
                    context.Options.PatchKeyId!,
                    key.ExportSubjectPublicKeyInfo()),
            ]);
        }

        var storePath = ResolvePath(context, context.Options.PatchTrustStorePath);
        var storeBytes = await ReadBoundedAsync(
            storePath,
            context.Options.MaximumInputBytes,
            context.CancellationToken).ConfigureAwait(false);
        PatchTrustStoreDocument document;
        try
        {
            document = JsonSerializer.Deserialize(
                storeBytes,
                PatchTrustStoreJsonContext.Default.PatchTrustStoreDocument) ??
                throw new CliInputException("The patch trust store is empty.");
        }
        catch (JsonException exception)
        {
            throw new CliInputException(
                $"Patch trust store '{storePath}' is invalid JSON: {exception.Message}",
                exception);
        }

        if (!string.Equals(document.Schema, PatchTrustStoreDocument.SchemaName, StringComparison.Ordinal) ||
            document.Keys is not { Length: > 0 and <= PatchTrustStoreDocument.MaximumKeyCount })
        {
            throw new CliInputException(
                $"Patch trust store '{storePath}' must use schema " +
                $"'{PatchTrustStoreDocument.SchemaName}' and contain 1 to " +
                $"{PatchTrustStoreDocument.MaximumKeyCount} keys.");
        }

        var directory = Path.GetDirectoryName(storePath)!;
        var trustedKeys = new List<LuaPatchTrustedEcdsaKey>(document.Keys.Length);
        foreach (var entry in document.Keys)
        {
            if (string.IsNullOrWhiteSpace(entry.KeyId) || string.IsNullOrWhiteSpace(entry.PublicKey))
            {
                throw new CliInputException(
                    $"Patch trust store '{storePath}' contains a key without keyId or publicKey.");
            }

            using var key = ECDsa.Create();
            ImportPem(
                key,
                ResolveTrustKeyPath(storePath, directory, entry.PublicKey),
                Math.Min(context.Options.MaximumInputBytes, MaximumPemBytes));
            trustedKeys.Add(new LuaPatchTrustedEcdsaKey(
                entry.KeyId,
                key.ExportSubjectPublicKeyInfo())
            {
                ValidFrom = entry.ValidFrom,
                ValidUntil = entry.ValidUntil,
                RevokedAt = entry.RevokedAt,
            });
        }

        try
        {
            return new LuaPatchEcdsaTrustStore(trustedKeys);
        }
        catch (ArgumentException exception)
        {
            throw new CliInputException(
                $"Patch trust store '{storePath}' is invalid: {exception.Message}",
                exception);
        }
    }

    private static string ResolvePath(CliCommandContext context, string path) =>
        Path.GetFullPath(path, context.CurrentDirectory);

    private static string ResolveTrustKeyPath(
        string storePath,
        string directory,
        string keyPath)
    {
        try
        {
            return Path.GetFullPath(keyPath, directory);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
        {
            throw new CliInputException(
                $"Patch trust store '{storePath}' contains an invalid publicKey path.",
                exception);
        }
    }

    private static async Task<byte[]> ReadBoundedAsync(
        string path,
        long maximumBytes,
        CancellationToken cancellationToken)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new CliInputException($"Patch input '{path}' does not exist.");
            }

            if (info.Length > maximumBytes || info.Length > int.MaxValue)
            {
                throw new CliInputException($"Patch input '{path}' exceeds the input limit.");
            }

            return await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            throw new CliInputException($"Cannot read patch input '{path}': {exception.Message}", exception);
        }
    }

    private static void ImportPem(ECDsa key, string path, long maximumBytes)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists)
            {
                throw new FileNotFoundException("The patch key does not exist.", path);
            }

            if (info.Length > maximumBytes || info.Length > int.MaxValue)
            {
                throw new IOException("The patch key exceeds the input limit.");
            }

            key.ImportFromPem(File.ReadAllText(path, Encoding.UTF8));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or CryptographicException or ArgumentException)
        {
            throw new CliInputException($"Cannot read patch key '{path}': {exception.Message}", exception);
        }
    }
}
