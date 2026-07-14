using System.Globalization;
using System.Text;
using Lunil.Analysis;
using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.IR.Lua54;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Stability.Tests;

public sealed class DeterministicFuzzTests
{
    private const ulong SyntaxSeed = 0x54_07_00_01UL;
    private const ulong ChunkSeed = 0x54_07_00_02UL;
    private const ulong AnnotationSeed = 0x54_07_00_03UL;
    private const ulong AnalysisSeed = 0x54_07_00_04UL;
    private const int SyntaxCases = 512;
    private const int ChunkCases = 384;
    private const int AnnotationCases = 512;
    private const int AnalysisCases = 256;

    private const string PucLua548Fixture =
        "G0x1YVQAGZMNChoKBAgIeFYAAAAAAAAAAAAAACh3QAGUQC5jb2RleC9maXh0dXJlLmx1YYCAAAEDhlEAAAADAAAAgYAUgAABAADGAAMBxgABAYEEhmhlbGxvgQEAAICGAQABAAAAgIGIbWVzc2FnZYKGgYVfRU5W";

    [Fact]
    public void SyntaxFuzzIsBoundedDeterministicAndReplayable()
    {
        var random = new StableRandom(SyntaxSeed);
        var lexerOptions = LuaLexerOptions.File with
        {
            MaximumTokenCount = 512,
            MaximumDiagnosticCount = 64,
        };
        var parserOptions = LuaParserOptions.Default with
        {
            MaximumRecursionDepth = 32,
            MaximumNodeCount = 1_024,
            MaximumDiagnosticCount = 64,
        };

        for (var caseIndex = 0; caseIndex < SyntaxCases; caseIndex++)
        {
            var input = random.NextBytes(random.NextInt(257));
            Replay(caseIndex, SyntaxSeed, input, () =>
            {
                var first = Parse(input, lexerOptions, parserOptions);
                var second = Parse(input, lexerOptions, parserOptions);
                Assert.Equal(SyntaxFingerprint(first), SyntaxFingerprint(second));
                Assert.InRange(first.Diagnostics.Length, 0, parserOptions.MaximumDiagnosticCount);
                Assert.InRange(
                    first.Root.DescendantNodes().Count() + 1,
                    1,
                    parserOptions.MaximumNodeCount + 1);
            });
        }
    }

    [Fact]
    public void Lua54ChunkMutationFuzzIsBoundedDeterministicAndReplayable()
    {
        var fixture = Convert.FromBase64String(PucLua548Fixture);
        var random = new StableRandom(ChunkSeed);
        var options = Lua54ChunkReaderOptions.Default with
        {
            MaximumChunkBytes = 4_096,
            MaximumPrototypeDepth = 16,
            MaximumPrototypeCount = 64,
            MaximumInstructionCount = 1_024,
            MaximumConstantCount = 1_024,
            MaximumUpvalueCount = 256,
            MaximumStringBytes = 4_096,
            MaximumDebugEntryCount = 2_048,
        };

        for (var caseIndex = 0; caseIndex < ChunkCases; caseIndex++)
        {
            var input = MutateChunk(fixture, ref random, caseIndex);
            Replay(caseIndex, ChunkSeed, input, () => Assert.Equal(
                ReadChunkFingerprint(input, options),
                ReadChunkFingerprint(input, options)));
        }
    }

    [Fact]
    public void AnnotationFuzzIsBoundedDeterministicAndReplayable()
    {
        var random = new StableRandom(AnnotationSeed);
        var options = new LuaAnnotationOptions
        {
            Dialect = LuaAnnotationDialect.Compatible,
            EnableLegacyFallback = true,
            ReportUnknownTags = true,
            MaximumAnnotationCount = 8,
            MaximumTokensPerAnnotation = 64,
            MaximumTypeDepth = 12,
            MaximumDiagnosticCount = 24,
        };

        for (var caseIndex = 0; caseIndex < AnnotationCases; caseIndex++)
        {
            var payload = random.NextBytes(random.NextInt(193));
            ReplaceLineBreaks(payload);
            byte[] input = [.. "---@type "u8, .. payload, (byte)'\n', .. "local value = nil"u8];
            Replay(caseIndex, AnnotationSeed, input, () =>
            {
                var first = ParseAnnotations(input, options);
                var second = ParseAnnotations(input, options);
                Assert.Equal(AnnotationFingerprint(first), AnnotationFingerprint(second));
                Assert.InRange(first.Annotations.Length, 0, options.MaximumAnnotationCount);
                Assert.InRange(first.Diagnostics.Length, 0, options.MaximumDiagnosticCount);
            });
        }
    }

