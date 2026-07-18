using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using Lunil.IR.Canonical;
using Lunil.Runtime.CodeGen;

namespace Lunil.CodeGen.Cil.Jit;

public sealed record LuaJitFunctionProfileEntry(
    int FunctionId,
    LuaJitFunctionProfile Profile);

public sealed record LuaJitModuleProfile(
    string ModuleContentId,
    ImmutableArray<LuaJitFunctionProfileEntry> Functions);

public enum LuaJitProfileImportStatus : byte
{
    Imported,
    Rejected,
    Incompatible,
    Disabled,
}

public sealed record LuaJitProfileImportResult(
    LuaJitProfileImportStatus Status,
    string? DiagnosticCode = null,
    string? Message = null)
{
    public bool Succeeded => Status == LuaJitProfileImportStatus.Imported;
}

public static class LuaJitProfileDiagnosticCodes
{
    public const string Malformed = "JITP1001";
    public const string Incompatible = "JITP1002";
    public const string InvalidProfile = "JITP1003";
}

public static class LuaJitProfileCodec
{
    public const int CurrentSchemaVersion = 2;
    public const int CurrentCodegenVersion = 1;

    private const string Magic = "LUNIL-JIT-PROFILE";
    private const int MaximumProfileBytes = 64 * 1024 * 1024;
    private const int MaximumStringBytes = 4 * 1024;
    private const int MaximumSignaturesPerSite = 64;
    private static ReadOnlySpan<byte> FooterMagic => "LUNILPF1"u8;
    private static readonly LuaJitValueKinds AllValueKinds = Enum
        .GetValues<LuaJitValueKinds>()
        .Aggregate(LuaJitValueKinds.None, static (current, value) => current | value);

    public static byte[] Serialize(
        LuaIrModule module,
        IReadOnlyList<LuaJitFunctionProfile> profiles)
    {
        ArgumentNullException.ThrowIfNull(module);
        ArgumentNullException.ThrowIfNull(profiles);
        if (profiles.Count != module.Functions.Length)
        {
            throw new ArgumentException(
                "Profile count must match the canonical module function count.",
                nameof(profiles));
        }

        var entries = module.Functions
            .Select(function => new LuaJitFunctionProfileEntry(
                function.Id,
                profiles[function.Id]))
            .OrderBy(static entry => entry.FunctionId)
            .ToImmutableArray();
        var profile = new LuaJitModuleProfile(
            LuaJitModuleIdentity.Create(module),
            entries);
        Validate(module, profile);

        using var coreStream = new MemoryStream();
        using (var writer = new BinaryWriter(coreStream, Encoding.UTF8, leaveOpen: true))
        {
            WriteString(writer, Magic);
            writer.Write(CurrentSchemaVersion);
            writer.Write(LuaIrModule.CurrentFormatVersion);
            writer.Write(LuaCodegenAbiV3.RuntimeAbiVersion);
            writer.Write(CurrentCodegenVersion);
            WriteString(writer, profile.ModuleContentId);
            writer.Write(profile.Functions.Length);
            foreach (var function in profile.Functions)
            {
                WriteFunction(writer, function);
            }
        }

        var core = coreStream.ToArray();
        using var output = new MemoryStream(core.Length + FooterMagic.Length + 32);
        output.Write(core);
        output.Write(FooterMagic);
        output.Write(SHA256.HashData(core));
        return output.ToArray();
    }

    public static LuaJitModuleProfile Deserialize(
        LuaIrModule module,
        ReadOnlySpan<byte> payload)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (payload.Length <= FooterMagic.Length + 32 || payload.Length > MaximumProfileBytes)
        {
            throw Malformed("Profile payload size is invalid.");
        }

        var coreLength = payload.Length - FooterMagic.Length - 32;
        var core = payload[..coreLength];
        var footer = payload[coreLength..];
        if (!footer[..FooterMagic.Length].SequenceEqual(FooterMagic) ||
            !CryptographicOperations.FixedTimeEquals(
                footer[FooterMagic.Length..],
                SHA256.HashData(core)))
        {
            throw Malformed("Profile checksum footer is invalid.");
        }

