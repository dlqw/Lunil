using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using Lunil.Core;
using Lunil.Core.Text;
using Lunil.Hosting;
using Lunil.IR.Lua54;
using Lunil.Fuzz.Fixture;
using Lunil.Runtime.Values;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

const int defaultIterations = 1_000_000;
const int defaultSeed = 0x13_08_2026;

var options = FuzzOptions.Parse(args, defaultIterations, defaultSeed);
var runner = new ReleaseFuzzRunner(options);
runner.Run();

internal sealed record FuzzOptions(int Iterations, int Seed)
{
    public static FuzzOptions Parse(string[] arguments, int defaultCount, int defaultSeed)
    {
        var iterations = defaultCount;
        var seed = defaultSeed;
        foreach (var argument in arguments)
        {
            if (argument.StartsWith("--iterations=", StringComparison.Ordinal))
            {
                iterations = ParsePositive(argument[13..], "iterations");
            }
            else if (argument.StartsWith("--seed=", StringComparison.Ordinal))
            {
                seed = int.Parse(argument[7..], NumberStyles.Integer, CultureInfo.InvariantCulture);
            }
            else
            {
                throw new ArgumentException($"Unknown argument '{argument}'.");
            }
        }

        return new FuzzOptions(iterations, seed);
    }

    private static int ParsePositive(string value, string name)
    {
        var result = int.Parse(value, NumberStyles.None, CultureInfo.InvariantCulture);
        if (result <= 0)
        {
            throw new ArgumentOutOfRangeException(name, result, "The value must be positive.");
        }

        return result;
    }
}

internal sealed class ReleaseFuzzRunner
{
    private static readonly byte[][] SourceSeeds =
    [
        "return 1"u8.ToArray(),
        "local t={}; for i=1,8 do t[i]=i*i end; return t"u8.ToArray(),
        "local function f(x) if x<=1 then return 1 end return x*f(x-1) end return f(8)"u8.ToArray(),
        "--[=[ long comment ]=]\nlocal s='\\x41\\u{42}'; return #s"u8.ToArray(),
    ];

    private static readonly LuaLexerOptions LexerOptions = new()
    {
        AcceptShebang = true,
        AcceptUtf8ByteOrderMark = true,
        MaximumTokenCount = 256,
        MaximumDiagnosticCount = 64,
    };

    private static readonly LuaParserOptions ParserOptions = new()
    {
        MaximumRecursionDepth = 32,
        MaximumNodeCount = 512,
        MaximumDiagnosticCount = 64,
    };

    private static readonly Lua54ChunkReaderOptions ChunkOptions = new()
    {
        MaximumChunkBytes = 2_048,
        MaximumPrototypeDepth = 8,
        MaximumPrototypeCount = 32,
        MaximumInstructionCount = 256,
        MaximumConstantCount = 256,
        MaximumUpvalueCount = 64,
        MaximumStringBytes = 512,
        MaximumDebugEntryCount = 256,
    };

    private static readonly LuaPatchBundleReadOptions PatchOptions = new()
    {
        MaximumBundleBytes = 4_096,
        MaximumManifestBytes = 2_048,
        MaximumEntryCount = 4,
        MaximumEntryBytes = 1_024,
        MaximumTotalEntryBytes = 2_048,
        MaximumNameBytes = 128,
        MaximumSignatureBytes = 64,
        MaximumCapabilityCount = 8,
        MaximumCapabilityNameBytes = 64,
        MaximumTargetLabelCount = 8,
        MaximumTargetLabelNameBytes = 64,
        MaximumTargetLabelValueBytes = 128,
        UtcNow = new DateTimeOffset(2026, 8, 13, 0, 0, 0, TimeSpan.Zero),
    };

    private readonly FuzzOptions _options;
    private ulong _checksum = 14_695_981_039_346_656_037UL;

    public ReleaseFuzzRunner(FuzzOptions options) => _options = options;

    public void Run()
    {
        var counts = SplitIterations(_options.Iterations);
        var stopwatch = Stopwatch.StartNew();
        var source = FuzzSource(counts[0], CreateRandom(0));
        var chunk = FuzzChunk(counts[1], CreateRandom(1));
        var binding = FuzzBindings(counts[2], CreateRandom(2));
        var patch = FuzzPatches(counts[3], CreateRandom(3));
        stopwatch.Stop();

        var processed = source.Total + chunk.Total + binding.Total + patch.Total;
        if (processed != _options.Iterations)
        {
            throw new InvalidOperationException(
                $"Fuzz corpus accounting mismatch: expected {_options.Iterations}, processed {processed}.");
        }

        Console.WriteLine(
            "LUNIL_RELEASE_FUZZ_RESULT " +
            $"iterations={processed} seed={_options.Seed} " +
            $"source={source.Total} source_accept={source.Accepted} source_reject={source.Rejected} " +
            $"chunk={chunk.Total} chunk_accept={chunk.Accepted} chunk_reject={chunk.Rejected} " +
            $"binding={binding.Total} binding_accept={binding.Accepted} binding_reject={binding.Rejected} " +
            $"patch={patch.Total} patch_accept={patch.Accepted} patch_reject={patch.Rejected} " +
            $"checksum={_checksum:x16} elapsed_ms={(long)stopwatch.Elapsed.TotalMilliseconds}");
    }

