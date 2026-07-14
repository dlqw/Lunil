using System.Text;
using System.Text.Json;
using System.Globalization;
using Lunil.Cli.CommandLine;
using Lunil.Cli.Diagnostics;
using Lunil.Cli.IO;
using Lunil.Compiler;
using Lunil.Core.Diagnostics;
using Lunil.EmmyLua;
using Lunil.IR.Canonical;
using Lunil.IR.Lua54;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Cli.Commands;

internal static class DumpCommand
{
    public static async Task<CliExitCode> ExecuteAsync(CliCommandContext context)
    {
        var input = await CliInputDocument.LoadAsync(
            context.Options.Inputs[0],
            context.Options,
            context.CurrentDirectory,
            context.StandardInput,
            context.CancellationToken).ConfigureAwait(false);
        LuaCompilationResult? compilation = null;
        LuaIrModule? module;
        Lua54Chunk? chunk = null;
        var hasDiagnosticsErrors = false;
        if (input.IsBinaryChunk)
        {
            try
            {
                chunk = Lua54ChunkReader.Read(input.Bytes);
                module = Lua54PrototypeConverter.Convert(chunk);
            }
            catch (Exception exception) when (exception is Lua54ChunkFormatException or
                InvalidDataException or ArgumentException)
            {
                await WriteProblemAsync(context, input.DisplayPath, "LUA8001", exception.Message)
                    .ConfigureAwait(false);
                return CliExitCode.Diagnostics;
            }
        }
        else
        {
            using var host = context.CreateHost([input], out _);
            var workspace = await host.AnalyzeWorkspaceAsync(
                [input.ToWorkspaceDocument()],
                context.CancellationToken).ConfigureAwait(false);
            var diagnostics = CliDiagnosticWriter.FromWorkspace(
                workspace,
                context.Options.WarningsAsErrors);
            await CliDiagnosticWriter.WriteAsync(
                context.StandardError,
                diagnostics,
                context.Options.DiagnosticFormat,
                context.CancellationToken).ConfigureAwait(false);
            hasDiagnosticsErrors = CliDiagnosticWriter.HasErrors(diagnostics);
            compilation = workspace.GetModule(input.ModuleName)?.Compilation;
            module = compilation?.Module;
            if (hasDiagnosticsErrors &&
                context.Options.DumpKind is CliDumpKind.Ir or CliDumpKind.Chunk)
            {
                return CliExitCode.Diagnostics;
            }
        }

        if (input.IsBinaryChunk && context.Options.DumpKind is
            CliDumpKind.Syntax or CliDumpKind.Annotations or CliDumpKind.Analysis)
        {
            await WriteProblemAsync(
                context,
                input.DisplayPath,
                "LUA8003",
                $"Dump kind '{context.Options.DumpKind.ToString().ToLowerInvariant()}' requires source input.")
                .ConfigureAwait(false);
            return CliExitCode.Diagnostics;
        }

        if (module is null && context.Options.DumpKind is CliDumpKind.Ir or CliDumpKind.Chunk)
        {
            await WriteProblemAsync(
                context,
                input.DisplayPath,
                "LUA8004",
                "No canonical module is available for this dump.").ConfigureAwait(false);
            return CliExitCode.Diagnostics;
        }

        if (context.Options.DumpKind == CliDumpKind.Chunk && chunk is null)
        {
            chunk = Lua54CanonicalPrototypeWriter.CreateChunk(module!, module!.MainFunctionId);
        }

        var payload = context.Options.DumpFormat == CliDumpFormat.Json
            ? WriteJson(input, compilation, module, chunk, context.Options.DumpKind)
            : WriteText(input, compilation, module, chunk, context.Options.DumpKind);
        await WriteOutputAsync(context, payload).ConfigureAwait(false);
        return hasDiagnosticsErrors ? CliExitCode.Diagnostics : CliExitCode.Success;
    }

