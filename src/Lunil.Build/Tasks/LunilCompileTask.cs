using System.Collections.Immutable;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lunil.CodeGen.Cil;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.CodeGen.Cil.Caching;
using Lunil.CodeGen.Cil.Loading;
using Lunil.Compiler;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Workspace;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Lunil.Build.Tasks;

public sealed class LunilCompileTask : Microsoft.Build.Utilities.Task
{
    private LuaBackendDiskCache? _cache;
    private bool _cacheInitialized;

    [Required]
    public ITaskItem[] Sources { get; set; } = [];

    [Required]
    public string IntermediateOutputPath { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    public string TargetFramework { get; set; } = string.Empty;

    public string RuntimeIdentifier { get; set; } = string.Empty;

    public string DesignTimeBuild { get; set; } = string.Empty;

    public bool CacheEnabled { get; set; } = true;

    public string CacheDirectory { get; set; } = string.Empty;

    public long CacheMaximumBytes { get; set; } = 1024L * 1024 * 1024;

    public long CacheMaximumEntryBytes { get; set; } = 256L * 1024 * 1024;

    public long CacheMaximumQuarantineBytes { get; set; } = 64L * 1024 * 1024;

    public string PublishAot { get; set; } = string.Empty;

    public string PublishReadyToRun { get; set; } = string.Empty;

    public string PublishTrimmed { get; set; } = string.Empty;

    [Output]
    public ITaskItem[] GeneratedSources { get; private set; } = [];

    [Output]
    public ITaskItem[] GeneratedReferences { get; private set; } = [];

    [Output]
    public ITaskItem[] GeneratedArtifacts { get; private set; } = [];

    public override bool Execute()
    {
        try
        {
            return ExecuteCore();
        }
        catch (Exception exception) when (exception is not OutOfMemoryException and
            not StackOverflowException and not AccessViolationException)
        {
            LogBuildError(
                LunilBuildDiagnosticCodes.InternalBuildFailure,
                null,
                0,
                0,
                $"Lunil build task failed: {exception.GetType().Name}: {exception.Message}");
            return false;
        }
    }

    private bool ExecuteCore()
    {
        var projectDirectory = Path.GetFullPath(ProjectDirectory);
        string outputDirectory;
        try
        {
            outputDirectory = Path.GetFullPath(IntermediateOutputPath, projectDirectory);
            Directory.CreateDirectory(outputDirectory);
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException or UnauthorizedAccessException)
        {
            LogBuildError(
                LunilBuildDiagnosticCodes.InvalidOutputPath,
                null,
                0,
                0,
                $"Lunil intermediate output path is invalid: {exception.Message}");
            return false;
        }

        using var outputLock = AcquireOutputLock(outputDirectory);

        var seenNames = new HashSet<string>(StringComparer.Ordinal);
        var optionsBySource = new List<LunilCompileItemOptions>(Sources.Length);
        foreach (var source in Sources)
        {
            if (!LunilCompileItemOptions.TryCreate(
                source,
                projectDirectory,
                (code, message) => LogBuildError(code, source.ItemSpec, 0, 0, message),
                out var options))
            {
                continue;
            }

            if (!seenNames.Add(options!.ModuleName))
            {
                LogBuildError(
                    LunilBuildDiagnosticCodes.DuplicateModuleName,
                    source.ItemSpec,
                    0,
                    0,
                    $"ModuleName '{options.ModuleName}' is assigned to more than one LunilCompile item.");
                continue;
            }

            optionsBySource.Add(options);
        }

        if (Log.HasLoggedErrors)
        {
            return false;
        }

        if (bool.TryParse(DesignTimeBuild, out var designTimeBuild) && designTimeBuild)
        {
            var designTimeSource = Path.Combine(outputDirectory, "LunilModules.g.cs");
            AtomicWriteText(designTimeSource, GenerateRegistrySource([]));
            GeneratedSources = [new TaskItem(designTimeSource)];
            GeneratedArtifacts = [new TaskItem(designTimeSource)];
            return true;
        }

        var workspaceCompilations = AnalyzeWorkspace(optionsBySource);
        if (Log.HasLoggedErrors)
        {
            return false;
        }

        var compiledModules = new List<CompiledModule>(optionsBySource.Count);
        var artifacts = new List<ITaskItem>();
        foreach (var options in optionsBySource)
        {
            workspaceCompilations.TryGetValue(options.ModuleName, out var workspaceCompilation);
            var module = CompileModule(options, outputDirectory, workspaceCompilation);
            if (module is not null)
            {
                compiledModules.Add(module);
                artifacts.AddRange(module.ArtifactPaths.Select(static path => (ITaskItem)new TaskItem(path)));
            }
        }

        if (Log.HasLoggedErrors)
        {
            return false;
        }

        var registrySource = Path.Combine(outputDirectory, "LunilModules.g.cs");
        AtomicWriteText(registrySource, GenerateRegistrySource(compiledModules));
        artifacts.Add(new TaskItem(registrySource));

        var stampPath = Path.Combine(outputDirectory, "LunilCompile.stamp");
        var stamp = string.Join(
            "\n",
            compiledModules
                .OrderBy(static module => module.ModuleName, StringComparer.Ordinal)
                .Select(static module => $"{module.ModuleName}\t{module.ModuleContentId}"));
        AtomicWriteText(stampPath, stamp + "\n");
        artifacts.Add(new TaskItem(stampPath));

        GeneratedSources = [new TaskItem(registrySource)];
        GeneratedReferences = [.. compiledModules.Select(static module => CreateReference(module.PePath))];
        GeneratedArtifacts = [.. artifacts];
        return true;
    }

    private CompiledModule? CompileModule(
        LunilCompileItemOptions options,
        string outputDirectory,
        LuaCompilationResult? workspaceCompilation)
    {
        var sourceBytes = File.ReadAllBytes(options.SourcePath);
        var sourceSha256 = Convert.ToHexStringLower(SHA256.HashData(sourceBytes));
        var inputKind = options.InputKind == LunilBuildInputKind.Auto
            ? sourceBytes.AsSpan().StartsWith(new byte[] { 0x1b, (byte)'L', (byte)'u', (byte)'a' })
                ? LunilBuildInputKind.BinaryChunk
                : LunilBuildInputKind.Source
            : options.InputKind;
        var reused = TryReuseModule(
            options,
            inputKind,
            sourceSha256,
            outputDirectory);
        if (reused is not null)
        {
            Log.LogMessage(
                MessageImportance.Low,
                $"Lunil module '{options.ModuleName}' is up-to-date ({reused.ModuleContentId}).");
            return reused;
        }

        var logicalName = options.ModuleName.Replace('.', '/') +
            (inputKind == LunilBuildInputKind.BinaryChunk ? ".luac" : ".lua");
        LuaIrModule canonicalModule;
        if (inputKind == LunilBuildInputKind.BinaryChunk)
        {
            try
            {
                canonicalModule = Lua54PrototypeConverter.Convert(sourceBytes);
            }
            catch (Lua54ChunkFormatException exception)
            {
                LogBuildError(
                    LunilBuildDiagnosticCodes.InvalidBinaryChunk,
                    options.SourcePath,
                    1,
                    exception.ByteOffset + 1,
                    exception.Message);
                return null;
            }
            catch (InvalidDataException exception)
            {
                LogBuildError(
                    LunilBuildDiagnosticCodes.InvalidBinaryChunk,
                    options.SourcePath,
                    1,
                    1,
                    exception.Message);
                return null;
            }
        }
        else
        {
            var source = new SourceText(sourceBytes);
            var frontendCompilation = workspaceCompilation ??
                new LuaCompiler().Compile(source, "@" + logicalName);
            if (workspaceCompilation is null)
            {
                foreach (var diagnostic in frontendCompilation.Diagnostics)
                {
                    LogFrontendDiagnostic(options.SourcePath, source, diagnostic.Diagnostic);
                }
            }

            if (!frontendCompilation.Succeeded ||
                frontendCompilation.Module is null ||
                Log.HasLoggedErrors)
            {
                return null;
            }

            canonicalModule = frontendCompilation.Module;
        }

        var canonicalModuleBytes = LuaAotModuleIdentity.SerializeCanonicalModule(canonicalModule);
        var cacheKey = CreateCacheKey(
            options,
            inputKind,
            sourceSha256,
            canonicalModule,
            logicalName);
        var cache = GetCache();
        if (cache is not null)
        {
            var read = cache.TryReadAsync(cacheKey).AsTask().GetAwaiter().GetResult();
            if (read.IsHit)
            {
                try
                {
                    var cached = LunilBuildCachePayload.Deserialize(
                        read.Payload.AsSpan(),
                        cacheKey.CanonicalModuleHash);
                    Log.LogMessage(
                        MessageImportance.Low,
                        $"Lunil module '{options.ModuleName}' restored from backend cache ({cacheKey.CacheId}).");
                    return MaterializeModule(
                        options,
                        inputKind,
                        sourceSha256,
                        outputDirectory,
                        cacheKey,
                        cached);
                }
                catch (Exception exception) when (exception is InvalidDataException or
                    ArgumentException or BadImageFormatException)
                {
                    _ = cache.QuarantineAsync(
                        cacheKey,
                        $"semantic-validation: {exception.Message}").AsTask().GetAwaiter().GetResult();
                    Log.LogMessage(
                        MessageImportance.Low,
                        $"Lunil backend cache entry '{cacheKey.CacheId}' was rejected and will be rebuilt: {exception.Message}");
                }
            }
            else if (read.Status == LuaBackendCacheReadStatus.Unavailable)
            {
                Log.LogMessage(
                    MessageImportance.Low,
                    $"Lunil backend cache is unavailable ({read.DiagnosticCode}); compiling locally.");
            }
        }

        var compilation = LuaAotCompiler.Compile(
            canonicalModule,
            new LuaAotCompilationOptions
            {
                EmitPortablePdb = options.DebugSymbols,
                SourceDocument = new LuaAotSourceDocument
                {
                    LogicalName = logicalName,
                    Content = sourceBytes.ToImmutableArray(),
                },
            });
        if (!compilation.Succeeded || compilation.Artifact is null)
        {
            foreach (var diagnostic in compilation.Diagnostics)
            {
                var line = GetAotDiagnosticLine(canonicalModule, diagnostic);
                LogBuildError(
                    LunilBuildDiagnosticCodes.ArtifactEmissionFailed,
                    options.SourcePath,
                    line,
                    line == 0 ? 0 : 1,
                    $"[{diagnostic.Code}] {diagnostic.Message}");
            }

            return null;
        }

        var artifact = compilation.Artifact;
        if (cache is not null)
        {
            var payload = LunilBuildCachePayload.Serialize(canonicalModuleBytes, artifact);
            var write = cache.WriteAsync(cacheKey, payload).AsTask().GetAwaiter().GetResult();
            if (write.Status is LuaBackendCacheWriteStatus.Unavailable or
                LuaBackendCacheWriteStatus.RejectedTooLarge)
            {
                Log.LogMessage(
                    MessageImportance.Low,
                    $"Lunil backend cache write skipped ({write.DiagnosticCode}).");
            }
        }

        return MaterializeModule(
            options,
            inputKind,
            sourceSha256,
            outputDirectory,
            cacheKey,
            new LunilBuildCachedArtifact(
                canonicalModuleBytes.ToImmutableArray(),
                artifact.Manifest,
                artifact.PeImage,
                artifact.PortablePdbImage));
    }

    private Dictionary<string, LuaCompilationResult> AnalyzeWorkspace(
        IReadOnlyCollection<LunilCompileItemOptions> optionsBySource)
    {
        var documents = new List<LuaWorkspaceDocument>();
        var sources = new Dictionary<string, (LunilCompileItemOptions Options, SourceText Source)>(
            StringComparer.Ordinal);
        foreach (var options in optionsBySource.OrderBy(static options =>
                     options.ModuleName,
                     StringComparer.Ordinal))
        {
            var bytes = File.ReadAllBytes(options.SourcePath);
            var inputKind = options.InputKind == LunilBuildInputKind.Auto
                ? bytes.AsSpan().StartsWith(new byte[] { 0x1b, (byte)'L', (byte)'u', (byte)'a' })
                    ? LunilBuildInputKind.BinaryChunk
                    : LunilBuildInputKind.Source
                : options.InputKind;
            if (inputKind != LunilBuildInputKind.Source)
            {
                continue;
            }

            var source = new SourceText(bytes);
            var logicalName = "@" + options.ModuleName.Replace('.', '/') + ".lua";
            documents.Add(new LuaWorkspaceDocument(
                new LuaModuleIdentity(options.ModuleName),
                new LuaSourceDocument(source, logicalName)));
            sources.Add(options.ModuleName, (options, source));
        }

        if (documents.Count == 0)
        {
            return new Dictionary<string, LuaCompilationResult>(StringComparer.Ordinal);
        }

        using var workspace = new LuaWorkspace(new LuaWorkspaceOptions
        {
            MaximumModuleCount = Math.Max(documents.Count, 1),
            MaximumDependencyCount = Math.Max(documents.Count * 64, 64),
            MaximumSourceBytes = Math.Max(
                documents.Sum(static document => (long)document.Source.Text.Length),
                1),
            MaximumParallelism = Math.Max(1, Math.Min(Environment.ProcessorCount, documents.Count)),
            UnresolvedModuleSeverity = DiagnosticSeverity.Warning,
            DynamicRequireSeverity = DiagnosticSeverity.Warning,
        });
        var result = workspace.AnalyzeAsync(documents).GetAwaiter().GetResult();
        foreach (var diagnostic in result.Diagnostics)
        {
            if (diagnostic.Module is not null &&
                sources.TryGetValue(diagnostic.Module.Name, out var source))
            {
                LogFrontendDiagnostic(
                    source.Options.SourcePath,
                    source.Source,
                    new Diagnostic(
                        diagnostic.Code,
                        diagnostic.Severity,
                        diagnostic.Span,
                        diagnostic.Message));
            }
            else if (diagnostic.Severity == DiagnosticSeverity.Error)
            {
                LogBuildError(diagnostic.Code, null, 0, 0, diagnostic.Message);
            }
            else if (diagnostic.Severity == DiagnosticSeverity.Warning)
            {
                Log.LogWarning(diagnostic.Code, diagnostic.Message);
            }
        }

        return result.Modules.ToDictionary(
            static module => module.Identity.Name,
            static module => module.Compilation,
            StringComparer.Ordinal);
    }

    private CompiledModule MaterializeModule(
        LunilCompileItemOptions options,
        LunilBuildInputKind inputKind,
        string sourceSha256,
        string outputDirectory,
        LuaBackendCacheKey cacheKey,
        LunilBuildCachedArtifact artifact)
    {
        var stem = SanitizeFileName(options.ModuleName) + "." +
            artifact.Manifest.ModuleContentId[..16];
        var pePath = Path.Combine(outputDirectory, artifact.Manifest.AssemblyName + ".dll");
        var pdbPath = Path.Combine(outputDirectory, artifact.Manifest.AssemblyName + ".pdb");
        var canonicalModulePath = Path.Combine(
            outputDirectory,
            artifact.Manifest.AssemblyName + ".lir");
        var manifestPath = Path.Combine(outputDirectory, stem + ".lunil.json");
        AtomicWriteBytes(pePath, artifact.PeImage.AsSpan());
        AtomicWriteBytes(canonicalModulePath, artifact.CanonicalModule.AsSpan());
        var artifactPaths = new List<string> { pePath, canonicalModulePath };
        if (!artifact.PortablePdbImage.IsDefaultOrEmpty)
        {
            AtomicWriteBytes(pdbPath, artifact.PortablePdbImage.AsSpan());
            artifactPaths.Add(pdbPath);
        }

        var buildManifest = new LunilBuildModuleManifest
        {
            SchemaVersion = 2,
            CacheId = cacheKey.CacheId,
            ModuleName = options.ModuleName,
            ModuleContentId = artifact.Manifest.ModuleContentId,
            SourcePath = Path.GetRelativePath(ProjectDirectory, options.SourcePath)
                .Replace(Path.DirectorySeparatorChar, '/'),
            SourceSha256 = sourceSha256,
            InputKind = inputKind.ToString(),
            Optimization = options.Optimization.ToString(),
            DebugSymbols = options.DebugSymbols,
            Sandbox = options.Sandbox.ToString(),
            TargetFramework = TargetFramework,
            RuntimeIdentifier = RuntimeIdentifier,
            AssemblyName = artifact.Manifest.AssemblyName,
            TypeName = artifact.Manifest.TypeName,
            PeFile = Path.GetFileName(pePath),
            PdbFile = artifact.PortablePdbImage.IsDefaultOrEmpty
                ? null
                : Path.GetFileName(pdbPath),
            CanonicalModuleFile = Path.GetFileName(canonicalModulePath),
            Functions = artifact.Manifest.Functions,
        };
        AtomicWriteText(
            manifestPath,
            JsonSerializer.Serialize(buildManifest, BuildManifestJsonOptions) + "\n");
        artifactPaths.Add(manifestPath);

        return new CompiledModule(
            options.ModuleName,
            artifact.Manifest.ModuleContentId,
            artifact.Manifest.TypeName,
            artifact.Manifest.Functions,
            Convert.ToBase64String(artifact.CanonicalModule.AsSpan()),
            pePath,
            artifactPaths);
    }

    private LuaBackendCacheKey CreateCacheKey(
        LunilCompileItemOptions options,
        LunilBuildInputKind inputKind,
        string sourceSha256,
        LuaIrModule canonicalModule,
        string logicalName)
    {
        var runtimeIdentifier = string.IsNullOrWhiteSpace(RuntimeIdentifier)
            ? "portable"
            : RuntimeIdentifier;
        var deploymentMode = IsTrue(PublishAot)
            ? LuaBackendDeploymentMode.NativeAot
            : IsTrue(PublishReadyToRun)
                ? LuaBackendDeploymentMode.ReadyToRun
                : string.IsNullOrWhiteSpace(RuntimeIdentifier)
                    ? LuaBackendDeploymentMode.Portable
                    : LuaBackendDeploymentMode.CoreClr;
        return LuaBackendCacheKey.Create(new LuaBackendCacheKeyParameters
        {
            ArtifactKind = LuaBackendCacheArtifactKind.PersistedCil,
            SourceContentHash = sourceSha256,
            CanonicalModuleHash = LuaAotModuleIdentity.ComputeContentId(canonicalModule),
            SourceBindingId = logicalName,
            CompilerVersion = CompilerVersion,
            Optimization = options.Optimization == LunilBuildOptimization.Release
                ? LuaBackendOptimizationMode.Release
                : LuaBackendOptimizationMode.Debug,
            DebugSymbols = options.DebugSymbols,
            HookMode = LuaBackendHookMode.Exact,
            SandboxMode = options.Sandbox switch
            {
                LunilBuildSandbox.Trusted => LuaBackendSandboxMode.Trusted,
                LunilBuildSandbox.Restricted => LuaBackendSandboxMode.Restricted,
                _ => LuaBackendSandboxMode.Default,
            },
            TargetFramework = string.IsNullOrWhiteSpace(TargetFramework)
                ? "unknown"
                : TargetFramework,
            RuntimeIdentifier = runtimeIdentifier,
            DeploymentMode = deploymentMode,
            TrimmingMode = IsTrue(PublishTrimmed)
                ? LuaBackendTrimmingMode.Enabled
                : LuaBackendTrimmingMode.Disabled,
            FeatureSet =
            [
                "persisted-cil-v1",
                inputKind == LunilBuildInputKind.BinaryChunk
                    ? "input-binary-chunk"
                    : "input-source",
            ],
        });
    }

    private LuaBackendDiskCache? GetCache()
    {
        if (_cacheInitialized)
        {
            return _cache;
        }

        _cacheInitialized = true;
        if (!CacheEnabled)
        {
            return null;
        }

        try
        {
            var root = string.IsNullOrWhiteSpace(CacheDirectory)
                ? GetDefaultCacheDirectory()
                : Path.GetFullPath(CacheDirectory, ProjectDirectory);
            _cache = new LuaBackendDiskCache(new LuaBackendDiskCacheOptions
            {
                RootDirectory = root,
                MaximumBytes = CacheMaximumBytes,
                MaximumEntryBytes = CacheMaximumEntryBytes,
                MaximumQuarantineBytes = CacheMaximumQuarantineBytes,
            });
            return _cache;
        }
        catch (Exception exception) when (exception is ArgumentException or IOException or
            NotSupportedException or UnauthorizedAccessException)
        {
            Log.LogMessage(
                MessageImportance.Low,
                $"Lunil backend cache was disabled: {exception.Message}");
            return null;
        }
    }

    private static string GetDefaultCacheDirectory()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(local))
        {
            local = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            if (!string.IsNullOrWhiteSpace(local))
            {
                local = Path.Combine(local, ".cache");
            }
        }