    [Fact]
    public void TypeAnalysisFuzzIsBoundedDeterministicAndReplayable()
    {
        var random = new StableRandom(AnalysisSeed);
        var options = new LuaAnalysisOptions
        {
            ReportUnknownGlobals = true,
            ReportImplicitAny = true,
            ReportRedundantConditions = true,
            MaximumTypeCount = 256,
            MaximumConstraintCount = 512,
            MaximumControlFlowBlockCount = 256,
            MaximumFlowIterations = 8,
            MaximumUnionMemberCount = 8,
            MaximumTypeDepth = 16,
            MaximumGenericInstantiationCount = 64,
            MaximumReturnPackLength = 16,
            MaximumDiagnosticCount = 48,
        };
        var annotationOptions = new LuaAnnotationOptions
        {
            Dialect = LuaAnnotationDialect.Compatible,
            EnableLegacyFallback = true,
            MaximumAnnotationCount = 16,
            MaximumTokensPerAnnotation = 64,
            MaximumTypeDepth = 16,
            MaximumDiagnosticCount = 24,
        };

        for (var caseIndex = 0; caseIndex < AnalysisCases; caseIndex++)
        {
            var input = CreateAnalysisInput(ref random, caseIndex);
            Replay(caseIndex, AnalysisSeed, input, () =>
            {
                var first = Analyze(input, annotationOptions, options);
                var second = Analyze(input, annotationOptions, options);
                Assert.Equal(AnalysisFingerprint(first), AnalysisFingerprint(second));
                Assert.InRange(first.Diagnostics.Length, 0, options.MaximumDiagnosticCount);
                Assert.InRange(first.BudgetUsage.TypeCount, 0, options.MaximumTypeCount);
                Assert.InRange(first.BudgetUsage.ConstraintCount, 0, options.MaximumConstraintCount);
                Assert.InRange(
                    first.BudgetUsage.ControlFlowBlockCount,
                    0,
                    options.MaximumControlFlowBlockCount + 1);
            });
        }
    }

    private static LuaParseResult Parse(
        byte[] input,
        LuaLexerOptions lexerOptions,
        LuaParserOptions parserOptions) =>
        LuaParser.Parse(new SourceText(input), lexerOptions, parserOptions);