        try
        {
            using var stream = new MemoryStream(core.ToArray(), writable: false);
            using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
            if (!string.Equals(ReadString(reader), Magic, StringComparison.Ordinal))
            {
                throw Malformed("Profile magic is invalid.");
            }

            if (reader.ReadInt32() != CurrentSchemaVersion ||
                reader.ReadInt32() != LuaIrModule.CurrentFormatVersion ||
                reader.ReadInt32() != LuaCodegenAbiV3.RuntimeAbiVersion ||
                reader.ReadInt32() != CurrentCodegenVersion)
            {
                throw Incompatible("Profile schema, IR, Runtime ABI, or codegen version is incompatible.");
            }

            var moduleContentId = ReadString(reader);
            if (!string.Equals(
                moduleContentId,
                LuaJitModuleIdentity.Create(module),
                StringComparison.Ordinal))
            {
                throw Incompatible("Profile module identity does not match the requested module.");
            }

            var functionCount = ReadCount(reader, module.Functions.Length, "function");
            if (functionCount != module.Functions.Length)
            {
                throw Incompatible("Profile function count does not match the canonical module.");
            }

            var functions = ImmutableArray.CreateBuilder<LuaJitFunctionProfileEntry>(functionCount);
            for (var index = 0; index < functionCount; index++)
            {
                functions.Add(ReadFunction(reader));
            }

            if (stream.Position != stream.Length)
            {
                throw Malformed("Profile payload has trailing data.");
            }

            var profile = new LuaJitModuleProfile(
                moduleContentId,
                functions
                    .OrderBy(static entry => entry.FunctionId)
                    .ToImmutableArray());
            Validate(module, profile);
            return profile;
        }
        catch (ProfileFormatException)
        {
            throw;
        }
        catch (Exception error) when (error is EndOfStreamException or IOException or
            ArgumentException or OverflowException)
        {
            throw Malformed("Profile payload is truncated or malformed.", error);
        }
    }

    private static void WriteFunction(BinaryWriter writer, LuaJitFunctionProfileEntry entry)
    {
        writer.Write(entry.FunctionId);
        var profile = entry.Profile;
        writer.Write(profile.Samples);
        writer.Write(profile.ArgumentKinds.Length);
        foreach (var kinds in profile.ArgumentKinds)
        {
            writer.Write((ushort)kinds);
        }

        var sites = profile.Sites
            .OrderBy(static site => site.ProgramCounter)
            .ToArray();
        writer.Write(sites.Length);
        foreach (var site in sites)
        {
            writer.Write(site.ProgramCounter);
            writer.Write((byte)site.Opcode);
            writer.Write(site.Samples);
            writer.Write((ushort)site.FirstOperandKinds);
            writer.Write((ushort)site.SecondOperandKinds);
            writer.Write((ushort)site.ThirdOperandKinds);
            writer.Write(site.CallArgumentKinds.Length);
            foreach (var kinds in site.CallArgumentKinds)
            {
                writer.Write((ushort)kinds);
            }
            writer.Write(site.BranchTaken);
            writer.Write(site.BranchNotTaken);
            writer.Write(site.IsMegamorphic);

            var shapes = site.TableShapes
                .OrderBy(static shape => shape.ArrayCapacity)
                .ThenBy(static shape => shape.ShapeVersion)
                .ThenBy(static shape => shape.MetatableVersion)
                .ThenBy(static shape => shape.KeyKinds)
                .ToArray();
            writer.Write(shapes.Length);
            foreach (var shape in shapes)
            {
                writer.Write((ushort)shape.KeyKinds);
                writer.Write(shape.ArrayCapacity);
                writer.Write(shape.ShapeVersion);
                writer.Write(shape.MetatableVersion);
                writer.Write(shape.HasMetatable);
                writer.Write(shape.Samples);
            }

            var targets = site.CallTargets
                .OrderBy(static target => target.Kind)
                .ThenBy(static target => target.ModuleContentId, StringComparer.Ordinal)
                .ThenBy(static target => target.FunctionId)
                .ThenBy(static target => target.NativeName, StringComparer.Ordinal)
                .ToArray();
            writer.Write(targets.Length);
            foreach (var target in targets)
            {
                writer.Write((byte)target.Kind);
                WriteString(writer, target.ModuleContentId);
                writer.Write(target.FunctionId);
                WriteString(writer, target.NativeName);
                writer.Write(target.Samples);
            }
        }
    }

    private static LuaJitFunctionProfileEntry ReadFunction(BinaryReader reader)
    {
        var functionId = reader.ReadInt32();
        var samples = reader.ReadInt64();
        var argumentCount = ReadCount(reader, 255, "argument");
        var arguments = ImmutableArray.CreateBuilder<LuaJitValueKinds>(argumentCount);
        for (var argument = 0; argument < argumentCount; argument++)
        {
            arguments.Add((LuaJitValueKinds)reader.ReadUInt16());
        }

        var siteCount = ReadCount(reader, 1_000_000, "site");
        var sites = ImmutableArray.CreateBuilder<LuaJitSiteProfile>(siteCount);
        for (var siteIndex = 0; siteIndex < siteCount; siteIndex++)
        {
            var programCounter = reader.ReadInt32();
            var opcode = (LuaIrOpcode)reader.ReadByte();
            var siteSamples = reader.ReadInt64();
            var firstKinds = (LuaJitValueKinds)reader.ReadUInt16();
            var secondKinds = (LuaJitValueKinds)reader.ReadUInt16();
            var thirdKinds = (LuaJitValueKinds)reader.ReadUInt16();
            var callArgumentCount = ReadCount(reader, 255, "call argument");
            var callArguments = ImmutableArray.CreateBuilder<LuaJitValueKinds>(
                callArgumentCount);
            for (var argument = 0; argument < callArgumentCount; argument++)
            {
                callArguments.Add((LuaJitValueKinds)reader.ReadUInt16());
            }
            var branchTaken = reader.ReadInt64();
            var branchNotTaken = reader.ReadInt64();
            var megamorphic = ReadBoolean(reader);
            var shapeCount = ReadCount(reader, MaximumSignaturesPerSite, "table shape");
            var shapes = ImmutableArray.CreateBuilder<LuaJitTableShapeProfile>(shapeCount);
            for (var shapeIndex = 0; shapeIndex < shapeCount; shapeIndex++)
            {
                shapes.Add(new LuaJitTableShapeProfile(
                    (LuaJitValueKinds)reader.ReadUInt16(),
                    reader.ReadInt32(),
                    reader.ReadUInt64(),
                    reader.ReadUInt64(),
                    ReadBoolean(reader),
                    reader.ReadInt64()));
            }

            var targetCount = ReadCount(reader, MaximumSignaturesPerSite, "call target");
            var targets = ImmutableArray.CreateBuilder<LuaJitCallTargetProfile>(targetCount);
            for (var targetIndex = 0; targetIndex < targetCount; targetIndex++)
            {
                targets.Add(new LuaJitCallTargetProfile(
                    (LuaJitCallTargetKind)reader.ReadByte(),
                    ReadString(reader),
                    reader.ReadInt32(),
                    ReadString(reader),
                    reader.ReadInt64()));
            }

            sites.Add(new LuaJitSiteProfile(
                programCounter,
                opcode,
                siteSamples,
                firstKinds,
                secondKinds,
                thirdKinds,
                branchTaken,
                branchNotTaken,
                megamorphic,
                shapes.MoveToImmutable(),
                targets.MoveToImmutable())
            {
                CallArgumentKinds = callArguments.MoveToImmutable(),
            });
        }

        return new LuaJitFunctionProfileEntry(
            functionId,
            new LuaJitFunctionProfile(
                samples,
                arguments.MoveToImmutable(),
                sites.MoveToImmutable()));
    }

    private static void Validate(LuaIrModule module, LuaJitModuleProfile profile)
    {
        if (!IsHash(profile.ModuleContentId) || !string.Equals(
            profile.ModuleContentId,
            LuaJitModuleIdentity.Create(module),
            StringComparison.Ordinal))
        {
            throw Incompatible("Profile module identity is incompatible.");
        }

        if (profile.Functions.Length != module.Functions.Length ||
            profile.Functions.Select(static entry => entry.FunctionId).Distinct().Count() !=
            profile.Functions.Length)
        {
            throw Incompatible("Profile function map is incompatible.");
        }

        foreach (var entry in profile.Functions)
        {
            var function = module.Functions.FirstOrDefault(candidate =>
                candidate.Id == entry.FunctionId) ?? throw Incompatible(
                    "Profile function id is not present in the canonical module.");
            ValidateFunction(function, entry.Profile);
        }
    }

    private static void ValidateFunction(
        LuaIrFunction function,
        LuaJitFunctionProfile profile)
    {
        if (profile.Samples < 0 || profile.ArgumentKinds.Length != function.ParameterCount ||
            profile.ArgumentKinds.Any(static kinds => !AreKindsValid(kinds)) ||
            profile.Sites.Length > function.Instructions.Length)
        {
            throw InvalidProfile("Profile function counters or parameter kinds are invalid.");
        }

        var seenSites = new HashSet<int>();
        long siteSampleTotal = 0;
        foreach (var site in profile.Sites)
        {
            var instruction = (uint)site.ProgramCounter < (uint)function.Instructions.Length
                ? function.Instructions[site.ProgramCounter]
                : default;
            if (site.ProgramCounter < 0 ||
                site.ProgramCounter >= function.Instructions.Length ||
                !seenSites.Add(site.ProgramCounter) ||
                site.Opcode != instruction.Opcode ||
                site.Samples < 0 || site.BranchTaken < 0 || site.BranchNotTaken < 0 ||
                site.BranchTaken > site.Samples || site.BranchNotTaken > site.Samples ||
                !AreKindsValid(site.FirstOperandKinds) ||
                !AreKindsValid(site.SecondOperandKinds) ||
                !AreKindsValid(site.ThirdOperandKinds) ||
                site.CallArgumentKinds.Any(static kinds => !AreKindsValid(kinds)) ||
                (site.Opcode is LuaIrOpcode.Call or LuaIrOpcode.TailCall
                    ? site.CallArgumentKinds.Length != Math.Max(0, instruction.B)
                    : !site.CallArgumentKinds.IsDefaultOrEmpty) ||
                site.BranchTaken > site.Samples - Math.Min(site.BranchNotTaken, site.Samples))
            {
                throw InvalidProfile("Profile site counters or opcode identity are invalid.");
            }

            siteSampleTotal = CheckedProfileAdd(siteSampleTotal, site.Samples);
            ValidateShapes(site);
            ValidateCallTargets(site);
        }

        if (siteSampleTotal != profile.Samples)
        {
            throw InvalidProfile("Profile function sample total does not match its sites.");
        }
    }

    private static void ValidateShapes(LuaJitSiteProfile site)
    {
        if (!site.TableShapes.IsDefaultOrEmpty &&
            site.Opcode is not (LuaIrOpcode.GetTable or LuaIrOpcode.SetTable))
        {
            throw InvalidProfile("Table shape profile is attached to a non-table opcode.");
        }

        var signatures = new HashSet<(LuaJitValueKinds, int, ulong, ulong, bool)>();
        foreach (var shape in site.TableShapes)
        {
            if (!AreKindsValid(shape.KeyKinds) || shape.ArrayCapacity < 0 ||
                shape.Samples <= 0 || shape.Samples > site.Samples ||
                !signatures.Add((
                    shape.KeyKinds,
                    shape.ArrayCapacity,
                    shape.ShapeVersion,
                    shape.MetatableVersion,
                    shape.HasMetatable)))
            {
                throw InvalidProfile("Table shape profile is invalid.");
            }
        }
    }

    private static void ValidateCallTargets(LuaJitSiteProfile site)
    {
        if (!site.CallTargets.IsDefaultOrEmpty &&
            site.Opcode is not (LuaIrOpcode.Call or LuaIrOpcode.TailCall))
        {
            throw InvalidProfile("Call target profile is attached to a non-call opcode.");
        }

        var signatures = new HashSet<(LuaJitCallTargetKind, string, int, string)>();
        foreach (var target in site.CallTargets)
        {
            if (!Enum.IsDefined(target.Kind) || target.Samples <= 0 ||
                target.Samples > site.Samples || target.NativeName.Length > MaximumStringBytes ||
                !signatures.Add((
                    target.Kind,
                    target.ModuleContentId,
                    target.FunctionId,
                    target.NativeName)))
            {
                throw InvalidProfile("Call target profile is invalid.");
            }

            var identityIsValid = target.Kind switch
            {
                LuaJitCallTargetKind.Lua => IsHash(target.ModuleContentId) &&
                    target.FunctionId >= 0 && target.NativeName.Length == 0,
                LuaJitCallTargetKind.Native => target.ModuleContentId.Length == 0 &&
                    target.FunctionId == -1 && !string.IsNullOrWhiteSpace(target.NativeName),
                LuaJitCallTargetKind.Unknown => target.ModuleContentId.Length == 0 &&
                    target.FunctionId == -1 && target.NativeName.Length == 0,
                _ => false,
            };
            if (!identityIsValid)
            {
                throw InvalidProfile("Call target identity is invalid.");
            }
        }
    }

    private static int ReadCount(BinaryReader reader, int maximum, string name)
    {
        var count = reader.ReadInt32();
        return count >= 0 && count <= maximum
            ? count
            : throw Malformed($"Profile {name} count is invalid.");
    }

    private static void WriteString(BinaryWriter writer, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        if (bytes.Length > MaximumStringBytes)
        {
            throw new ArgumentException("Profile string exceeds the format limit.", nameof(value));
        }

        writer.Write(bytes.Length);
        writer.Write(bytes);
    }

    private static string ReadString(BinaryReader reader)
    {
        var length = ReadCount(reader, MaximumStringBytes, "string byte");
        if (length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw Malformed("Profile string is truncated.");
        }

        var bytes = reader.ReadBytes(length);
        return new UTF8Encoding(false, true).GetString(bytes);
    }

    private static bool ReadBoolean(BinaryReader reader) => reader.ReadByte() switch
    {
        0 => false,
        1 => true,
        _ => throw Malformed("Profile Boolean value is invalid."),
    };

    private static long CheckedProfileAdd(long left, long right)
    {
        try
        {
            return checked(left + right);
        }
        catch (OverflowException error)
        {
            throw InvalidProfile("Profile sample counters overflow.", error);
        }
    }

    private static bool AreKindsValid(LuaJitValueKinds kinds) =>
        (kinds & ~AllValueKinds) == 0;

    private static bool IsHash(string value) => value.Length == 64 &&
        value.All(static character =>
            character is (>= '0' and <= '9') or (>= 'a' and <= 'f'));

    private static ProfileFormatException Malformed(string message, Exception? inner = null) =>
        new(LuaJitProfileDiagnosticCodes.Malformed, message, inner);

    private static ProfileFormatException Incompatible(string message, Exception? inner = null) =>
        new(LuaJitProfileDiagnosticCodes.Incompatible, message, inner);

    private static ProfileFormatException InvalidProfile(string message, Exception? inner = null) =>
        new(LuaJitProfileDiagnosticCodes.InvalidProfile, message, inner);

    internal sealed class ProfileFormatException(
        string diagnosticCode,
        string message,
        Exception? innerException = null) : IOException(message, innerException)
    {
        public string DiagnosticCode { get; } = diagnosticCode;
    }
}