    private CorpusResult FuzzSource(int count, Random random)
    {
        var accepted = 0;
        var rejected = 0;
        for (var iteration = 0; iteration < count; iteration++)
        {
            var bytes = CreateSourceInput(random, iteration);
            var lexed = LuaLexer.Lex(new SourceText(bytes), LexerOptions);
            var parsed = LuaParser.Parse(lexed, ParserOptions);
            var diagnostics = lexed.Diagnostics.Length + parsed.Diagnostics.Length;
            if (diagnostics == 0)
            {
                accepted++;
            }
            else
            {
                rejected++;
            }

            Mix((uint)bytes.Length);
            Mix((uint)lexed.Tokens.Length);
            Mix((uint)diagnostics);
            Mix((uint)parsed.Root.Span.Length);
        }

        return new CorpusResult(count, accepted, rejected);
    }

    private CorpusResult FuzzChunk(int count, Random random)
    {
        var seed = CreateChunkSeed();
        var accepted = 0;
        var rejected = 0;
        for (var iteration = 0; iteration < count; iteration++)
        {
            var bytes = MutateBytes(seed, random, iteration, 1_024);
            try
            {
                var chunk = Lua54ChunkReader.Read(bytes, ChunkOptions);
                var canonical = Lua54ChunkWriter.Write(chunk);
                _ = Lua54ChunkReader.Read(canonical, ChunkOptions);
                accepted++;
                Mix((uint)canonical.Length);
                Mix((uint)chunk.MainPrototype.Code.Length);
            }
            catch (Lua54ChunkFormatException exception)
            {
                rejected++;
                Mix((uint)exception.ByteOffset);
            }

            Mix((uint)bytes.Length);
        }

        return new CorpusResult(count, accepted, rejected);
    }

    private CorpusResult FuzzBindings(int count, Random random)
    {
        var registry = CreateBindingRegistry();
        var typeName = typeof(FuzzBindingTarget).FullName!;
        var assemblyName = typeof(FuzzBindingTarget).Assembly.GetName().Name!;
        using var host = new LuaHost(new LuaHostOptions
        {
            InstallStandardLibrary = false,
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.MemberAccess,
                AllowedAssemblyNames = [assemblyName],
                AllowedTypeNames = [typeName],
                AllowedMemberNames =
                [
                    typeName + "." + nameof(FuzzBindingTarget.Add),
                    typeName + "." + nameof(FuzzBindingTarget.Negate),
                    typeName + "." + nameof(FuzzBindingTarget.Echo),
                ],
                BindingRegistry = registry,
                BindingMode = LuaClrBindingMode.RegistryOnly,
            },
        });

        var accepted = 0;
        var rejected = 0;
        for (var iteration = 0; iteration < count; iteration++)
        {
            var mode = random.Next(8);
            var member = mode switch
            {
                0 or 1 or 2 => nameof(FuzzBindingTarget.Add),
                3 => nameof(FuzzBindingTarget.Negate),
                4 => nameof(FuzzBindingTarget.Echo),
                5 => nameof(FuzzBindingTarget.Hidden),
                6 => "missing_" + random.Next(16).ToString(CultureInfo.InvariantCulture),
                _ => nameof(FuzzBindingTarget.Add),
            };
            var arguments = CreateBindingArguments(host, random, mode);
            try
            {
                var result = host.ClrBridge.InvokeStatic(typeName, member, arguments);
                accepted++;
                Mix((uint)result.ReturnValue.Kind);
                if (mode == 0)
                {
                    var expected = checked(arguments[0].AsInteger() + arguments[1].AsInteger());
                    if (result.ReturnValue.AsInteger() != expected)
                    {
                        throw new InvalidOperationException("The generated Add binding returned an invalid result.");
                    }
                }
            }
            catch (LuaClrException exception)
            {
                rejected++;
                Mix((uint)exception.Code);
            }

            Mix((uint)mode);
            Mix((uint)arguments.Length);
        }

