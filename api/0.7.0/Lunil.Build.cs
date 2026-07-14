// Target Frameworks: net10.0
#nullable enable

namespace Lunil.Build.Tasks
{
    public static class LunilBuildDiagnosticCodes
    {
        public const string MissingSource = "LUNIL1001";
        public const string InvalidModuleName = "LUNIL1002";
        public const string InvalidOptimization = "LUNIL1003";
        public const string InvalidDebugSymbols = "LUNIL1004";
        public const string InvalidSandbox = "LUNIL1005";
        public const string DuplicateModuleName = "LUNIL1006";
        public const string InvalidOutputPath = "LUNIL1007";
        public const string InvalidInputKind = "LUNIL1008";
        public const string CompilationFailed = "LUNIL2001";
        public const string ArtifactEmissionFailed = "LUNIL2002";
        public const string InvalidBinaryChunk = "LUNIL2003";
        public const string InternalBuildFailure = "LUNIL9001";
    }

    public enum LunilBuildInputKind
    {
        Auto = 0,
        Source = 1,
        BinaryChunk = 2
    }

    public enum LunilBuildOptimization
    {
        Debug = 0,
        Release = 1
    }

    public enum LunilBuildSandbox
    {
        Default = 0,
        Trusted = 1,
        Restricted = 2
    }

    public sealed class LunilCompileItemOptions : System.IEquatable<Lunil.Build.Tasks.LunilCompileItemOptions>
    {
        public string SourcePath { get => throw null; init { } }
        public string ModuleName { get => throw null; init { } }
        public Lunil.Build.Tasks.LunilBuildInputKind InputKind { get => throw null; init { } }
        public Lunil.Build.Tasks.LunilBuildOptimization Optimization { get => throw null; init { } }
        public bool DebugSymbols { get => throw null; init { } }
        public Lunil.Build.Tasks.LunilBuildSandbox Sandbox { get => throw null; init { } }
        public LunilCompileItemOptions(string SourcePath, string ModuleName, Lunil.Build.Tasks.LunilBuildInputKind InputKind, Lunil.Build.Tasks.LunilBuildOptimization Optimization, bool DebugSymbols, Lunil.Build.Tasks.LunilBuildSandbox Sandbox) { }
        public override string ToString() => throw null;
        public static bool operator !=(Lunil.Build.Tasks.LunilCompileItemOptions? left, Lunil.Build.Tasks.LunilCompileItemOptions? right) => throw null;
        public static bool operator ==(Lunil.Build.Tasks.LunilCompileItemOptions? left, Lunil.Build.Tasks.LunilCompileItemOptions? right) => throw null;
        public override int GetHashCode() => throw null;
        public override bool Equals(object? obj) => throw null;
        public bool Equals(Lunil.Build.Tasks.LunilCompileItemOptions? other) => throw null;
        public void Deconstruct(out string SourcePath, out string ModuleName, out Lunil.Build.Tasks.LunilBuildInputKind InputKind, out Lunil.Build.Tasks.LunilBuildOptimization Optimization, out bool DebugSymbols, out Lunil.Build.Tasks.LunilBuildSandbox Sandbox) => throw null;
    }

    public sealed class LunilCompileTask : Microsoft.Build.Utilities.Task
    {
        public Microsoft.Build.Framework.ITaskItem[] Sources { get => throw null; set { } }
        public string IntermediateOutputPath { get => throw null; set { } }
        public string ProjectDirectory { get => throw null; set { } }
        public string TargetFramework { get => throw null; set { } }
        public string RuntimeIdentifier { get => throw null; set { } }
        public string DesignTimeBuild { get => throw null; set { } }
        public bool CacheEnabled { get => throw null; set { } }
        public string CacheDirectory { get => throw null; set { } }
        public long CacheMaximumBytes { get => throw null; set { } }
        public long CacheMaximumEntryBytes { get => throw null; set { } }
        public long CacheMaximumQuarantineBytes { get => throw null; set { } }
        public string PublishAot { get => throw null; set { } }
        public string PublishReadyToRun { get => throw null; set { } }
        public string PublishTrimmed { get => throw null; set { } }
        public Microsoft.Build.Framework.ITaskItem[] GeneratedSources { get => throw null; }
        public Microsoft.Build.Framework.ITaskItem[] GeneratedReferences { get => throw null; }
        public Microsoft.Build.Framework.ITaskItem[] GeneratedArtifacts { get => throw null; }
        public override bool Execute() => throw null;
    }
}
