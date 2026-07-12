using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Lunil.CodeGen.Cil;
using Lunil.CodeGen.Cil.Artifacts;
using Lunil.Core.Diagnostics;
using Lunil.Core.Text;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Lunil.Build.Tasks;

public sealed class LunilCompileTask : Microsoft.Build.Utilities.Task
{
    [Required]
    public ITaskItem[] Sources { get; set; } = [];

    [Required]
    public string IntermediateOutputPath { get; set; } = string.Empty;

    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    public string TargetFramework { get; set; } = string.Empty;

    public string RuntimeIdentifier { get; set; } = string.Empty;

    public string DesignTimeBuild { get; set; } = string.Empty;

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

        var compiledModules = new List<CompiledModule>(optionsBySource.Count);
        var artifacts = new List<ITaskItem>();
        foreach (var options in optionsBySource)
        {
            var module = CompileModule(options, outputDirectory);
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
        string outputDirectory)
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
            var parsing = LuaParser.Parse(source);
            var binding = LuaBinder.Bind(parsing);
            var lowering = LuaLowerer.Lower(binding);
            var frontendDiagnostics = parsing.Diagnostics
                .Concat(binding.Diagnostics)
                .Concat(lowering.Diagnostics)
                .DistinctBy(static diagnostic => (
                    diagnostic.Code,
                    diagnostic.Severity,
                    diagnostic.Span,
                    diagnostic.Message));
            foreach (var diagnostic in frontendDiagnostics)
            {
                LogFrontendDiagnostic(options.SourcePath, source, diagnostic);
            }

            if (!lowering.Succeeded || lowering.Module is null || Log.HasLoggedErrors)
            {
                return null;
            }

            canonicalModule = lowering.Module;
        }

        var logicalName = options.ModuleName.Replace('.', '/') +
            (inputKind == LunilBuildInputKind.BinaryChunk ? ".luac" : ".lua");
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
        var stem = SanitizeFileName(options.ModuleName) + "." + artifact.Manifest.ModuleContentId[..16];
        var pePath = Path.Combine(outputDirectory, artifact.Manifest.AssemblyName + ".dll");
        var pdbPath = Path.Combine(outputDirectory, artifact.Manifest.AssemblyName + ".pdb");
        var canonicalModulePath = Path.Combine(outputDirectory, artifact.Manifest.AssemblyName + ".lir");
        var manifestPath = Path.Combine(outputDirectory, stem + ".lunil.json");
        AtomicWriteBytes(pePath, artifact.PeImage.AsSpan());
        var canonicalModuleBytes = LuaAotModuleIdentity.SerializeCanonicalModule(canonicalModule);
        AtomicWriteBytes(canonicalModulePath, canonicalModuleBytes);
        var artifactPaths = new List<string> { pePath, canonicalModulePath };
        if (!artifact.PortablePdbImage.IsDefaultOrEmpty)
        {
            AtomicWriteBytes(pdbPath, artifact.PortablePdbImage.AsSpan());
            artifactPaths.Add(pdbPath);
        }

        var buildManifest = new LunilBuildModuleManifest
        {
            SchemaVersion = 1,
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
            PdbFile = artifact.PortablePdbImage.IsDefaultOrEmpty ? null : Path.GetFileName(pdbPath),
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
            Convert.ToBase64String(canonicalModuleBytes),
            pePath,
            artifactPaths);
    }

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

            if (manifest is null || manifest.SchemaVersion != 1 ||
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

            var artifactPaths = new List<string> { pePath, canonicalModulePath, manifestPath };
            if (pdbPath is not null)
            {
                artifactPaths.Add(pdbPath);
            }

            return new CompiledModule(
                manifest.ModuleName,
                manifest.ModuleContentId,
                manifest.TypeName,
                manifest.Functions,
                Convert.ToBase64String(File.ReadAllBytes(canonicalModulePath)),
                pePath,
                artifactPaths);
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
