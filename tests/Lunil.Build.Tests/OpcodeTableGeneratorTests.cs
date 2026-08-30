using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Lunil.IR.Generators;

namespace Lunil.Build.Tests;

public sealed class OpcodeTableGeneratorTests
{
    [Theory]
    [InlineData(typeof(Lua51OpcodeTableGenerator), "Lunil.IR.Lua51.Lua51Opcode", "Lua51GeneratedOpcodeTable", "LoadConstant")]
    [InlineData(typeof(Lua52OpcodeTableGenerator), "Lunil.IR.Lua52.Lua52Opcode", "Lua52GeneratedOpcodeTable", "LoadConstant")]
    [InlineData(typeof(Lua53OpcodeTableGenerator), "Lunil.IR.Lua53.Lua53Opcode", "Lua53GeneratedOpcodeTable", "LoadConstant")]
    [InlineData(typeof(Lua55OpcodeTableGenerator), "Lunil.IR.Lua55.Lua55Opcode", "Lua55GeneratedOpcodeTable", "LoadConstant")]
    public void ContiguousOpcodesProduceValidTables(
        Type generatorType,
        string enumMetadataName,
        string expectedTypeName,
        string expectedOpcodeName)
    {
        var parts = enumMetadataName.Split('.');
        var namespaceName = string.Join('.', parts[..^1]);
        var enumName = parts[^1];
        var source = "namespace " + namespaceName + ";\n" +
            "public enum " + enumName + "\n" +
            "{\n" +
            "    Move = 0,\n" +
            "    " + expectedOpcodeName + " = 1,\n" +
            "    Return = 2,\n" +
            "}";
        var (diagnostics, sources) = Run(generatorType, source);

        Assert.DoesNotContain(diagnostics, static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
        var generated = Assert.Single(sources, item =>
            item.HintName.Contains(expectedTypeName, StringComparison.Ordinal));
        var text = generated.Text;
        Assert.Contains(expectedTypeName, text, StringComparison.Ordinal);
        Assert.Contains(expectedOpcodeName, text, StringComparison.Ordinal);

        var parsed = CSharpSyntaxTree.ParseText(text);
        Assert.DoesNotContain(parsed.GetDiagnostics(), static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void GappedOpcodesAreRejectedWithALunilgenDiagnostic()
    {
        var source = "namespace Lunil.IR.Lua51;\n" +
            "public enum Lua51Opcode\n" +
            "{\n" +
            "    Move = 0,\n" +
            "    Return = 2,\n" +
            "}";

        var (diagnostics, _) = Run(typeof(Lua51OpcodeTableGenerator), source);

        Assert.Contains(diagnostics, static diagnostic =>
            diagnostic.Severity == DiagnosticSeverity.Error &&
            diagnostic.Id == "LUNILGEN003");
    }

    private static (ImmutableArray<Diagnostic> Diagnostics, (string HintName, string Text)[] Sources) Run(
        Type generatorType,
        string source)
    {
        var generator = (ISourceGenerator)Activator.CreateInstance(generatorType)!;
        var reference = MetadataReference.CreateFromFile(typeof(object).Assembly.Location);
        var compilation = CSharpCompilation.Create(
            "lunil.build.tests",
            [CSharpSyntaxTree.ParseText(source)],
            [reference],
            new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
        var driver = CSharpGeneratorDriver.Create(generator).RunGenerators(compilation);
        var result = driver.GetRunResult();
        return (result.Diagnostics, result.Results[0].GeneratedSources
            .Select(static item => (item.HintName, item.SourceText.ToString()))
            .ToArray());
    }
}
