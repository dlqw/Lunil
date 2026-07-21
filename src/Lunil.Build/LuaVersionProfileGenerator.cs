using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Lunil.IR.Generators;

[Generator]
public sealed class LuaVersionProfileGenerator : ISourceGenerator
{
    private static readonly DiagnosticDescriptor MissingChunkFormat = new(
        "LUNILGEN002",
        "Version adapter has no chunk format",
        "The enabled adapter symbol for '{0}' requires a LuaVersionProfile ChunkFormat declaration",
        "Lunil.Build",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public void Initialize(GeneratorInitializationContext context)
    {
    }

    public void Execute(GeneratorExecutionContext context)
    {
        if (!string.Equals(context.Compilation.AssemblyName, "Lunil.Core", StringComparison.Ordinal))
        {
            return;
        }

        var version = context.Compilation.GetTypeByMetadataName("Lunil.Core.LuaLanguageVersion");
        if (version is null || version.TypeKind != TypeKind.Enum)
        {
            return;
        }

        var profileAttribute = context.Compilation.GetTypeByMetadataName(
            "Lunil.Core.LuaVersionProfileAttribute");
        if (profileAttribute is null)
        {
            return;
        }

        var adapterSymbols = context.ParseOptions.PreprocessorSymbolNames;
        var values = version.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => field.HasConstantValue)
            .OrderBy(static field => Convert.ToInt32(field.ConstantValue, CultureInfo.InvariantCulture))
            .Select(field =>
            {
                var attribute = field.GetAttributes().FirstOrDefault(candidate =>
                    SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, profileAttribute));
                var synchronousFinalizerErrors = false;
                var supportsGenerationalCollection = false;
                var preservesDeadThreadOpenUpvalues = false;
                var cachesClosuresByUpvalues = false;
                var arithmeticStringCoercionProducesFloat = false;
                var coercesNumericStringsForBitwiseOperations = false;
                var hasWarnLibrary = false;
                var hasCoroutineClose = false;
                var hasToBeClosedProtocol = false;
                var hasUtf8Library = false;
                var hasBit32Library = false;
                var hasRawLength = false;
                var hasGlobalUnpack = false;
                var hasLoadString = false;
                var hasModuleLibrary = false;
                var hasTableMove = false;
                var hasTablePack = false;
                var hasTableCreate = false;
                var hasStringPack = false;
                var hasStringGFind = false;
                var hasLegacyMath = false;
                var hasDebugSetCStackLimit = false;
                var hasPackageSearchers = false;
                var hasPackageLoaders = false;
                var hasPackageSeeAll = false;
                var hasLegacyTable = false;
                var chunkFormat = "LuaChunkFormat.None";
                if (attribute is not null)
                {
                    foreach (var named in attribute.NamedArguments)
                    {
                        if (named.Key == "ChunkFormat" && named.Value.Value is not null)
                        {
                            chunkFormat = Convert.ToInt32(
                                named.Value.Value,
                                CultureInfo.InvariantCulture) switch
                            {
                                1 => "LuaChunkFormat.Lua53",
                                2 => "LuaChunkFormat.Lua54",
                                3 => "LuaChunkFormat.Lua52",
                                4 => "LuaChunkFormat.Lua51",
                                5 => "LuaChunkFormat.Lua55",
                                _ => "LuaChunkFormat.None",
                            };
                        }
                        else if (named.Key == "SynchronousFinalizerErrors" &&
                            named.Value.Value is bool synchronous)
                        {
                            synchronousFinalizerErrors = synchronous;
                        }
                        else if (named.Key == "SupportsGenerationalCollection" &&
                            named.Value.Value is bool generational)
                        {
                            supportsGenerationalCollection = generational;
                        }
                        else if (named.Key == "PreservesDeadThreadOpenUpvalues" &&
                            named.Value.Value is bool preserves)
                        {
                            preservesDeadThreadOpenUpvalues = preserves;
                        }
                        else if (named.Key == "CachesClosuresByUpvalues" &&
                            named.Value.Value is bool caches)
                        {
                            cachesClosuresByUpvalues = caches;
                        }
                        else if (named.Key == "ArithmeticStringCoercionProducesFloat" &&
                            named.Value.Value is bool arithmeticStringFloat)
                        {
                            arithmeticStringCoercionProducesFloat = arithmeticStringFloat;
                        }
                        else if (named.Key == "CoercesNumericStringsForBitwiseOperations" &&
                            named.Value.Value is bool bitwiseStringCoercion)
                        {
                            coercesNumericStringsForBitwiseOperations = bitwiseStringCoercion;
                        }
                        else if (named.Key == "HasWarnLibrary" &&
                            named.Value.Value is bool hasWarn)
                        {
                            hasWarnLibrary = hasWarn;
                        }
                        else if (named.Key == "HasCoroutineClose" &&
                            named.Value.Value is bool hasClose)
                        {
                            hasCoroutineClose = hasClose;
                        }
                        else if (named.Key == "HasToBeClosedProtocol" &&
                            named.Value.Value is bool hasToBeClosed)
                        {
                            hasToBeClosedProtocol = hasToBeClosed;
                        }
                        else if (named.Key == "HasUtf8Library" && named.Value.Value is bool hasUtf8)
                        {
                            hasUtf8Library = hasUtf8;
                        }
                        else if (named.Key == "HasBit32Library" && named.Value.Value is bool hasBit32)
                        {
                            hasBit32Library = hasBit32;
                        }
                        else if (named.Key == "HasRawLength" && named.Value.Value is bool rawLength)
                        {
                            hasRawLength = rawLength;
                        }
                        else if (named.Key == "HasGlobalUnpack" && named.Value.Value is bool globalUnpack)
                        {
                            hasGlobalUnpack = globalUnpack;
                        }
                        else if (named.Key == "HasLoadString" && named.Value.Value is bool loadString)
                        {
                            hasLoadString = loadString;
                        }
                        else if (named.Key == "HasModuleLibrary" && named.Value.Value is bool moduleLibrary)
                        {
                            hasModuleLibrary = moduleLibrary;
                        }
                        else if (named.Key == "HasTableMove" && named.Value.Value is bool tableMove)
                        {
                            hasTableMove = tableMove;
                        }
                        else if (named.Key == "HasTablePack" && named.Value.Value is bool tablePack)
                        {
                            hasTablePack = tablePack;
                        }
                        else if (named.Key == "HasTableCreate" && named.Value.Value is bool tableCreate)
                        {
                            hasTableCreate = tableCreate;
                        }
                        else if (named.Key == "HasStringPack" && named.Value.Value is bool stringPack)
                        {
                            hasStringPack = stringPack;
                        }
                        else if (named.Key == "HasStringGFind" && named.Value.Value is bool stringGFind)
                        {
                            hasStringGFind = stringGFind;
                        }
                        else if (named.Key == "HasLegacyMath" && named.Value.Value is bool legacyMath)
                        {
                            hasLegacyMath = legacyMath;
                        }
                        else if (named.Key == "HasDebugSetCStackLimit" && named.Value.Value is bool debugSetCStackLimit)
                        {
                            hasDebugSetCStackLimit = debugSetCStackLimit;
                        }
                        else if (named.Key == "HasPackageSearchers" && named.Value.Value is bool packageSearchers)
                        {
                            hasPackageSearchers = packageSearchers;
                        }
                        else if (named.Key == "HasPackageLoaders" && named.Value.Value is bool packageLoaders)
                        {
                            hasPackageLoaders = packageLoaders;
                        }
                        else if (named.Key == "HasPackageSeeAll" && named.Value.Value is bool packageSeeAll)
                        {
                            hasPackageSeeAll = packageSeeAll;
                        }
                        else if (named.Key == "HasLegacyTable" && named.Value.Value is bool legacyTable)
                        {
                            hasLegacyTable = legacyTable;
                        }
                    }
                }

                return (
                    field.Name,
                    IsImplemented: adapterSymbols.Contains(
                        $"LUNIL_{field.Name.ToUpperInvariant()}_ADAPTER"),
                    ChunkFormat: chunkFormat,
                    SynchronousFinalizerErrors: synchronousFinalizerErrors,
                    SupportsGenerationalCollection: supportsGenerationalCollection,
                    PreservesDeadThreadOpenUpvalues: preservesDeadThreadOpenUpvalues,
                    CachesClosuresByUpvalues: cachesClosuresByUpvalues,
                    ArithmeticStringCoercionProducesFloat: arithmeticStringCoercionProducesFloat,
                    CoercesNumericStringsForBitwiseOperations:
                        coercesNumericStringsForBitwiseOperations,
                    HasWarnLibrary: hasWarnLibrary,
                    HasCoroutineClose: hasCoroutineClose,
                    HasToBeClosedProtocol: hasToBeClosedProtocol,
                    HasUtf8Library: hasUtf8Library,
                    HasBit32Library: hasBit32Library,
                    HasRawLength: hasRawLength,
                    HasGlobalUnpack: hasGlobalUnpack,
                    HasLoadString: hasLoadString,
                    HasModuleLibrary: hasModuleLibrary,
                    HasTableMove: hasTableMove,
                    HasTablePack: hasTablePack,
                    HasTableCreate: hasTableCreate,
                    HasStringPack: hasStringPack,
                    HasStringGFind: hasStringGFind,
                    HasLegacyMath: hasLegacyMath,
                    HasDebugSetCStackLimit: hasDebugSetCStackLimit,
                    HasPackageSearchers: hasPackageSearchers,
                    HasPackageLoaders: hasPackageLoaders,
                    HasPackageSeeAll: hasPackageSeeAll,
                    HasLegacyTable: hasLegacyTable);
            })
            .ToArray();
        if (values.Length == 0)
        {
            return;
        }