        if (string.IsNullOrWhiteSpace(local))
        {
            local = Path.GetTempPath();
        }

        return Path.Combine(local, "Lunil", "backend-cache");
    }

    private static bool IsTrue(string value) =>
        bool.TryParse(value, out var enabled) && enabled;

    private CompiledModule? TryReuseModule(
        LunilCompileItemOptions options,
        LunilBuildInputKind inputKind,
        string sourceSha256,
        string outputDirectory)
    {
        var pattern = SanitizeFileName(options.ModuleName) + ".*.lunil.json";
        foreach (var manifestPath in Directory.EnumerateFiles(outputDirectory, pattern))
        {
            LunilBuildModuleManifest? manifest;
            try
            {
                manifest = JsonSerializer.Deserialize<LunilBuildModuleManifest>(
                    File.ReadAllText(manifestPath),
                    BuildManifestJsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (manifest is null || manifest.SchemaVersion != 2 ||
                string.IsNullOrWhiteSpace(manifest.CacheId) ||
                !string.Equals(manifest.ModuleName, options.ModuleName, StringComparison.Ordinal) ||
                !string.Equals(manifest.SourceSha256, sourceSha256, StringComparison.Ordinal) ||
                !string.Equals(manifest.InputKind, inputKind.ToString(), StringComparison.Ordinal) ||
                !string.Equals(manifest.Optimization, options.Optimization.ToString(), StringComparison.Ordinal) ||
                manifest.DebugSymbols != options.DebugSymbols ||
                !string.Equals(manifest.Sandbox, options.Sandbox.ToString(), StringComparison.Ordinal) ||
                !string.Equals(manifest.TargetFramework, TargetFramework, StringComparison.Ordinal) ||
                !string.Equals(manifest.RuntimeIdentifier, RuntimeIdentifier, StringComparison.Ordinal) ||
                manifest.Functions.IsDefaultOrEmpty)
            {
                continue;
            }

            var pePath = Path.Combine(outputDirectory, manifest.PeFile);
            var pdbPath = manifest.PdbFile is null
                ? null
                : Path.Combine(outputDirectory, manifest.PdbFile);
            var canonicalModulePath = Path.Combine(outputDirectory, manifest.CanonicalModuleFile);
            if (!File.Exists(pePath) || !File.Exists(canonicalModulePath) ||
                (pdbPath is not null && !File.Exists(pdbPath)))
            {
                continue;
            }

            try
            {
                var canonicalModuleBytes = File.ReadAllBytes(canonicalModulePath);
                var canonicalModule = LuaAotModuleIdentity.DeserializeCanonicalModule(
                    canonicalModuleBytes);
                var logicalName = options.ModuleName.Replace('.', '/') +
                    (inputKind == LunilBuildInputKind.BinaryChunk ? ".luac" : ".lua");
                var cacheKey = CreateCacheKey(
                    options,
                    inputKind,
                    sourceSha256,
                    canonicalModule,
                    logicalName);
                if (!string.Equals(
                    manifest.CacheId,
                    cacheKey.CacheId,
                    StringComparison.Ordinal))
                {
                    continue;
                }

                var peImage = File.ReadAllBytes(pePath).ToImmutableArray();
                var pdbImage = pdbPath is null
                    ? ImmutableArray<byte>.Empty
                    : File.ReadAllBytes(pdbPath).ToImmutableArray();
                var validation = LuaAotArtifactLoader.Validate(
                    peImage,
                    pdbImage,
                    new LuaAotLoadOptions
                    {
                        ExpectedModuleContentId = cacheKey.CanonicalModuleHash,
                    });
                if (!validation.Succeeded || validation.Manifest is null ||
                    !string.Equals(
                        manifest.ModuleContentId,
                        validation.Manifest.ModuleContentId,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        manifest.AssemblyName,
                        validation.Manifest.AssemblyName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        manifest.TypeName,
                        validation.Manifest.TypeName,
                        StringComparison.Ordinal) ||
                    !string.Equals(
                        manifest.PeFile,
                        validation.Manifest.AssemblyName + ".dll",
                        StringComparison.Ordinal) ||
                    (manifest.PdbFile is not null && !string.Equals(
                        manifest.PdbFile,
                        validation.Manifest.PortablePdbName,
                        StringComparison.Ordinal)))
                {
                    continue;
                }

                var artifactPaths = new List<string>
                {
                    pePath,
                    canonicalModulePath,
                    manifestPath,
                };
                if (pdbPath is not null)
                {
                    artifactPaths.Add(pdbPath);
                }

                return new CompiledModule(
                    manifest.ModuleName,
                    validation.Manifest.ModuleContentId,
                    validation.Manifest.TypeName,
                    validation.Manifest.Functions,
                    Convert.ToBase64String(canonicalModuleBytes),
                    pePath,
                    artifactPaths);
            }
            catch (Exception exception) when (exception is IOException or InvalidDataException or
                UnauthorizedAccessException or ArgumentException or BadImageFormatException)
            {
                Log.LogMessage(
                    MessageImportance.Low,
                    $"Lunil local artifact manifest '{manifestPath}' was rejected: {exception.Message}");
            }
        }

        return null;
    }

    private void LogFrontendDiagnostic(string path, SourceText source, Diagnostic diagnostic)
    {
        var start = source.GetLocation(Math.Min(diagnostic.Span.Start, source.Length));
        var end = source.GetLocation(Math.Min(diagnostic.Span.End, source.Length));
        if (diagnostic.Severity == DiagnosticSeverity.Error)
        {
            Log.LogError(
                "Lunil",
                LunilBuildDiagnosticCodes.CompilationFailed,
                null,
                path,
                start.Line + 1,
                start.Utf16Column + 1,
                end.Line + 1,
                end.Utf16Column + 1,
                $"[{diagnostic.Code}] {diagnostic.Message}");
        }
        else if (diagnostic.Severity == DiagnosticSeverity.Warning)
        {
            Log.LogWarning(
                "Lunil",
                diagnostic.Code,
                null,
                path,
                start.Line + 1,
                start.Utf16Column + 1,
                end.Line + 1,
                end.Utf16Column + 1,
                diagnostic.Message);
        }
    }

    private void LogBuildError(string code, string? path, int line, int column, string message) =>
        Log.LogError(
            "Lunil",
            code,
            null,
            path,
            line,
            column,
            line,
            column,
            message);

    private static int GetAotDiagnosticLine(
        Lunil.IR.Canonical.LuaIrModule module,
        LuaAotDiagnostic diagnostic)
    {
        if (diagnostic.FunctionId < 0 || diagnostic.FunctionId >= module.Functions.Length)
        {
            return 0;
        }

        var function = module.Functions[diagnostic.FunctionId];
        return diagnostic.CanonicalProgramCounter >= 0 &&
            diagnostic.CanonicalProgramCounter < function.Instructions.Length
            ? function.Instructions[diagnostic.CanonicalProgramCounter].SourceLine
            : function.LineDefined;
    }

    private static TaskItem CreateReference(string path)
    {
        var item = new TaskItem(path);
        item.SetMetadata("Private", "true");
        item.SetMetadata("ReferenceOutputAssembly", "true");
        return item;
    }

    private static string GenerateRegistrySource(IReadOnlyList<CompiledModule> modules)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("namespace Lunil.Generated.Build;");
        builder.AppendLine();
        builder.AppendLine("internal static class LunilModuleRegistry");
        builder.AppendLine("{");
        builder.AppendLine("    [global::System.Runtime.CompilerServices.ModuleInitializer]");
        builder.AppendLine("    internal static void Initialize()");
        builder.AppendLine("    {");
        foreach (var module in modules.OrderBy(static module => module.ModuleName, StringComparer.Ordinal))
        {
            builder.Append("        global::Lunil.CodeGen.Cil.LuaStaticAotRegistry.Register(new global::Lunil.CodeGen.Cil.LuaStaticAotModule(")
                .Append(CSharpString(module.ModuleName)).Append(", ")
                .Append(CSharpString(module.ModuleContentId)).AppendLine(",");
            builder.Append("            global::Lunil.CodeGen.Cil.LuaAotModuleIdentity.DeserializeCanonicalModule(global::System.Convert.FromBase64String(")
                .Append(CSharpString(module.CanonicalModuleBase64)).AppendLine(")),");
            builder.AppendLine("            new global::System.Collections.Generic.Dictionary<int, global::Lunil.CodeGen.Cil.Emission.LuaCompiledMethod>");
            builder.AppendLine("            {");
            foreach (var function in module.Functions.OrderBy(static function => function.FunctionId))
            {
                builder.Append("                [").Append(function.FunctionId).Append("] = ");
                if (function.Shards.Length == 1)
                {
                    builder.Append("global::").Append(module.TypeName).Append('.')
                        .Append(function.Shards[0].MethodName).AppendLine(",");
                }
                else
                {
                    builder.Append("Function_").Append(module.Identifier).Append('_')
                        .Append(function.FunctionId).AppendLine(",");
                }
            }

            builder.AppendLine("            }));");
        }

        builder.AppendLine("    }");
        foreach (var module in modules)
        {
            foreach (var function in module.Functions.Where(static function => function.Shards.Length != 1))
            {
                builder.AppendLine();
                builder.Append("    private static global::Lunil.Runtime.CodeGen.LuaCompiledExit Function_")
                    .Append(module.Identifier).Append('_').Append(function.FunctionId).AppendLine("(");
                builder.AppendLine("        global::Lunil.Runtime.CodeGen.LuaExecutionContext context,");
                builder.AppendLine("        global::Lunil.Runtime.Execution.LuaThread thread,");
                builder.AppendLine("        global::Lunil.Runtime.Execution.LuaFrame frame)");
                builder.AppendLine("    {");
                foreach (var shard in function.Shards)
                {
                    builder.Append("        if (frame.ProgramCounter < ")
                        .Append(checked(shard.StartProgramCounter + shard.InstructionCount))
                        .AppendLine(")");
                    builder.AppendLine("        {");
                    builder.Append("            return global::").Append(module.TypeName).Append('.')
                        .Append(shard.MethodName).AppendLine("(context, thread, frame);");
                    builder.AppendLine("        }");
                }

                builder.AppendLine("        return global::Lunil.Runtime.CodeGen.LuaCompiledExit.Deopt(");
                builder.AppendLine("            frame.ProgramCounter,");
                builder.AppendLine("            0,");
                builder.AppendLine("            global::Lunil.Runtime.CodeGen.LuaCompiledExitReason.UnsupportedInstruction);");
                builder.AppendLine("    }");
            }
        }

        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string CSharpString(string value) => JsonSerializer.Serialize(value);

    private static string SanitizeFileName(string moduleName)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        return string.Concat(moduleName.Select(character =>
            invalid.Contains(character) || character is '.' or '-' ? '_' : character));
    }

    private static void AtomicWriteBytes(string path, ReadOnlySpan<byte> content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(content))
        {
            return;
        }

        var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporaryPath, content.ToArray());
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporaryPath);
        }
    }

    private static void AtomicWriteText(string path, string content) =>
        AtomicWriteBytes(path, Encoding.UTF8.GetBytes(content));

    private static FileStream AcquireOutputLock(string outputDirectory)
    {
        var path = Path.Combine(outputDirectory, ".lunil-build.lock");
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            try
            {
                return new FileStream(
                    path,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.None);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                Thread.Sleep(25);
            }
        }
    }

    private static readonly JsonSerializerOptions BuildManifestJsonOptions = new()
    {
        WriteIndented = true,
    };

    private static readonly string CompilerVersion =
        typeof(LuaAotCompiler).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion ??
        typeof(LuaAotCompiler).Assembly.GetName().Version?.ToString() ??
        "unknown";

    private sealed record CompiledModule(
        string ModuleName,
        string ModuleContentId,
        string TypeName,
        ImmutableArray<LuaAotFunctionManifest> Functions,
        string CanonicalModuleBase64,
        string PePath,
        IReadOnlyList<string> ArtifactPaths)
    {
        public string Identifier => ModuleContentId[..16];
    }

    private sealed class LunilBuildModuleManifest
    {
        public required int SchemaVersion { get; init; }

        public required string CacheId { get; init; }

        public required string ModuleName { get; init; }

        public required string ModuleContentId { get; init; }

        public required string SourcePath { get; init; }

        public required string SourceSha256 { get; init; }

        public required string InputKind { get; init; }

        public required string Optimization { get; init; }

        public required bool DebugSymbols { get; init; }

        public required string Sandbox { get; init; }

        public required string TargetFramework { get; init; }

        public required string RuntimeIdentifier { get; init; }

        public required string AssemblyName { get; init; }

        public required string TypeName { get; init; }

        public required string PeFile { get; init; }

        public string? PdbFile { get; init; }

        public required string CanonicalModuleFile { get; init; }

        public required ImmutableArray<LuaAotFunctionManifest> Functions { get; init; }
    }
}