    private static byte[] WriteText(
        CliInputDocument input,
        LuaCompilationResult? compilation,
        LuaIrModule? module,
        Lua54Chunk? chunk,
        CliDumpKind kind)
    {
        var output = new StringBuilder();
        switch (kind)
        {
            case CliDumpKind.Summary:
                output.Append("input: ").AppendLine(input.DisplayPath);
                output.Append("input-kind: ").AppendLine(input.IsBinaryChunk ? "chunk" : "source");
                output.Append("module: ").AppendLine(input.ModuleName);
                output.Append("source-bytes: ").AppendLine(input.Bytes.Length.ToString(CultureInfo.InvariantCulture));
                output.Append("functions: ").AppendLine((module?.Functions.Length ?? 0).ToString(CultureInfo.InvariantCulture));
                output.Append("diagnostics: ").AppendLine(
                    (compilation?.Diagnostics.Length ?? 0).ToString(CultureInfo.InvariantCulture));
                break;
            case CliDumpKind.Syntax:
                WriteSyntaxText(compilation!.Syntax.Root, compilation.Source.Text, output, 0);
                break;
            case CliDumpKind.Annotations:
                foreach (var annotation in compilation!.Annotations.Annotations)
                {
                    output.Append(annotation.Tag).Append(' ')
                        .Append(annotation.Dialect).Append(' ')
                        .Append(annotation.GetType().Name).Append(' ')
                        .Append('[').Append(annotation.Span.Start).Append("..")
                        .Append(annotation.Span.End).AppendLine(")");
                }

                break;
            case CliDumpKind.Analysis:
                output.Append("budget: types=").Append(compilation!.Analysis.BudgetUsage.TypeCount)
                    .Append(" constraints=").Append(compilation.Analysis.BudgetUsage.ConstraintCount)
                    .Append(" cfg-blocks=").Append(compilation.Analysis.BudgetUsage.ControlFlowBlockCount)
                    .Append(" exceeded=").AppendLine(compilation.Analysis.BudgetUsage.WasExceeded.ToString());
                foreach (var symbol in compilation.Analysis.Symbols.OrderBy(static symbol => symbol.Symbol.Id))
                {
                    output.Append("symbol ").Append(symbol.Symbol.Id).Append(' ')
                        .Append(symbol.Symbol.Name).Append(' ')
                        .Append(symbol.InferredType.DisplayName).Append(" declared=")
                        .Append(symbol.DeclaredType.DisplayName).Append(" assigned=")
                        .AppendLine(symbol.IsDefinitelyAssigned.ToString());
                }

                foreach (var function in compilation.Analysis.Functions.OrderBy(static function => function.FunctionId))
                {
                    output.Append("function ").Append(function.FunctionId).Append(" type=")
                        .Append(function.Type.DisplayName).Append(" returns=")
                        .Append(function.InferredReturns.DisplayName).Append(" blocks=")
                        .Append(function.ControlFlowGraph.Blocks.Length).Append(" iterations=")
                        .Append(function.FlowIterationCount).Append(" widened=")
                        .AppendLine(function.WasWidened.ToString());
                }

                break;
            case CliDumpKind.Ir:
                WriteIrText(module!, output);
                break;
            case CliDumpKind.Chunk:
                WriteChunkText(chunk!.MainPrototype, output, 0, "main");
                break;
            default:
                throw new InvalidOperationException("Unknown dump kind.");
        }

        return Encoding.UTF8.GetBytes(output.ToString());
    }

    private static byte[] WriteJson(
        CliInputDocument input,
        LuaCompilationResult? compilation,
        LuaIrModule? module,
        Lua54Chunk? chunk,
        CliDumpKind kind)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WriteString("schema", "lunil.dump.v1");
            writer.WriteString("kind", kind.ToString().ToLowerInvariant());
            writer.WriteString("input", input.DisplayPath);
            writer.WriteString("module", input.ModuleName);
            switch (kind)
            {
                case CliDumpKind.Summary:
                    writer.WriteString("inputKind", input.IsBinaryChunk ? "chunk" : "source");
                    writer.WriteNumber("sourceBytes", input.Bytes.Length);
                    writer.WriteNumber("functions", module?.Functions.Length ?? 0);
                    writer.WriteNumber("diagnostics", compilation?.Diagnostics.Length ?? 0);
                    break;
                case CliDumpKind.Syntax:
                    writer.WritePropertyName("syntax");
                    WriteSyntaxJson(writer, compilation!.Syntax.Root, compilation.Source.Text);
                    break;
                case CliDumpKind.Annotations:
                    writer.WriteStartArray("annotations");
                    foreach (var annotation in compilation!.Annotations.Annotations)
                    {
                        writer.WriteStartObject();
                        writer.WriteString("tag", annotation.Tag);
                        writer.WriteString("dialect", annotation.Dialect.ToString().ToLowerInvariant());
                        writer.WriteString("nodeKind", annotation.GetType().Name);
                        WriteSpan(writer, annotation.Span.Start, annotation.Span.Length);
                        writer.WriteEndObject();
                    }

                    writer.WriteEndArray();
                    break;
                case CliDumpKind.Analysis:
                    WriteAnalysisJson(writer, compilation!);
                    break;
                case CliDumpKind.Ir:
                    WriteIrJson(writer, module!);
                    break;
                case CliDumpKind.Chunk:
                    writer.WritePropertyName("prototype");
                    WriteChunkJson(writer, chunk!.MainPrototype);
                    break;
                default:
                    throw new InvalidOperationException("Unknown dump kind.");
            }

