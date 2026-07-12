namespace Lunil.Build.Tasks;

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
