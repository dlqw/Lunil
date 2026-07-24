using System.Collections.Immutable;
using Lunil.Compiler;
using Lunil.IR.Canonical;

namespace Lunil.Hosting;

public enum LuaPatchPreflightStatus : byte
{
    Ready,
    LanguageVersionMismatch,
    CompilationFailed,
    ChunkValidationFailed,
    CanonicalIrDecoderRequired,
    CanonicalIrValidationFailed,
    Deferred,
}

public sealed record LuaPatchModulePreflightResult(
    string ModuleName,
    LuaPatchEntryKind Kind,
    LuaPatchPreflightStatus Status,
    LuaCompilationResult? Compilation,
    LuaIrModule? Module,
    string? Message)
{
    public bool Succeeded => Status == LuaPatchPreflightStatus.Ready;
}

public sealed record LuaPatchPreflightResult(
    LuaPatchManifest Manifest,
    LuaPatchDependencyPlan DependencyPlan,
    ImmutableArray<LuaPatchModulePreflightResult> Modules)
{
    public bool Succeeded => Modules.All(static module => module.Succeeded);
}

public interface ILuaPatchCanonicalIrDecoder
{
    LuaIrModule Decode(string moduleName, ReadOnlySpan<byte> payload);
}

public static class LuaPatchPreflight
{
    public static LuaPatchPreflightResult Analyze(
        LuaPatchBundle bundle,
        LuaHostOptions? hostOptions = null,
        ILuaPatchCanonicalIrDecoder? canonicalIrDecoder = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        var configured = (hostOptions ?? LuaHostOptions.Default) with
        {
            LanguageVersion = bundle.Manifest.LanguageVersion,
            InstallStandardLibrary = false,
            Clr = LuaClrOptions.Disabled,
        };
        using var stagingHost = new LuaHost(configured);
        var plan = LuaPatchDependencyPlan.Create(bundle.Entries);
        var entriesByModule = bundle.Entries
            .Where(static entry => entry.ModuleName is not null)
            .ToDictionary(static entry => entry.ModuleName!, StringComparer.Ordinal);
        var results = ImmutableArray.CreateBuilder<LuaPatchModulePreflightResult>(entriesByModule.Count);
        foreach (var component in plan.Components)
        {
            foreach (var moduleName in component.Modules)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(AnalyzeEntry(
                    stagingHost,
                    entriesByModule[moduleName],
                    canonicalIrDecoder,
                    cancellationToken));
            }
        }

        return new LuaPatchPreflightResult(bundle.Manifest, plan, results.ToImmutable());
    }

    private static LuaPatchModulePreflightResult AnalyzeEntry(
        LuaHost host,
        LuaPatchEntry entry,
        ILuaPatchCanonicalIrDecoder? decoder,
        CancellationToken cancellationToken)
    {
        var moduleName = entry.ModuleName!;
        try
        {
            switch (entry.Kind)
            {
                case LuaPatchEntryKind.Source:
                    var compilation = host.Compiler.CompileBytes(
                        entry.Content.Span,
                        "@patch/" + entry.Name,
                        cancellationToken);
                    return compilation.Succeeded && compilation.Module is not null
                        ? Ready(moduleName, entry.Kind, compilation.Module, compilation)
                        : new LuaPatchModulePreflightResult(
                            moduleName,
                            entry.Kind,
                            LuaPatchPreflightStatus.CompilationFailed,
                            compilation,
                            null,
                            compilation.Diagnostics.FirstOrDefault()?.Message ??
                                "The source entry failed compilation.");
                case LuaPatchEntryKind.BinaryChunk:
                    var closure = host.State.LoadBinaryChunk(entry.Content.Span);
                    return Ready(moduleName, entry.Kind, closure.Module, null);
                case LuaPatchEntryKind.CanonicalIr:
                    if (decoder is null)
                    {
                        return new LuaPatchModulePreflightResult(
                            moduleName,
                            entry.Kind,
                            LuaPatchPreflightStatus.CanonicalIrDecoderRequired,
                            null,
                            null,
                            "A canonical IR decoder is required for this entry.");
                    }

                    var module = decoder.Decode(moduleName, entry.Content.Span);
                    if (module.LanguageVersion != host.Options.LanguageVersion)
                    {
                        return new LuaPatchModulePreflightResult(
                            moduleName,
                            entry.Kind,
                            LuaPatchPreflightStatus.LanguageVersionMismatch,
                            null,
                            null,
                            "The canonical module language version does not match the patch.");
                    }

                    var errors = LuaIrVerifier.Verify(module);
                    return errors.IsEmpty
                        ? Ready(moduleName, entry.Kind, module, null)
                        : new LuaPatchModulePreflightResult(
                            moduleName,
                            entry.Kind,
                            LuaPatchPreflightStatus.CanonicalIrValidationFailed,
                            null,
                            null,
                            errors[0].Message);
                default:
                    throw new InvalidOperationException("Companion data is not a Lua module.");
            }
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException and
            not OutOfMemoryException and
            not StackOverflowException and
            not AccessViolationException)
        {
            return new LuaPatchModulePreflightResult(
                moduleName,
                entry.Kind,
                entry.Kind == LuaPatchEntryKind.BinaryChunk
                    ? LuaPatchPreflightStatus.ChunkValidationFailed
                    : LuaPatchPreflightStatus.CanonicalIrValidationFailed,
                null,
                null,
                exception.Message);
        }
    }

    private static LuaPatchModulePreflightResult Ready(
        string moduleName,
        LuaPatchEntryKind kind,
        LuaIrModule module,
        LuaCompilationResult? compilation) => new(
            moduleName,
            kind,
            LuaPatchPreflightStatus.Ready,
            compilation,
            module,
            null);
}