            writer.WriteEndObject();
        }

        output.WriteByte((byte)'\n');
        return output.ToArray();
    }

    private static void WriteSyntaxText(
        LuaSyntaxNode node,
        Lunil.Core.Text.SourceText source,
        StringBuilder output,
        int depth)
    {
        output.Append(' ', depth * 2).Append(node.Kind).Append(' ')
            .Append('[').Append(node.Span.Start).Append("..").Append(node.Span.End)
            .AppendLine(")");
        foreach (var child in node.Children)
        {
            if (child.Node is not null)
            {
                WriteSyntaxText(child.Node, source, output, depth + 1);
            }
            else if (child.Token is not null)
            {
                var token = child.Token;
                var text = Encoding.UTF8.GetString(source.GetSpan(token.Span))
                    .Replace("\r", "\\r", StringComparison.Ordinal)
                    .Replace("\n", "\\n", StringComparison.Ordinal);
                output.Append(' ', (depth + 1) * 2).Append(token.Kind).Append(' ')
                    .Append('[').Append(token.Span.Start).Append("..").Append(token.Span.End)
                    .Append(") '").Append(text).AppendLine("'");
            }
        }
    }

    private static void WriteSyntaxJson(
        Utf8JsonWriter writer,
        LuaSyntaxNode node,
        Lunil.Core.Text.SourceText source)
    {
        writer.WriteStartObject();
        writer.WriteString("nodeKind", node.Kind.ToString());
        WriteSpan(writer, node.Span.Start, node.Span.Length);
        writer.WriteStartArray("children");
        foreach (var child in node.Children)
        {
            if (child.Node is not null)
            {
                WriteSyntaxJson(writer, child.Node, source);
            }
            else if (child.Token is not null)
            {
                var token = child.Token;
                writer.WriteStartObject();
                writer.WriteString("tokenKind", token.Kind.ToString());
                writer.WriteString("text", Encoding.UTF8.GetString(source.GetSpan(token.Span)));
                writer.WriteBoolean("missing", token.IsMissing);
                WriteSpan(writer, token.Span.Start, token.Span.Length);
                writer.WriteEndObject();
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteAnalysisJson(Utf8JsonWriter writer, LuaCompilationResult compilation)
    {
        var analysis = compilation.Analysis;
        writer.WriteStartObject("budget");
        writer.WriteNumber("types", analysis.BudgetUsage.TypeCount);
        writer.WriteNumber("constraints", analysis.BudgetUsage.ConstraintCount);
        writer.WriteNumber("controlFlowBlocks", analysis.BudgetUsage.ControlFlowBlockCount);
        writer.WriteNumber("genericInstantiations", analysis.BudgetUsage.GenericInstantiationCount);
        writer.WriteNumber("maximumTypeDepth", analysis.BudgetUsage.MaximumObservedTypeDepth);
        writer.WriteBoolean("exceeded", analysis.BudgetUsage.WasExceeded);
        writer.WriteEndObject();
        writer.WriteStartArray("symbols");
        foreach (var symbol in analysis.Symbols.OrderBy(static symbol => symbol.Symbol.Id))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", symbol.Symbol.Id);
            writer.WriteString("name", symbol.Symbol.Name);
            writer.WriteString("kind", symbol.Symbol.Kind.ToString().ToLowerInvariant());
            writer.WriteString("declaredType", symbol.DeclaredType.DisplayName);
            writer.WriteString("inferredType", symbol.InferredType.DisplayName);
            writer.WriteBoolean("definitelyAssigned", symbol.IsDefinitelyAssigned);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("functions");
        foreach (var function in analysis.Functions.OrderBy(static function => function.FunctionId))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", function.FunctionId);
            writer.WriteString("type", function.Type.DisplayName);
            writer.WriteString("returns", function.InferredReturns.DisplayName);
            writer.WriteNumber("blocks", function.ControlFlowGraph.Blocks.Length);
            writer.WriteNumber("iterations", function.FlowIterationCount);
            writer.WriteBoolean("widened", function.WasWidened);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteIrText(LuaIrModule module, StringBuilder output)
    {
        output.Append("format-version: ").AppendLine(module.FormatVersion.ToString(CultureInfo.InvariantCulture));
        output.Append("main-function: ").AppendLine(module.MainFunctionId.ToString(CultureInfo.InvariantCulture));
        foreach (var function in module.Functions.OrderBy(static function => function.Id))
        {
            output.Append("function ").Append(function.Id).Append(" parent=")
                .Append(function.ParentFunctionId).Append(" params=").Append(function.ParameterCount)
                .Append(" vararg=").Append(function.IsVarArg).Append(" registers=")
                .AppendLine(function.RegisterCount.ToString(CultureInfo.InvariantCulture));
            for (var pc = 0; pc < function.Instructions.Length; pc++)
            {
                var instruction = function.Instructions[pc];
                output.Append("  ").Append(pc.ToString("D4", CultureInfo.InvariantCulture)).Append(' ')
                    .Append(instruction.Opcode).Append(" A=").Append(instruction.A)
                    .Append(" B=").Append(instruction.B).Append(" C=").Append(instruction.C)
                    .Append(" D=").Append(instruction.D).Append(" line=")
                    .AppendLine(instruction.SourceLine.ToString(CultureInfo.InvariantCulture));
            }
        }
    }

    private static void WriteIrJson(Utf8JsonWriter writer, LuaIrModule module)
    {
        writer.WriteNumber("formatVersion", module.FormatVersion);
        writer.WriteNumber("mainFunctionId", module.MainFunctionId);
        writer.WriteStartArray("functions");
        foreach (var function in module.Functions.OrderBy(static function => function.Id))
        {
            writer.WriteStartObject();
            writer.WriteNumber("id", function.Id);
            writer.WriteNumber("parentFunctionId", function.ParentFunctionId);
            writer.WriteNumber("parameterCount", function.ParameterCount);
            writer.WriteBoolean("vararg", function.IsVarArg);
            writer.WriteNumber("registerCount", function.RegisterCount);
            writer.WriteStartArray("instructions");
            for (var pc = 0; pc < function.Instructions.Length; pc++)
            {
                var instruction = function.Instructions[pc];
                writer.WriteStartObject();
                writer.WriteNumber("pc", pc);
                writer.WriteString("opcode", instruction.Opcode.ToString());
                writer.WriteNumber("a", instruction.A);
                writer.WriteNumber("b", instruction.B);
                writer.WriteNumber("c", instruction.C);
                writer.WriteNumber("d", instruction.D);
                writer.WriteNumber("sourceLine", instruction.SourceLine);
                writer.WriteEndObject();
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }

    private static void WriteChunkText(
        Lua54Prototype prototype,
        StringBuilder output,
        int depth,
        string name)
    {
        output.Append(' ', depth * 2).Append("prototype ").Append(name)
            .Append(" params=").Append(prototype.ParameterCount)
            .Append(" stack=").Append(prototype.MaximumStackSize)
            .Append(" code=").AppendLine(prototype.Code.Length.ToString(CultureInfo.InvariantCulture));
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var instruction = prototype.Code[pc];
            output.Append(' ', depth * 2 + 2).Append(pc.ToString("D4", CultureInfo.InvariantCulture)).Append(' ')
                .Append(instruction.Opcode).Append(" raw=0x")
                .AppendLine(instruction.RawValue.ToString("x8", CultureInfo.InvariantCulture));
        }

        for (var index = 0; index < prototype.NestedPrototypes.Length; index++)
        {
            WriteChunkText(prototype.NestedPrototypes[index], output, depth + 1, name + "." + index);
        }
    }

    private static void WriteChunkJson(Utf8JsonWriter writer, Lua54Prototype prototype)
    {
        writer.WriteStartObject();
        writer.WriteNumber("parameterCount", prototype.ParameterCount);
        writer.WriteNumber("varargFlags", prototype.VarArgFlags);
        writer.WriteNumber("maximumStackSize", prototype.MaximumStackSize);
        writer.WriteStartArray("code");
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var instruction = prototype.Code[pc];
            writer.WriteStartObject();
            writer.WriteNumber("pc", pc);
            writer.WriteString("opcode", instruction.Opcode.ToString());
            writer.WriteString("raw", $"0x{instruction.RawValue:x8}");
            writer.WriteNumber("a", instruction.A);
            writer.WriteNumber("b", instruction.B);
            writer.WriteNumber("c", instruction.C);
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
        writer.WriteStartArray("prototypes");
        foreach (var nested in prototype.NestedPrototypes)
        {
            WriteChunkJson(writer, nested);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSpan(Utf8JsonWriter writer, int start, int length)
    {
        writer.WriteStartObject("span");
        writer.WriteNumber("start", start);
        writer.WriteNumber("length", length);
        writer.WriteEndObject();
    }

    private static async Task WriteOutputAsync(CliCommandContext context, byte[] payload)
    {
        if (string.IsNullOrWhiteSpace(context.Options.OutputPath) || context.Options.OutputPath == "-")
        {
            await context.StandardOutput.WriteAsync(payload, context.CancellationToken).ConfigureAwait(false);
            return;
        }

        var path = Path.GetFullPath(context.Options.OutputPath, context.CurrentDirectory);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, payload, context.CancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            NotSupportedException or ArgumentException)
        {
            throw new CliInputException($"Cannot write dump output '{path}': {exception.Message}", exception);
        }
    }

    private static Task WriteProblemAsync(
        CliCommandContext context,
        string source,
        string code,
        string message) =>
        CliDiagnosticWriter.WriteAsync(
            context.StandardError,
            [CliDiagnosticWriter.CreateProblem(
                source,
                code,
                DiagnosticSeverity.Error,
                "dump",
                message)],
            context.Options.DiagnosticFormat,
            context.CancellationToken);
}