    private static string SyntaxFingerprint(LuaParseResult result)
    {
        var nodes = result.Root.DescendantNodes().Prepend(result.Root)
            .Select(static node => $"{node.Kind}:{node.Span.Start}:{node.Span.Length}");
        var diagnostics = result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}:{diagnostic.Span.Start}:{diagnostic.Span.Length}");
        return string.Join('|', nodes.Concat(diagnostics));
    }

    private static byte[] MutateChunk(
        byte[] fixture,
        ref StableRandom random,
        int caseIndex)
    {
        if (caseIndex == 0)
        {
            return fixture.ToArray();
        }

        byte[] result;
        switch (caseIndex % 4)
        {
            case 0:
                result = fixture[..random.NextInt(fixture.Length + 1)];
                break;
            case 1:
                result = fixture.ToArray();
                for (var mutation = 0; mutation < 1 + random.NextInt(8); mutation++)
                {
                    var index = random.NextInt(result.Length);
                    result[index] ^= (byte)(1 << random.NextInt(8));
                }

                break;
            case 2:
                result = random.NextBytes(random.NextInt(257));
                break;
            default:
                result = [.. fixture, .. random.NextBytes(1 + random.NextInt(32))];
                break;
        }

        return result;
    }

    private static string ReadChunkFingerprint(
        byte[] input,
        Lua54ChunkReaderOptions options)
    {
        try
        {
            var chunk = Lua54ChunkReader.Read(input, options);
            var prototypes = Flatten(chunk.MainPrototype).ToArray();
            var prototypeFingerprint = string.Join(
                ',',
                prototypes.Select(static prototype =>
                    FormattableString.Invariant(
                        $"{prototype.Code.Length}/{prototype.Constants.Length}/{prototype.Upvalues.Length}/{prototype.NestedPrototypes.Length}")));
            return FormattableString.Invariant(
                $"ok:{chunk.Target.ByteOrder}:{chunk.Target.IntegerSize}:{chunk.Target.NumberSize}:{chunk.MainUpvalueCount}:{prototypes.Length}:{prototypeFingerprint}");
        }
        catch (Lua54ChunkFormatException exception)
        {
            return FormattableString.Invariant(
                $"error:{exception.ByteOffset}:{exception.Reason}");
        }
    }

    private static IEnumerable<Lua54Prototype> Flatten(Lua54Prototype prototype)
    {
        yield return prototype;
        foreach (var child in prototype.NestedPrototypes)
        {
            foreach (var descendant in Flatten(child))
            {
                yield return descendant;
            }
        }
    }

    private static LuaAnnotationDocument ParseAnnotations(
        byte[] input,
        LuaAnnotationOptions options)
    {
        var lexing = LuaLexer.Lex(new SourceText(input));
        return LuaAnnotationParser.Parse(lexing, options);
    }

    private static string AnnotationFingerprint(LuaAnnotationDocument document)
    {
        var annotations = document.Annotations.Select(static annotation =>
            $"{annotation.GetType().Name}:{annotation.Span.Start}:{annotation.Span.Length}");
        var diagnostics = document.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}:{diagnostic.Span.Start}:{diagnostic.Span.Length}");
        return $"{document.Dialect}|{string.Join('|', annotations.Concat(diagnostics))}";
    }

    private static byte[] CreateAnalysisInput(ref StableRandom random, int caseIndex)
    {
        var type = (caseIndex % 4) switch
        {
            0 => "integer|string|nil",
            1 => "{kind: 'number', value: integer}|{kind: 'text', value: string}",
            2 => "fun(value: integer): integer",
            _ => "Box<integer>|nil",
        };
        var payload = random.NextBytes(random.NextInt(97));
        ReplaceLineBreaks(payload);
        byte[] prefix = Encoding.UTF8.GetBytes(
            $"---@class Box<T>\n---@field value T\n---@type {type}\n" +
            $"local value = {(caseIndex % 3 == 0 ? "nil" : caseIndex.ToString(CultureInfo.InvariantCulture))}\n" +
            "if value ~= nil then value = value end\n---@type ");
        byte[] suffix = "\nlocal tail = value\nreturn tail"u8.ToArray();
        return [.. prefix, .. payload, .. suffix];
    }

    private static LuaAnalysisResult Analyze(
        byte[] input,
        LuaAnnotationOptions annotationOptions,
        LuaAnalysisOptions analysisOptions)
    {
        var lexing = LuaLexer.Lex(
            new SourceText(input),
            LuaLexerOptions.Default with
            {
                MaximumTokenCount = 1_024,
                MaximumDiagnosticCount = 64,
            });
        var parsing = LuaParser.Parse(
            lexing,
            LuaParserOptions.Default with
            {
                MaximumRecursionDepth = 48,
                MaximumNodeCount = 2_048,
                MaximumDiagnosticCount = 64,
            });
        return LuaTypeAnalyzer.Analyze(
            LuaBinder.Bind(parsing),
            LuaAnnotationParser.Parse(lexing, annotationOptions),
            analysisOptions);
    }

    private static string AnalysisFingerprint(LuaAnalysisResult result)
    {
        var symbols = result.Symbols.Select(static symbol =>
            $"{symbol.Symbol.Name}:{symbol.DeclaredType.DisplayName}:" +
            $"{symbol.InferredType.DisplayName}:{symbol.IsDefinitelyAssigned}");
        var diagnostics = result.Diagnostics.Select(static diagnostic =>
            $"{diagnostic.Code}:{diagnostic.Span.Start}:{diagnostic.Span.Length}");
        var budget = result.BudgetUsage;
        return string.Join('|', symbols.Concat(diagnostics)) +
            FormattableString.Invariant(
                $"|budget:{budget.TypeCount}:{budget.ConstraintCount}:{budget.ControlFlowBlockCount}:{budget.GenericInstantiationCount}:{budget.MaximumObservedTypeDepth}:{budget.WasExceeded}");
    }

    private static void ReplaceLineBreaks(byte[] bytes)
    {
        for (var index = 0; index < bytes.Length; index++)
        {
            if (bytes[index] is (byte)'\r' or (byte)'\n')
            {
                bytes[index] = (byte)'x';
            }
        }
    }

    private static void Replay(int caseIndex, ulong seed, byte[] input, Action action)
    {
        try
        {
            action();
        }
        catch (Exception exception)
        {
            var inputBase64 = Convert.ToBase64String(input);
            throw new InvalidOperationException(
                FormattableString.Invariant(
                    $"Deterministic fuzz failure; seed=0x{seed:X16}; case={caseIndex}; inputBase64={inputBase64}"),
                exception);
        }
    }

    private struct StableRandom(ulong seed)
    {
        private ulong _state = seed;

        public int NextInt(int exclusiveMaximum)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(exclusiveMaximum);
            return (int)(NextUInt64() % (uint)exclusiveMaximum);
        }

        public byte[] NextBytes(int length)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(length);
            var result = new byte[length];
            for (var index = 0; index < result.Length; index++)
            {
                result[index] = (byte)NextUInt64();
            }

            return result;
        }

        private ulong NextUInt64()
        {
            _state += 0x9E3779B97F4A7C15UL;
            var value = _state;
            value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
            value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
            return value ^ (value >> 31);
        }
    }
}
