using System.Text.RegularExpressions;
using Microsoft.Build.Framework;

namespace Lunil.Build.Tasks;

public enum LunilBuildOptimization
{
    Debug,
    Release,
}

public enum LunilBuildSandbox
{
    Default,
    Trusted,
    Restricted,
}

public enum LunilBuildInputKind
{
    Auto,
    Source,
    BinaryChunk,
}

public sealed record LunilCompileItemOptions(
    string SourcePath,
    string ModuleName,
    LunilBuildInputKind InputKind,
    LunilBuildOptimization Optimization,
    bool DebugSymbols,
    LunilBuildSandbox Sandbox)
{
    private static readonly Regex ValidModuleName = new(
        "^[A-Za-z_][A-Za-z0-9_.-]*$",
        RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    internal static bool TryCreate(
        ITaskItem item,
        string projectDirectory,
        Action<string, string> reportError,
        out LunilCompileItemOptions? options)
    {
        ArgumentNullException.ThrowIfNull(item);
        ArgumentNullException.ThrowIfNull(projectDirectory);
        ArgumentNullException.ThrowIfNull(reportError);

        options = null;
        var sourcePath = Path.GetFullPath(item.ItemSpec, projectDirectory);
        if (!File.Exists(sourcePath))
        {
            reportError(
                LunilBuildDiagnosticCodes.MissingSource,
                $"Lua source file '{sourcePath}' does not exist.");
            return false;
        }

        var moduleName = MetadataOrDefault(
            item,
            "ModuleName",
            Path.GetFileNameWithoutExtension(sourcePath));
        if (!ValidModuleName.IsMatch(moduleName))
        {
            reportError(
                LunilBuildDiagnosticCodes.InvalidModuleName,
                $"ModuleName '{moduleName}' is invalid. Use an identifier-like dotted name.");
            return false;
        }

        var optimizationText = MetadataOrDefault(item, "Optimization", "Debug");
        if (!Enum.TryParse<LunilBuildOptimization>(optimizationText, true, out var optimization))
        {
            reportError(
                LunilBuildDiagnosticCodes.InvalidOptimization,
                $"Optimization '{optimizationText}' is invalid. Expected Debug or Release.");
            return false;
        }

        var inputKindText = MetadataOrDefault(item, "InputKind", "Auto");
        if (!Enum.TryParse<LunilBuildInputKind>(inputKindText, true, out var inputKind))
        {
            reportError(
                LunilBuildDiagnosticCodes.InvalidInputKind,
                $"InputKind '{inputKindText}' is invalid. Expected Auto, Source, or BinaryChunk.");
            return false;
        }

        var debugText = MetadataOrDefault(item, "DebugSymbols", "false");
        if (!bool.TryParse(debugText, out var debugSymbols))
        {
            reportError(
                LunilBuildDiagnosticCodes.InvalidDebugSymbols,
                $"DebugSymbols '{debugText}' is invalid. Expected true or false.");
            return false;
        }

        var sandboxText = MetadataOrDefault(item, "Sandbox", "Default");
        if (!Enum.TryParse<LunilBuildSandbox>(sandboxText, true, out var sandbox))
        {
            reportError(
                LunilBuildDiagnosticCodes.InvalidSandbox,
                $"Sandbox '{sandboxText}' is invalid. Expected Default, Trusted, or Restricted.");
            return false;
        }

        options = new LunilCompileItemOptions(
            sourcePath,
            moduleName,
            inputKind,
            optimization,
            debugSymbols,
            sandbox);
        return true;
    }

    private static string MetadataOrDefault(ITaskItem item, string name, string fallback)
    {
        var value = item.GetMetadata(name);
        return string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
    }
}
