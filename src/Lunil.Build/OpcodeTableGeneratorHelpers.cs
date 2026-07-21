using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Lunil.IR.Generators;

internal readonly record struct OpcodeDefinition(string Name, int Value);

internal static class OpcodeTableGeneratorHelpers
{
    internal static bool TryGetValidatedValues(
        GeneratorExecutionContext context,
        string metadataName,
        int maximumValue,
        DiagnosticDescriptor invalidLayout,
        out OpcodeDefinition[] values)
    {
        var opcode = context.Compilation.GetTypeByMetadataName(metadataName);
        if (opcode is null || opcode.TypeKind != TypeKind.Enum)
        {
            values = Array.Empty<OpcodeDefinition>();
            return false;
        }

        values = opcode.GetMembers()
            .OfType<IFieldSymbol>()
            .Where(static field => field.HasConstantValue)
            .Select(static field => new OpcodeDefinition(
                field.Name,
                Convert.ToInt32(field.ConstantValue, CultureInfo.InvariantCulture)))
            .OrderBy(static item => item.Value)
            .ToArray();
        if (values.Length == 0)
        {
            return false;
        }

        for (var index = 0; index < values.Length; index++)
        {
            if (values[index].Value == index && values[index].Value <= maximumValue)
            {
                continue;
            }

            context.ReportDiagnostic(Diagnostic.Create(
                invalidLayout,
                Location.None,
                index,
                values[index].Value,
                values[index].Name));
            values = Array.Empty<OpcodeDefinition>();
            return false;
        }

        return true;
    }
}