        foreach (var value in values)
        {
            if (value.IsImplemented && value.ChunkFormat == "LuaChunkFormat.None")
            {
                context.ReportDiagnostic(Diagnostic.Create(
                    MissingChunkFormat,
                    Location.None,
                    value.Name));
                return;
            }
        }

        var cases = string.Join(
            ",\n            ",
            values.Select(static value =>
                $"LuaLanguageVersion.{value.Name} => new LuaVersionFeatures(" +
                $"{value.IsImplemented.ToString().ToLowerInvariant()}, " +
                $"{value.ChunkFormat}, " +
                $"{value.SynchronousFinalizerErrors.ToString().ToLowerInvariant()}, " +
                $"{value.SupportsGenerationalCollection.ToString().ToLowerInvariant()}, " +
                $"{value.PreservesDeadThreadOpenUpvalues.ToString().ToLowerInvariant()}, " +
                $"{value.CachesClosuresByUpvalues.ToString().ToLowerInvariant()}, " +
                $"{value.ArithmeticStringCoercionProducesFloat.ToString().ToLowerInvariant()}, " +
                $"{value.CoercesNumericStringsForBitwiseOperations.ToString().ToLowerInvariant()}, " +
                        $"{value.HasWarnLibrary.ToString().ToLowerInvariant()}, " +
                        $"{value.HasCoroutineClose.ToString().ToLowerInvariant()}, " +
                        $"{value.HasToBeClosedProtocol.ToString().ToLowerInvariant()}, " +
                        $"{value.HasUtf8Library.ToString().ToLowerInvariant()}, " +
                        $"{value.HasBit32Library.ToString().ToLowerInvariant()}, " +
                        $"{value.HasRawLength.ToString().ToLowerInvariant()}, " +
                        $"{value.HasGlobalUnpack.ToString().ToLowerInvariant()}, " +
                        $"{value.HasLoadString.ToString().ToLowerInvariant()}, " +
                        $"{value.HasModuleLibrary.ToString().ToLowerInvariant()}, " +
                        $"{value.HasTableMove.ToString().ToLowerInvariant()}, " +
                        $"{value.HasTablePack.ToString().ToLowerInvariant()}, " +
                        $"{value.HasTableCreate.ToString().ToLowerInvariant()}, " +
                        $"{value.HasStringPack.ToString().ToLowerInvariant()}, " +
                        $"{value.HasStringGFind.ToString().ToLowerInvariant()}, " +
                        $"{value.HasLegacyMath.ToString().ToLowerInvariant()}, " +
                        $"{value.HasDebugSetCStackLimit.ToString().ToLowerInvariant()}, " +
                        $"{value.HasPackageSearchers.ToString().ToLowerInvariant()}, " +
                        $"{value.HasPackageLoaders.ToString().ToLowerInvariant()}, " +
                        $"{value.HasPackageSeeAll.ToString().ToLowerInvariant()}, " +
                        $"{value.HasLegacyTable.ToString().ToLowerInvariant()})"));
        var source = $$"""
            // <auto-generated />
            namespace Lunil.Core;

            /// <summary>Generated feature contract for each supported Lua version.</summary>
            public readonly record struct LuaVersionFeatures(
                bool IsImplemented,
                LuaChunkFormat ChunkFormat,
                bool SynchronousFinalizerErrors,
                bool SupportsGenerationalCollection,
                bool PreservesDeadThreadOpenUpvalues,
                bool CachesClosuresByUpvalues,
                bool ArithmeticStringCoercionProducesFloat,
                bool CoercesNumericStringsForBitwiseOperations,
                bool HasWarnLibrary,
                bool HasCoroutineClose,
                bool HasToBeClosedProtocol,
                bool HasUtf8Library,
                bool HasBit32Library,
                bool HasRawLength,
                bool HasGlobalUnpack,
                bool HasLoadString,
                bool HasModuleLibrary,
                bool HasTableMove,
                bool HasTablePack,
                bool HasTableCreate,
                bool HasStringPack,
                bool HasStringGFind,
                bool HasLegacyMath,
                bool HasDebugSetCStackLimit,
                bool HasPackageSearchers,
                bool HasPackageLoaders,
                bool HasPackageSeeAll,
                bool HasLegacyTable);

            public static class LuaVersionFeatureTable
            {
                public static LuaVersionFeatures Get(LuaLanguageVersion version) => version switch
                {
                    {{cases}},
                    _ => throw new System.ArgumentOutOfRangeException(nameof(version), version,
                        "Unknown Lua language version."),
                };
            }
            """;
        context.AddSource("LuaVersionFeatures.g.cs", source);
    }
}