        return new CorpusResult(count, accepted, rejected);
    }

    private CorpusResult FuzzPatches(int count, Random random)
    {
        var signature = new DeterministicSignature();
        var seed = CreatePatchSeed(signature);
        var accepted = 0;
        var rejected = 0;
        for (var iteration = 0; iteration < count; iteration++)
        {
            var bytes = MutateBytes(seed, random, iteration, 2_048);
            try
            {
                using var input = new MemoryStream(bytes, writable: false);
                var patch = LuaPatchBundle.Read(input, signature, PatchOptions);
                accepted++;
                Mix((uint)patch.Entries.Length);
                Mix((uint)patch.Manifest.PatchId.Length);
            }
            catch (LuaPatchFormatException exception)
            {
                rejected++;
                Mix((uint)exception.Code);
            }

            Mix((uint)bytes.Length);
        }

        return new CorpusResult(count, accepted, rejected);
    }

    private static byte[] CreateSourceInput(Random random, int iteration)
    {
        if ((iteration & 3) == 0)
        {
            var bytes = new byte[random.Next(129)];
            random.NextBytes(bytes);
            return bytes;
        }

        return MutateBytes(SourceSeeds[random.Next(SourceSeeds.Length)], random, iteration, 256);
    }

    private static byte[] MutateBytes(byte[] seed, Random random, int iteration, int maximumLength)
    {
        switch (iteration & 7)
        {
            case 0:
                return (byte[])seed.Clone();
            case 1:
                {
                    var length = random.Next(seed.Length + 1);
                    return seed.AsSpan(0, length).ToArray();
                }
            case 2:
                {
                    var result = (byte[])seed.Clone();
                    var mutations = 1 + random.Next(Math.Min(8, Math.Max(1, result.Length)));
                    for (var index = 0; index < mutations && result.Length > 0; index++)
                    {
                        result[random.Next(result.Length)] ^= (byte)(1 << random.Next(8));
                    }

                    return result;
                }
            case 3:
                {
                    var result = (byte[])seed.Clone();
                    if (result.Length > 0)
                    {
                        result[random.Next(result.Length)] = (byte)random.Next(256);
                    }

                    return result;
                }
            case 4:
                {
                    var extra = random.Next(1, 33);
                    var length = Math.Min(maximumLength, seed.Length + extra);
                    var result = new byte[length];
                    seed.AsSpan(0, Math.Min(seed.Length, length)).CopyTo(result);
                    random.NextBytes(result.AsSpan(Math.Min(seed.Length, length)));
                    return result;
                }
            case 5:
                {
                    var length = random.Next(Math.Min(maximumLength, 256) + 1);
                    var result = new byte[length];
                    random.NextBytes(result);
                    return result;
                }
            case 6:
                {
                    var result = (byte[])seed.Clone();
                    if (result.Length >= sizeof(int))
                    {
                        var offset = random.Next(result.Length - sizeof(int) + 1);
                        BinaryPrimitives.WriteInt32LittleEndian(
                            result.AsSpan(offset, sizeof(int)),
                            random.Next(2) == 0 ? int.MaxValue : int.MinValue);
                    }

                    return result;
                }
            default:
                {
                    var result = (byte[])seed.Clone();
                    if (result.Length > 1)
                    {
                        var start = random.Next(result.Length);
                        var end = random.Next(start, result.Length);
                        Array.Fill(result, (byte)random.Next(256), start, end - start + 1);
                    }

                    return result;
                }
        }
    }

    private static byte[] CreateChunkSeed()
    {
        var prototype = new Lua54Prototype
        {
            Source = Lua54String.FromUtf8("@fuzz.lua"),
            MaximumStackSize = 2,
            Code = [Lua54Instruction.CreateAbc(Lua54Opcode.ReturnZero, 0, 0, 0)],
            Constants =
            [
                Lua54Constant.FromInteger(13),
                Lua54Constant.FromFloat(0.8),
                Lua54Constant.FromString(Lua54String.FromUtf8("fuzz"), isShort: true),
            ],
            Upvalues = [],
            NestedPrototypes = [],
            LineInfo = [sbyte.MinValue],
            AbsoluteLineInfo = [new Lua54AbsoluteLineInfo(0, 1)],
            LocalVariables = [],
            UpvalueNames = [],
        };
        return Lua54ChunkWriter.Write(new Lua54Chunk(Lua54ChunkTarget.Host, 0, prototype));
    }

    private static LuaClrBindingRegistry CreateBindingRegistry()
    {
        var parameters = new[]
        {
            new LuaClrParameterBinding("left", typeof(long)),
            new LuaClrParameterBinding("right", typeof(long)),
        };
        var members = new[]
        {
            new LuaClrMemberBinding(
                nameof(FuzzBindingTarget.Add), LuaClrMemberKind.Method, true, false, false,
                parameters, typeof(long), static (_, values) =>
                    FuzzBindingTarget.Add((long)values[0]!, (long)values[1]!)),
            new LuaClrMemberBinding(
                nameof(FuzzBindingTarget.Negate), LuaClrMemberKind.Method, true, false, false,
                [new LuaClrParameterBinding("value", typeof(bool))], typeof(bool),
                static (_, values) => FuzzBindingTarget.Negate((bool)values[0]!)),
            new LuaClrMemberBinding(
                nameof(FuzzBindingTarget.Echo), LuaClrMemberKind.Method, true, false, false,
                [new LuaClrParameterBinding("value", typeof(string))], typeof(string),
                static (_, values) => FuzzBindingTarget.Echo((string)values[0]!)),
            new LuaClrMemberBinding(
                nameof(FuzzBindingTarget.Hidden), LuaClrMemberKind.Method, true, false, false,
                [], typeof(long), static (_, _) => FuzzBindingTarget.Hidden()),
        };
        var registry = new LuaClrBindingRegistry();
        registry.Register(new LuaClrTypeBinding(typeof(FuzzBindingTarget), [], members));
        return registry;
    }

    private static LuaValue[] CreateBindingArguments(LuaHost host, Random random, int mode) => mode switch
    {
        0 => [LuaValue.FromInteger(random.Next(-1_000_000, 1_000_001)),
              LuaValue.FromInteger(random.Next(-1_000_000, 1_000_001))],
        1 => [LuaValue.FromInteger(long.MaxValue), LuaValue.FromInteger(random.Next(1, 10))],
        2 => [LuaValue.FromBoolean(random.Next(2) == 0), LuaValue.Nil],
        3 => [LuaValue.FromBoolean(random.Next(2) == 0)],
        4 => [LuaValue.FromString(host.State.Strings.GetOrCreate(
            Encoding.UTF8.GetBytes("fuzz-" + random.Next(1_000).ToString(CultureInfo.InvariantCulture))))],
        5 or 6 => [],
        _ => [LuaValue.FromFloat(double.NaN), LuaValue.FromFloat(double.PositiveInfinity)],
    };

    private static byte[] CreatePatchSeed(DeterministicSignature signature)
    {
        var manifest = new LuaPatchManifest
        {
            PatchId = "fuzz-seed",
            Channel = "alpha",
            TargetBuild = "0.13.0-alpha.7",
            BaseRevision = "1",
            TargetRevision = "2",
            LanguageVersion = LuaLanguageVersion.Lua54,
            RuntimeAbi = "lunil-runtime-v1",
            CreatedAt = new DateTimeOffset(2026, 8, 12, 0, 0, 0, TimeSpan.Zero),
            ExpiresAt = new DateTimeOffset(2026, 8, 14, 0, 0, 0, TimeSpan.Zero),
            Nonce = "fuzz-seed-nonce",
        };
        var bundle = LuaPatchBundle.Create(
            manifest,
            [new LuaPatchEntry("main.lua", "main", LuaPatchEntryKind.Source, "return 13"u8.ToArray())],
            signature);
        using var output = new MemoryStream();
        bundle.Write(output);
        return output.ToArray();
    }

    private Random CreateRandom(int corpus) => new(unchecked(_options.Seed + corpus * 1_000_003));

    private static int[] SplitIterations(int iterations)
    {
        var counts = new int[4];
        for (var index = 0; index < counts.Length; index++)
        {
            counts[index] = iterations / counts.Length + (index < iterations % counts.Length ? 1 : 0);
        }

        return counts;
    }

    private void Mix(uint value)
    {
        _checksum ^= value;
        _checksum *= 1_099_511_628_211UL;
    }

    private readonly record struct CorpusResult(int Total, int Accepted, int Rejected);
}

internal sealed class DeterministicSignature : ILuaPatchSigner, ILuaPatchSignatureVerifier
{
    public string Algorithm => "fuzz-sha256";

    public string KeyId => "release-fuzz";

    public byte[] SignDigest(ReadOnlySpan<byte> digest) => digest.ToArray();

    public bool IsTrusted(string algorithm, string keyId) =>
        string.Equals(algorithm, Algorithm, StringComparison.Ordinal) &&
        string.Equals(keyId, KeyId, StringComparison.Ordinal);

    public bool VerifyDigest(
        string algorithm,
        string keyId,
        ReadOnlySpan<byte> digest,
        ReadOnlySpan<byte> signature) =>
        IsTrusted(algorithm, keyId) && digest.SequenceEqual(signature);
}
