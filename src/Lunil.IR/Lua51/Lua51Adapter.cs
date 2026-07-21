using System.Buffers.Binary;
using System.Collections.Immutable;
using Lunil.Core;
using Lunil.IR.Canonical;
using Lunil.IR.Lua53;

#pragma warning disable CA1720

namespace Lunil.IR.Lua51;

public enum Lua51ByteOrder : byte { LittleEndian, BigEndian }

public readonly record struct Lua51ChunkTarget(
    Lua51ByteOrder ByteOrder, int SizeOfInt, int SizeOfSizeT, int InstructionSize, int NumberSize)
{
    public static Lua51ChunkTarget Host { get; } = new(
        BitConverter.IsLittleEndian ? Lua51ByteOrder.LittleEndian : Lua51ByteOrder.BigEndian,
        4, IntPtr.Size, 4, 8);
}

public sealed record Lua51Chunk(Lua51ChunkTarget Target, Lua51Prototype MainPrototype);

public sealed record Lua51Prototype
{
    public Lua51String? Source { get; init; }
    public int LineDefined { get; init; }
    public int LastLineDefined { get; init; }
    public byte UpvalueCount { get; init; }
    public byte ParameterCount { get; init; }
    public byte VarArgFlags { get; init; }
    public byte MaximumStackSize { get; init; }
    public ImmutableArray<Lua51Instruction> Code { get; init; }
    public ImmutableArray<Lua51Constant> Constants { get; init; }
    public ImmutableArray<Lua51Prototype> NestedPrototypes { get; init; }
    public ImmutableArray<int> LineInfo { get; init; }
    public ImmutableArray<Lua51LocalVariable> LocalVariables { get; init; }
    public ImmutableArray<Lua51String?> UpvalueNames { get; init; }
}

public sealed record Lua51LocalVariable(Lua51String? Name, int StartProgramCounter, int EndProgramCounter);

public readonly record struct Lua51String(byte[] Bytes)
{
    public int Length => Bytes.Length;
    public byte[] ToArray() => [.. Bytes];
    public ReadOnlySpan<byte> AsSpan() => Bytes;
    public override string ToString() => System.Text.Encoding.UTF8.GetString(Bytes);
}

public enum Lua51ConstantKind : byte { Nil, False, True, Number, String }

public sealed record Lua51Constant
{
    public Lua51ConstantKind Kind { get; init; }
    public double NumberValue { get; init; }
    public Lua51String? StringValue { get; init; }
    public static Lua51Constant Nil { get; } = new() { Kind = Lua51ConstantKind.Nil };
    public static Lua51Constant False { get; } = new() { Kind = Lua51ConstantKind.False };
    public static Lua51Constant True { get; } = new() { Kind = Lua51ConstantKind.True };
    public static Lua51Constant FromBoolean(bool value) => value ? True : False;
    public static Lua51Constant FromNumber(double value) => new() { Kind = Lua51ConstantKind.Number, NumberValue = value };
    public static Lua51Constant FromString(Lua51String value) => new() { Kind = Lua51ConstantKind.String, StringValue = value };
}

/// <summary>Lua 5.1's six-bit opcode and 8/9/9 operand encoding.</summary>
public enum Lua51Opcode : byte
{
    Move, LoadConstant, LoadBoolean, LoadNil, GetUpvalue, GetGlobal, GetTable, SetGlobal,
    SetUpvalue, SetTable, NewTable, Self, Add, Subtract, Multiply, Divide, Modulo, Power,
    UnaryMinus, LogicalNot, Length, Concatenate, Jump, Equal, LessThan, LessOrEqual, Test,
    TestSet, Call, TailCall, Return, NumericForLoop, NumericForPrepare, GenericForLoop,
    SetList, Close, Closure, VarArg,
}

public readonly record struct Lua51Instruction(uint RawValue)
{
    public const int MaximumA = Lua51GeneratedInstructionCodec.MaximumA;
    public const int MaximumB = Lua51GeneratedInstructionCodec.MaximumB;
    public const int MaximumC = Lua51GeneratedInstructionCodec.MaximumC;
    public const int MaximumBx = Lua51GeneratedInstructionCodec.MaximumBx;
    public const int SignedBxOffset = Lua51GeneratedInstructionCodec.SignedBxOffset;
    public Lua51Opcode Opcode => Lua51GeneratedInstructionCodec.DecodeOpcode(RawValue);
    public int A => Lua51GeneratedInstructionCodec.DecodeA(RawValue);
    public int C => Lua51GeneratedInstructionCodec.DecodeC(RawValue);
    public int B => Lua51GeneratedInstructionCodec.DecodeB(RawValue);
    public int Bx => Lua51GeneratedInstructionCodec.DecodeBx(RawValue);
    public int SignedBx => Bx - SignedBxOffset;
    public bool IsConstantB => B >= 1 << 8;
    public bool IsConstantC => C >= 1 << 8;
    public static Lua51Instruction CreateAbc(Lua51Opcode opcode, int a, int b, int c) =>
        new(Lua51GeneratedInstructionCodec.EncodeAbc(opcode, Checked(a, MaximumA), Checked(b, MaximumB), Checked(c, MaximumC)));
    public static Lua51Instruction CreateABx(Lua51Opcode opcode, int a, int bx) =>
        new(Lua51GeneratedInstructionCodec.EncodeABx(opcode, Checked(a, MaximumA), Checked(bx, MaximumBx)));
    public static Lua51Instruction CreateASignedBx(Lua51Opcode opcode, int a, int sbx) =>
        CreateABx(opcode, a, checked(sbx + SignedBxOffset));
    private static int Checked(int value, int maximum) => (uint)value <= (uint)maximum ? value :
        throw new ArgumentOutOfRangeException(nameof(value));
}

public sealed record Lua51ChunkReaderOptions
{
    public static Lua51ChunkReaderOptions Default { get; } = new();
    public int MaximumChunkBytes { get; init; } = 64 * 1024 * 1024;
    public int MaximumPrototypeDepth { get; init; } = 128;
    public int MaximumPrototypeCount { get; init; } = 100_000;
    public int MaximumInstructionCount { get; init; } = 4_000_000;
    public int MaximumConstantCount { get; init; } = 1_000_000;
    public int MaximumStringBytes { get; init; } = 16 * 1024 * 1024;
    public int MaximumDebugEntryCount { get; init; } = 2_000_000;
    public bool AllowTrailingData { get; init; }
}

public sealed class Lua51ChunkFormatException(string reason, int offset = 0)
    : FormatException($"Bad Lua 5.1 binary chunk at byte {offset}: {reason}")
{
    public string Reason { get; } = reason;
    public int Offset { get; } = offset;
}

public static class Lua51ChunkReader
{
    public static Lua51Chunk Read(ReadOnlySpan<byte> data, Lua51ChunkReaderOptions? options = null)
    {
        options ??= Lua51ChunkReaderOptions.Default;
        if (data.Length > options.MaximumChunkBytes)
            throw new Lua51ChunkFormatException("chunk exceeds configured size limit");
        var reader = new Reader(data, options);
        return reader.ReadChunk();
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private readonly Lua51ChunkReaderOptions _options;
        private int _offset, _prototypes, _instructions, _constants, _strings, _debug;
        private Lua51ChunkTarget _target;
        public Reader(ReadOnlySpan<byte> data, Lua51ChunkReaderOptions options) { _data = data; _options = options; }
        public Lua51Chunk ReadChunk()
        {
            Expect([0x1b, (byte)'L', (byte)'u', (byte)'a']); ExpectByte(0x51); ExpectByte(0);
            var endian = ReadByte();
            if (endian is not (0 or 1)) Fail("invalid endianness marker");
            var si = ReadByte(); var ss = ReadByte(); var inst = ReadByte(); var num = ReadByte(); var integral = ReadByte();
            if (si != 4 || ss is not (4 or 8) || inst != 4 || num is not (4 or 8) || integral != 0)
                Fail("unsupported scalar layout");
            _target = new(endian == 1 ? Lua51ByteOrder.LittleEndian : Lua51ByteOrder.BigEndian, si, ss, inst, num);
            var main = ReadPrototype(null, 1);
            if (!_options.AllowTrailingData && _offset != _data.Length) Fail("trailing data after main prototype");
            return new(_target, main);
        }
        private Lua51Prototype ReadPrototype(Lua51String? parentSource, int depth)
        {
            if (depth > _options.MaximumPrototypeDepth) Fail("prototype nesting exceeds limit");
            Add(ref _prototypes, 1, _options.MaximumPrototypeCount, "prototype count");
            var source = ReadString() ?? parentSource;
            var ld = ReadInt(); var lld = ReadInt(); var nups = ReadByte(); var pars = ReadByte();
            var vararg = ReadByte(); var stack = ReadByte();
            var codeCount = ReadInt(); Add(ref _instructions, codeCount, _options.MaximumInstructionCount, "instruction count");
            Ensure(codeCount, 4); var code = ImmutableArray.CreateBuilder<Lua51Instruction>(codeCount);
            for (var i = 0; i < codeCount; i++) code.Add(new(ReadUInt()));
            var constantCount = ReadInt(); Add(ref _constants, constantCount, _options.MaximumConstantCount, "constant count"); Ensure(constantCount, 1);
            var constants = ImmutableArray.CreateBuilder<Lua51Constant>(constantCount);
            for (var i = 0; i < constantCount; i++) constants.Add(ReadConstant());
            var nestedCount = ReadInt(); Ensure(nestedCount, 2); var nested = ImmutableArray.CreateBuilder<Lua51Prototype>(nestedCount);
            for (var i = 0; i < nestedCount; i++) nested.Add(ReadPrototype(source, depth + 1));
            var lineCount = ReadInt(); Add(ref _debug, lineCount, _options.MaximumDebugEntryCount, "debug entry count"); Ensure(lineCount, 4);
            var lines = ImmutableArray.CreateBuilder<int>(lineCount); for (var i = 0; i < lineCount; i++) lines.Add(ReadSignedInt());
            var localCount = ReadInt(); Add(ref _debug, localCount, _options.MaximumDebugEntryCount, "debug entry count"); Ensure(localCount, 1);
            var locals = ImmutableArray.CreateBuilder<Lua51LocalVariable>(localCount);
            for (var i = 0; i < localCount; i++) locals.Add(new(ReadString(), ReadInt(), ReadInt()));
            var upvalueNameCount = ReadInt(); Add(ref _debug, upvalueNameCount, _options.MaximumDebugEntryCount, "debug entry count"); Ensure(upvalueNameCount, 1);
            var names = ImmutableArray.CreateBuilder<Lua51String?>(upvalueNameCount); for (var i = 0; i < upvalueNameCount; i++) names.Add(ReadString());
            return new()
            {
                Source = source,
                LineDefined = ld,
                LastLineDefined = lld,
                UpvalueCount = nups,
                ParameterCount = pars,
                VarArgFlags = vararg,
                MaximumStackSize = stack,
                Code = code.MoveToImmutable(),
                Constants = constants.MoveToImmutable(),
                NestedPrototypes = nested.MoveToImmutable(),
                LineInfo = lines.MoveToImmutable(),
                LocalVariables = locals.MoveToImmutable(),
                UpvalueNames = names.MoveToImmutable()
            };
        }
        private Lua51Constant ReadConstant() => ReadByte() switch
        {
            0 => Lua51Constant.Nil,
            1 => Lua51Constant.FromBoolean(ReadByte() != 0),
            3 => Lua51Constant.FromNumber(ReadNumber()),
            4 => Lua51Constant.FromString(ReadString() ?? Fail<Lua51String>("null string constant")),
            var tag => Fail<Lua51Constant>($"unknown constant tag {tag}"),
        };
        private Lua51String? ReadString()
        {
            var size = ReadSizeT(); if (size == 0) return null; if (size > int.MaxValue) Fail("string too large");
            var bytes = ReadBytes((int)size); Add(ref _strings, checked((int)size - 1), _options.MaximumStringBytes, "string bytes");
            return new(bytes[..^1].ToArray());
        }
        private int ReadInt() { var value = ReadSignedInt(); if (value < 0) Fail("negative integer"); return value; }
        private int ReadSignedInt() => _target.ByteOrder == Lua51ByteOrder.LittleEndian ? BinaryPrimitives.ReadInt32LittleEndian(ReadBytes(4)) : BinaryPrimitives.ReadInt32BigEndian(ReadBytes(4));
        private uint ReadUInt() => _target.ByteOrder == Lua51ByteOrder.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4)) : BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));
        private ulong ReadSizeT() => _target.SizeOfSizeT == 4 ? (_target.ByteOrder == Lua51ByteOrder.LittleEndian ? BinaryPrimitives.ReadUInt32LittleEndian(ReadBytes(4)) : BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4))) : (_target.ByteOrder == Lua51ByteOrder.LittleEndian ? BinaryPrimitives.ReadUInt64LittleEndian(ReadBytes(8)) : BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8)));
        private double ReadNumber() { var bytes = ReadBytes(_target.NumberSize); return bytes.Length == 4 ? (_target.ByteOrder == Lua51ByteOrder.LittleEndian ? BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(bytes)) : BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32BigEndian(bytes))) : (_target.ByteOrder == Lua51ByteOrder.LittleEndian ? BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64LittleEndian(bytes)) : BitConverter.Int64BitsToDouble(BinaryPrimitives.ReadInt64BigEndian(bytes))); }
        private byte ReadByte() { if ((uint)_offset >= (uint)_data.Length) Fail("unexpected end of chunk"); return _data[_offset++]; }
        private ReadOnlySpan<byte> ReadBytes(int count) { if (count < 0 || count > _data.Length - _offset) Fail("truncated chunk"); var result = _data.Slice(_offset, count); _offset += count; return result; }
        private void Expect(ReadOnlySpan<byte> value) { if (!ReadBytes(value.Length).SequenceEqual(value)) Fail("not a binary chunk"); }
        private void ExpectByte(byte value) { if (ReadByte() != value) Fail("version or format mismatch"); }
        private void Ensure(int count, int minimum) { if (count < 0 || count > (_data.Length - _offset) / minimum) Fail("invalid count"); }
        private static void Add(ref int current, int amount, int max, string name) { if (amount < 0 || current > max - amount) throw new Lua51ChunkFormatException($"{name} exceeds configured limit"); current += amount; }
        private void Fail(string reason) => throw new Lua51ChunkFormatException(reason, _offset);
        private T Fail<T>(string reason) => throw new Lua51ChunkFormatException(reason, _offset);
    }
}

public static class Lua51PrototypeConverter
{
    public static LuaIrModule Convert(ReadOnlySpan<byte> bytes, Lua51ChunkReaderOptions? options = null) => Convert(Lua51ChunkReader.Read(bytes, options));
    public static LuaIrModule Convert(Lua51Chunk chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        var environmentRequirements = new Dictionary<Lua51Prototype, bool>(
            ReferenceEqualityComparer.Instance);
        AnalyzeEnvironmentRequirements(chunk.MainPrototype, environmentRequirements);
        var main = Translate(chunk.MainPrototype, default, environmentRequirements);
        return Lua53PrototypeConverter.Convert(new Lua53Chunk(
            new Lua53ChunkTarget(chunk.Target.ByteOrder == Lua51ByteOrder.LittleEndian ? Lua53ByteOrder.LittleEndian : Lua53ByteOrder.BigEndian,
                4, (byte)chunk.Target.SizeOfSizeT, 4, 8, (byte)chunk.Target.NumberSize),
            checked((byte)main.Upvalues.Length),
            main), LuaLanguageVersion.Lua51);
    }

    private static Lua53Prototype Translate(
        Lua51Prototype p,
        ImmutableArray<Lua53UpvalueDescriptor> upvalues,
        IReadOnlyDictionary<Lua51Prototype, bool> environmentRequirements)
    {
        var hasEnvironment = environmentRequirements[p];
        var upvalueOffset = hasEnvironment ? 1 : 0;
        if (upvalues.IsDefault)
        {
            var rootUpvalues = ImmutableArray.CreateBuilder<Lua53UpvalueDescriptor>(
                checked(p.UpvalueCount + upvalueOffset));
            if (hasEnvironment)
            {
                rootUpvalues.Add(new Lua53UpvalueDescriptor(1, 0));
            }

            rootUpvalues.AddRange(Enumerable.Range(0, p.UpvalueCount)
                .Select(_ => new Lua53UpvalueDescriptor(1, 0)));
            upvalues = rootUpvalues.MoveToImmutable();
        }
        var nestedUpvalues = new ImmutableArray<Lua53UpvalueDescriptor>[p.NestedPrototypes.Length];
        var skipped = new bool[p.Code.Length];
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            var instruction = p.Code[pc];
            if (instruction.Opcode != Lua51Opcode.Closure || instruction.Bx >= nestedUpvalues.Length)
                continue;
            var nested = p.NestedPrototypes[instruction.Bx];
            var count = nested.UpvalueCount;
            var nestedHasEnvironment = environmentRequirements[nested];
            var descriptors = ImmutableArray.CreateBuilder<Lua53UpvalueDescriptor>(
                checked(count + (nestedHasEnvironment ? 1 : 0)));
            if (nestedHasEnvironment)
            {
                // GETGLOBAL and SETGLOBAL refer to the function environment implicitly in
                // Lua 5.1. Keep it at canonical upvalue index zero for closures that need it.
                descriptors.Add(new Lua53UpvalueDescriptor(0, 0));
            }

            for (var index = 0; index < count; index++)
            {
                var bindingPc = pc + index + 1;
                if (bindingPc >= p.Code.Length)
                    throw new InvalidDataException("Lua 5.1 closure upvalue bindings are truncated.");
                var binding = p.Code[bindingPc];
                descriptors.Add(binding.Opcode switch
                {
                    Lua51Opcode.Move => new Lua53UpvalueDescriptor(1, checked((byte)binding.B)),
                    Lua51Opcode.GetUpvalue => new Lua53UpvalueDescriptor(
                        0,
                        checked((byte)(binding.B + upvalueOffset))),
                    _ => throw new InvalidDataException("Lua 5.1 closure has an invalid upvalue binding instruction."),
                });
                skipped[bindingPc] = true;
            }
            nestedUpvalues[instruction.Bx] = descriptors.MoveToImmutable();
        }

        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            if (skipped[pc] || p.Code[pc].Opcode != Lua51Opcode.GenericForLoop)
            {
                continue;
            }

            var jumpPc = pc + 1;
            if (jumpPc >= p.Code.Length || p.Code[jumpPc].Opcode != Lua51Opcode.Jump)
            {
                throw new InvalidDataException(
                    "Lua 5.1 TFORLOOP must be followed by its control-flow jump.");
            }

            skipped[jumpPc] = true;
        }

        var pcMap = new int[p.Code.Length + 1];
        var translatedPc = 0;
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            pcMap[pc] = translatedPc;
            if (!skipped[pc])
            {
                translatedPc += p.Code[pc].Opcode == Lua51Opcode.GenericForLoop ? 2 : 1;
            }
        }
        pcMap[^1] = translatedPc;
        var code = ImmutableArray.CreateBuilder<Lua53Instruction>(translatedPc);
        for (var pc = 0; pc < p.Code.Length; pc++)
        {
            if (skipped[pc]) continue;
            var instruction = p.Code[pc];
            if (instruction.Opcode == Lua51Opcode.GenericForLoop)
            {
                var jump = p.Code[pc + 1];
                var target = pc + 2 + jump.SignedBx;
                if ((uint)target > (uint)p.Code.Length)
                {
                    throw new InvalidDataException("Lua 5.1 generic-for target is outside the prototype.");
                }

                code.Add(Lua53Instruction.CreateAbc(
                    Lua53Opcode.GenericForCall,
                    instruction.A,
                    0,
                    instruction.C));
                code.Add(Lua53Instruction.CreateASignedBx(
                    Lua53Opcode.GenericForLoop,
                    checked(instruction.A + 2),
                    pcMap[target] - (pcMap[pc] + 2)));
            }
            else if (instruction.Opcode is Lua51Opcode.Jump or Lua51Opcode.NumericForLoop or Lua51Opcode.NumericForPrepare)
            {
                var target = pc + 1 + instruction.SignedBx;
                if ((uint)target > (uint)p.Code.Length)
                    throw new InvalidDataException("Lua 5.1 jump target is outside the prototype.");
                var mapped = pcMap[target] - (pcMap[pc] + 1);
                code.Add(Lua53Instruction.CreateASignedBx(
                    instruction.Opcode == Lua51Opcode.Jump ? Lua53Opcode.Jump :
                        instruction.Opcode == Lua51Opcode.NumericForLoop ? Lua53Opcode.NumericForLoop : Lua53Opcode.NumericForPrepare,
                    instruction.A, mapped));
            }
            else code.Add(Translate(instruction, upvalueOffset));
        }

        return new Lua53Prototype
        {
            Source = p.Source is { } s ? new Lua53String(s.ToArray()) : null,
            LineDefined = p.LineDefined,
            LastLineDefined = p.LastLineDefined,
            ParameterCount = p.ParameterCount,
            VarArgFlags = (byte)((p.VarArgFlags & 2) != 0 ? 1 : 0),
            MaximumStackSize = p.MaximumStackSize,
            Code = code.MoveToImmutable(),
            Constants = p.Constants.Select(Translate).ToImmutableArray(),
            NestedPrototypes = p.NestedPrototypes.Select((nested, index) => Translate(
                nested,
                nestedUpvalues[index],
                environmentRequirements)).ToImmutableArray(),
            LineInfo = TranslateLineInfo(p, skipped),
            LocalVariables = p.LocalVariables.Select(x => new Lua53LocalVariable(
                x.Name is { } n ? new Lua53String(n.ToArray()) : null,
                pcMap[x.StartProgramCounter],
                pcMap[x.EndProgramCounter])).ToImmutableArray(),
            UpvalueNames = TranslateUpvalueNames(
                p.UpvalueNames,
                upvalues.Length,
                hasEnvironment),
            Upvalues = upvalues,
        };
    }

    private static ImmutableArray<Lua53String?> TranslateUpvalueNames(
        ImmutableArray<Lua51String?> names,
        int upvalueCount,
        bool hasEnvironment)
    {
        var result = ImmutableArray.CreateBuilder<Lua53String?>(upvalueCount);
        if (hasEnvironment)
        {
            result.Add(new Lua53String("_ENV"u8.ToArray()));
        }
        foreach (var name in names)
        {
            result.Add(name is { } value ? new Lua53String(value.ToArray()) : null);
        }

        while (result.Count < upvalueCount)
        {
            result.Add(null);
        }

        return result.MoveToImmutable();
    }

    private static bool AnalyzeEnvironmentRequirements(
        Lua51Prototype prototype,
        IDictionary<Lua51Prototype, bool> requirements)
    {
        if (requirements.TryGetValue(prototype, out var existing))
        {
            return existing;
        }

        var required = prototype.Code.Any(static instruction =>
            instruction.Opcode is Lua51Opcode.GetGlobal or Lua51Opcode.SetGlobal);
        foreach (var nested in prototype.NestedPrototypes)
        {
            required |= AnalyzeEnvironmentRequirements(nested, requirements);
        }

        requirements[prototype] = required;
        return required;
    }

    private static ImmutableArray<int> TranslateLineInfo(Lua51Prototype prototype, bool[] skipped)
    {
        if (prototype.LineInfo.IsEmpty)
        {
            return [];
        }

        var lines = ImmutableArray.CreateBuilder<int>();
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            if (skipped[pc])
            {
                continue;
            }

            lines.Add(prototype.LineInfo[pc]);
            if (prototype.Code[pc].Opcode == Lua51Opcode.GenericForLoop)
            {
                lines.Add(prototype.LineInfo[pc]);
            }
        }

        return lines.ToImmutable();
    }

    private static Lua53Instruction Translate(Lua51Instruction i, int upvalueOffset)
    {
        if (i.Opcode == Lua51Opcode.Close)
        {
            return Lua53Instruction.CreateASignedBx(
                Lua53Opcode.Jump,
                checked(i.A + 1),
                0);
        }

        var op = i.Opcode switch
        {
            Lua51Opcode.Move => Lua53Opcode.Move,
            Lua51Opcode.LoadConstant => Lua53Opcode.LoadConstant,
            Lua51Opcode.LoadBoolean => Lua53Opcode.LoadBoolean,
            Lua51Opcode.LoadNil => Lua53Opcode.LoadNil,
            Lua51Opcode.GetUpvalue => Lua53Opcode.GetUpvalue,
            Lua51Opcode.GetGlobal => Lua53Opcode.GetGlobal,
            Lua51Opcode.GetTable => Lua53Opcode.GetTable,
            Lua51Opcode.SetGlobal => Lua53Opcode.SetTableUpvalue,
            Lua51Opcode.SetUpvalue => Lua53Opcode.SetUpvalue,
            Lua51Opcode.SetTable => Lua53Opcode.SetTable,
            Lua51Opcode.NewTable => Lua53Opcode.NewTable,
            Lua51Opcode.Self => Lua53Opcode.Self,
            Lua51Opcode.Add => Lua53Opcode.Add,
            Lua51Opcode.Subtract => Lua53Opcode.Subtract,
            Lua51Opcode.Multiply => Lua53Opcode.Multiply,
            Lua51Opcode.Divide => Lua53Opcode.Divide,
            Lua51Opcode.Modulo => Lua53Opcode.Modulo,
            Lua51Opcode.Power => Lua53Opcode.Power,
            Lua51Opcode.UnaryMinus => Lua53Opcode.UnaryMinus,
            Lua51Opcode.LogicalNot => Lua53Opcode.LogicalNot,
            Lua51Opcode.Length => Lua53Opcode.Length,
            Lua51Opcode.Concatenate => Lua53Opcode.Concatenate,
            Lua51Opcode.Jump => Lua53Opcode.Jump,
            Lua51Opcode.Equal => Lua53Opcode.Equal,
            Lua51Opcode.LessThan => Lua53Opcode.LessThan,
            Lua51Opcode.LessOrEqual => Lua53Opcode.LessOrEqual,
            Lua51Opcode.Test => Lua53Opcode.Test,
            Lua51Opcode.TestSet => Lua53Opcode.TestSet,
            Lua51Opcode.Call => Lua53Opcode.Call,
            Lua51Opcode.TailCall => Lua53Opcode.TailCall,
            Lua51Opcode.Return => Lua53Opcode.Return,
            Lua51Opcode.NumericForLoop => Lua53Opcode.NumericForLoop,
            Lua51Opcode.NumericForPrepare => Lua53Opcode.NumericForPrepare,
            Lua51Opcode.GenericForLoop => Lua53Opcode.GenericForLoop,
            Lua51Opcode.SetList => Lua53Opcode.SetList,
            Lua51Opcode.Closure => Lua53Opcode.Closure,
            Lua51Opcode.VarArg => Lua53Opcode.VarArg,
            _ => throw new InvalidDataException($"Unsupported Lua 5.1 opcode {i.Opcode}"),
        };
        if (i.Opcode is Lua51Opcode.GetGlobal) return Lua53Instruction.CreateAbc(op, i.A, 0, i.Bx | (1 << 8));
        if (i.Opcode is Lua51Opcode.SetGlobal) return Lua53Instruction.CreateAbc(op, 0, i.Bx | (1 << 8), i.A);
        if (i.Opcode is Lua51Opcode.GetUpvalue or Lua51Opcode.SetUpvalue)
            return Lua53Instruction.CreateAbc(op, i.A, checked(i.B + upvalueOffset), i.C);
        return i.Opcode is Lua51Opcode.LoadConstant or Lua51Opcode.Closure ? Lua53Instruction.CreateABx(op, i.A, i.Bx) :
            i.Opcode is Lua51Opcode.Jump or Lua51Opcode.NumericForLoop or Lua51Opcode.NumericForPrepare ? Lua53Instruction.CreateASignedBx(op, i.A, i.SignedBx) :
            Lua53Instruction.CreateAbc(op, i.A, i.B, i.C);
    }
    private static Lua53Constant Translate(Lua51Constant c) => c.Kind switch
    {
        Lua51ConstantKind.Nil => Lua53Constant.Nil,
        Lua51ConstantKind.False => Lua53Constant.False,
        Lua51ConstantKind.True => Lua53Constant.True,
        Lua51ConstantKind.Number => Lua53Constant.FromFloat(c.NumberValue),
        Lua51ConstantKind.String => Lua53Constant.FromString(new Lua53String(c.StringValue!.Value.ToArray()), c.StringValue.Value.Length <= 40),
        _ => throw new InvalidDataException("Unknown Lua 5.1 constant kind"),
    };
}

public static class Lua51CanonicalPrototypeWriter
{
    public static byte[] Write(LuaIrModule module, int functionId, bool stripDebug = false)
    {
        var chunk = CreateChunk(module, functionId, stripDebug);
        var bytes = new List<byte>(4096); bytes.AddRange([0x1b, (byte)'L', (byte)'u', (byte)'a', 0x51, 0, 1, 4, 8, 4, 8, 0]);
        WritePrototype(bytes, chunk.MainPrototype, stripDebug); return [.. bytes];
    }
    public static Lua51Chunk CreateChunk(LuaIrModule module, int functionId, bool stripDebug = false)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (module.LanguageVersion != LuaLanguageVersion.Lua51) throw new InvalidDataException("Lua 5.1 writer requires a Lua 5.1 module.");
        var source = Lua53CanonicalPrototypeWriter.CreateChunk(module with { LanguageVersion = LuaLanguageVersion.Lua53 }, functionId);
        var environmentIndexes = FindEnvironmentUpvalueIndexes(module);
        return new(Lua51ChunkTarget.Host, Translate(
            source.MainPrototype,
            module,
            functionId,
            environmentIndexes,
            stripDebug));
    }

    private static Lua51Prototype Translate(
        Lua53Prototype p,
        LuaIrModule module,
        int functionId,
        IReadOnlyDictionary<int, int?> environmentIndexes,
        bool stripDebug)
    {
        var function = module.Functions[functionId];
        if (p.Upvalues.Length != function.Upvalues.Length)
        {
            throw new InvalidDataException(
                $"Lua 5.1 prototype {functionId} does not match its canonical upvalues.");
        }

        var childIds = module.Functions
            .Where(candidate => candidate.ParentFunctionId == functionId)
            .Select(static candidate => candidate.Id)
            .ToImmutableArray();
        if (p.NestedPrototypes.Length != childIds.Length)
        {
            throw new InvalidDataException(
                $"Lua 5.1 prototype {functionId} does not match its canonical children.");
        }

        var environmentIndex = environmentIndexes[functionId];
        return new Lua51Prototype
        {
            Source = p.Source is { } s ? new Lua51String(s.ToArray()) : null,
            LineDefined = p.LineDefined,
            LastLineDefined = p.LastLineDefined,
            UpvalueCount = checked((byte)(p.Upvalues.Length - (environmentIndex.HasValue ? 1 : 0))),
            ParameterCount = p.ParameterCount,
            VarArgFlags = p.VarArgFlags == 0 ? (byte)0 : (byte)2,
            MaximumStackSize = p.MaximumStackSize,
            Code = TranslateCode(p, environmentIndex, childIds, environmentIndexes),
            Constants = p.Constants.Select(Translate).ToImmutableArray(),
            NestedPrototypes = p.NestedPrototypes.Select((nested, index) => Translate(
                nested,
                module,
                childIds[index],
                environmentIndexes,
                stripDebug)).ToImmutableArray(),
            LineInfo = TranslateLineInfo(p, childIds, environmentIndexes, stripDebug),
            LocalVariables = stripDebug ? [] : p.LocalVariables.Select(x => new Lua51LocalVariable(x.Name is { } n ? new Lua51String(n.ToArray()) : null, x.StartProgramCounter, x.EndProgramCounter)).ToImmutableArray(),
            UpvalueNames = TranslateUpvalueNames(p, environmentIndex, stripDebug),
        };
    }

    private static Dictionary<int, int?> FindEnvironmentUpvalueIndexes(LuaIrModule module)
    {
        var indexes = new Dictionary<int, int?>();
        int? Resolve(int functionId)
        {
            if (indexes.TryGetValue(functionId, out var existing))
            {
                return existing;
            }

            var function = module.Functions[functionId];
            int? parentEnvironmentIndex = null;
            if (function.ParentFunctionId >= 0)
            {
                parentEnvironmentIndex = Resolve(function.ParentFunctionId);
            }

            var candidates = function.Upvalues
                .Select(static (upvalue, index) => (upvalue, index))
                .Where(pair => function.ParentFunctionId < 0
                    ? pair.upvalue.SourceKind == LuaIrUpvalueSourceKind.Environment
                    : parentEnvironmentIndex.HasValue &&
                        pair.upvalue.SourceKind == LuaIrUpvalueSourceKind.Upvalue &&
                        pair.upvalue.SourceIndex == parentEnvironmentIndex.Value &&
                        string.Equals(pair.upvalue.Name, "_ENV", StringComparison.Ordinal))
                .Select(static pair => pair.index)
                .ToArray();
            if (candidates.Length > 1)
            {
                throw new InvalidDataException(
                    $"Lua 5.1 function {functionId} has multiple canonical environment upvalues.");
            }

            var result = candidates.Length == 0 ? (int?)null : candidates[0];
            indexes.Add(functionId, result);
            return result;
        }

        foreach (var function in module.Functions)
        {
            Resolve(function.Id);
        }

        return indexes;
    }

    private static ImmutableArray<Lua51String?> TranslateUpvalueNames(
        Lua53Prototype prototype,
        int? environmentIndex,
        bool stripDebug)
    {
        if (stripDebug || prototype.UpvalueNames.IsEmpty)
        {
            return [];
        }

        return prototype.UpvalueNames
            .Where((_, index) => index != environmentIndex)
            .Select(static name => name is { } value
                ? (Lua51String?)new Lua51String(value.ToArray())
                : null)
            .ToImmutableArray();
    }

    private static ImmutableArray<int> TranslateLineInfo(
        Lua53Prototype prototype,
        ImmutableArray<int> childIds,
        IReadOnlyDictionary<int, int?> environmentIndexes,
        bool stripDebug)
    {
        if (stripDebug || prototype.LineInfo.IsEmpty) return [];
        var lines = ImmutableArray.CreateBuilder<int>();
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var line = prototype.LineInfo[pc];
            lines.Add(line);
            var instruction = prototype.Code[pc];
            if (instruction.Opcode == Lua53Opcode.Jump && instruction.A > 0)
            {
                lines.Add(line);
            }
            if (instruction.Opcode != Lua53Opcode.Closure) continue;
            var childEnvironmentIndex = environmentIndexes[childIds[instruction.Bx]];
            var bindingCount = prototype.NestedPrototypes[instruction.Bx].Upvalues.Length -
                (childEnvironmentIndex.HasValue ? 1 : 0);
            for (var index = 0; index < bindingCount; index++)
                lines.Add(line);
        }
        return lines.ToImmutable();
    }
    private static ImmutableArray<Lua51Instruction> TranslateCode(
        Lua53Prototype prototype,
        int? environmentIndex,
        ImmutableArray<int> childIds,
        IReadOnlyDictionary<int, int?> environmentIndexes)
    {
        var globalAccesses = AnalyzeGlobalAccesses(prototype, environmentIndex);
        var pcMap = BuildPcMap(prototype, childIds, environmentIndexes);
        var count = pcMap[^1];
        var code = ImmutableArray.CreateBuilder<Lua51Instruction>(count);
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var instruction = prototype.Code[pc];
            if (instruction.Opcode == Lua53Opcode.Jump && instruction.A > 0)
            {
                var target = pc + 1 + instruction.SignedBx;
                code.Add(Lua51Instruction.CreateAbc(
                    Lua51Opcode.Close,
                    instruction.A - 1,
                    0,
                    0));
                code.Add(Lua51Instruction.CreateASignedBx(
                    Lua51Opcode.Jump,
                    0,
                    pcMap[target] - (pcMap[pc] + 2)));
            }
            else if (instruction.Opcode == Lua53Opcode.GenericForLoop)
            {
                var target = pc + 1 + instruction.SignedBx;
                code.Add(Lua51Instruction.CreateASignedBx(
                    Lua51Opcode.Jump,
                    0,
                    pcMap[target] - (pcMap[pc] + 1)));
            }
            else if (instruction.Opcode is Lua53Opcode.Jump or Lua53Opcode.NumericForLoop or Lua53Opcode.NumericForPrepare)
            {
                var target = pc + 1 + instruction.SignedBx;
                var mapped = pcMap[target] - (pcMap[pc] + 1);
                code.Add(Lua51Instruction.CreateASignedBx(Map(instruction.Opcode), instruction.A, mapped));
            }
            else if (instruction.Opcode == Lua53Opcode.Return &&
                pc > 0 &&
                prototype.Code[pc - 1].Opcode == Lua53Opcode.TailCall)
            {
                // Lua 5.1's verifier treats TAILCALL as an open call and requires the
                // following (unreachable) RETURN to consume the open result range.
                code.Add(Lua51Instruction.CreateAbc(
                    Lua51Opcode.Return,
                    instruction.A,
                    0,
                    0));
            }
            else if (globalAccesses.TryGetValue(pc, out var globalAccess))
            {
                code.Add(globalAccess.IsSet
                    ? Lua51Instruction.CreateABx(
                        Lua51Opcode.SetGlobal,
                        instruction.C,
                        globalAccess.ConstantIndex)
                    : Lua51Instruction.CreateABx(
                        Lua51Opcode.GetGlobal,
                        instruction.A,
                        globalAccess.ConstantIndex));
            }
            else code.Add(Translate(instruction, environmentIndex));
            if (instruction.Opcode != Lua53Opcode.Closure) continue;
            var childEnvironmentIndex = environmentIndexes[childIds[instruction.Bx]];
            foreach (var (upvalue, index) in prototype.NestedPrototypes[instruction.Bx].Upvalues
                .Select(static (upvalue, index) => (upvalue, index)))
            {
                if (index == childEnvironmentIndex)
                {
                    continue;
                }

                code.Add(Lua51Instruction.CreateAbc(
                    upvalue.InStack != 0 ? Lua51Opcode.Move : Lua51Opcode.GetUpvalue,
                    0,
                    upvalue.InStack != 0
                        ? upvalue.Index
                        : RemapUpvalueIndex(upvalue.Index, environmentIndex),
                    0));
            }
        }
        return code.MoveToImmutable();
    }

    private static int[] BuildPcMap(
        Lua53Prototype prototype,
        ImmutableArray<int> childIds,
        IReadOnlyDictionary<int, int?> environmentIndexes)
    {
        var pcMap = new int[prototype.Code.Length + 1];
        var count = 0;
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            pcMap[pc] = count++;
            var instruction = prototype.Code[pc];
            if (instruction.Opcode == Lua53Opcode.Closure)
            {
                count += prototype.NestedPrototypes[instruction.Bx].Upvalues.Length -
                    (environmentIndexes[childIds[instruction.Bx]].HasValue ? 1 : 0);
            }
            else if (instruction.Opcode == Lua53Opcode.Jump && instruction.A > 0)
            {
                count++;
            }
        }

        pcMap[^1] = count;
        return pcMap;
    }

    private static Dictionary<int, GlobalAccess> AnalyzeGlobalAccesses(
        Lua53Prototype prototype,
        int? environmentIndex)
    {
        var accesses = new Dictionary<int, GlobalAccess>();
        if (!environmentIndex.HasValue)
        {
            return accesses;
        }

        var consumedEnvironmentLoads = new HashSet<int>();
        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            var instruction = prototype.Code[pc];
            var environmentRegister = instruction.Opcode switch
            {
                Lua53Opcode.GetTable => instruction.B,
                Lua53Opcode.SetTable => instruction.A,
                _ => -1,
            };
            if (environmentRegister < 0)
            {
                continue;
            }

            var environmentDefinition = FindRegisterDefinition(
                prototype,
                pc,
                environmentRegister);
            if (environmentDefinition < 0 ||
                prototype.Code[environmentDefinition] is not
                {
                    Opcode: Lua53Opcode.GetUpvalue,
                    B: var upvalueIndex,
                } ||
                upvalueIndex != environmentIndex.Value)
            {
                continue;
            }

            var keyRegister = instruction.Opcode == Lua53Opcode.GetTable
                ? instruction.C
                : instruction.B;
            var keyDefinition = FindRegisterDefinition(prototype, pc, keyRegister);
            if (keyDefinition < 0 ||
                prototype.Code[keyDefinition].Opcode != Lua53Opcode.LoadConstant)
            {
                throw new InvalidDataException(
                    "Lua 5.1 function-environment access requires a constant string key.");
            }

            var constantIndex = prototype.Code[keyDefinition].Bx;
            if ((uint)constantIndex >= (uint)prototype.Constants.Length ||
                prototype.Constants[constantIndex].Kind is not
                    (Lua53ConstantKind.ShortString or Lua53ConstantKind.LongString))
            {
                throw new InvalidDataException(
                    "Lua 5.1 function-environment access requires a constant string key.");
            }

            accesses.Add(pc, new GlobalAccess(
                instruction.Opcode == Lua53Opcode.SetTable,
                constantIndex));
            consumedEnvironmentLoads.Add(environmentDefinition);
        }

        for (var pc = 0; pc < prototype.Code.Length; pc++)
        {
            if (prototype.Code[pc] is
                {
                    Opcode: Lua53Opcode.GetUpvalue,
                    B: var upvalueIndex,
                } &&
                upvalueIndex == environmentIndex.Value &&
                !consumedEnvironmentLoads.Contains(pc))
            {
                throw new InvalidDataException(
                    "A canonical Lua 5.1 environment value cannot be materialized directly.");
            }
        }

        return accesses;
    }

    private static int FindRegisterDefinition(
        Lua53Prototype prototype,
        int programCounter,
        int register)
    {
        for (var pc = programCounter - 1; pc >= 0; pc--)
        {
            if (WritesRegister(prototype.Code[pc], register))
            {
                return pc;
            }
        }

        return -1;
    }

    private static bool WritesRegister(Lua53Instruction instruction, int register) =>
        instruction.Opcode switch
        {
            Lua53Opcode.LoadNil => register >= instruction.A &&
                register <= instruction.A + instruction.B,
            Lua53Opcode.Self => register == instruction.A || register == instruction.A + 1,
            Lua53Opcode.Call => register >= instruction.A &&
                (instruction.C == 0 || register < instruction.A + instruction.C - 1),
            Lua53Opcode.NumericForLoop => register == instruction.A ||
                register == instruction.A + 3,
            Lua53Opcode.NumericForPrepare => register == instruction.A,
            Lua53Opcode.GenericForCall => register >= instruction.A + 3 &&
                register < instruction.A + 3 + instruction.C,
            Lua53Opcode.GenericForLoop => register == instruction.A,
            Lua53Opcode.VarArg => register >= instruction.A &&
                (instruction.C == 0 || register < instruction.A + instruction.C - 1),
            Lua53Opcode.Move or Lua53Opcode.LoadConstant or Lua53Opcode.LoadConstantExtra or
                Lua53Opcode.LoadBoolean or Lua53Opcode.GetUpvalue or Lua53Opcode.GetGlobal or
                Lua53Opcode.GetTable or Lua53Opcode.NewTable or Lua53Opcode.Add or
                Lua53Opcode.Subtract or Lua53Opcode.Multiply or Lua53Opcode.Modulo or
                Lua53Opcode.Power or Lua53Opcode.Divide or Lua53Opcode.FloorDivide or
                Lua53Opcode.BitwiseAnd or Lua53Opcode.BitwiseOr or Lua53Opcode.BitwiseXor or
                Lua53Opcode.ShiftLeft or Lua53Opcode.ShiftRight or Lua53Opcode.UnaryMinus or
                Lua53Opcode.BitwiseNot or Lua53Opcode.LogicalNot or Lua53Opcode.Length or
                Lua53Opcode.Concatenate or Lua53Opcode.TestSet or Lua53Opcode.Closure =>
                register == instruction.A,
            _ => false,
        };

    private static int RemapUpvalueIndex(int upvalueIndex, int? environmentIndex)
    {
        if (!environmentIndex.HasValue)
        {
            return upvalueIndex;
        }

        if (upvalueIndex == environmentIndex.Value)
        {
            throw new InvalidDataException(
                "Lua 5.1 function-environment upvalues are implicit and cannot be bound directly.");
        }

        return upvalueIndex > environmentIndex.Value ? upvalueIndex - 1 : upvalueIndex;
    }

    private static Lua51Instruction Translate(
        Lua53Instruction i,
        int? environmentIndex)
    {
        if (i.Opcode == Lua53Opcode.GetUpvalue && i.B == environmentIndex)
        {
            // Keep the instruction count stable for jump/debug mappings. The register is dead
            // after the associated table access is restored to GETGLOBAL/SETGLOBAL.
            return Lua51Instruction.CreateAbc(Lua51Opcode.LoadNil, i.A, i.A, 0);
        }

        if (i.Opcode is Lua53Opcode.GetUpvalue or Lua53Opcode.SetUpvalue)
        {
            return Lua51Instruction.CreateAbc(
                Map(i.Opcode),
                i.A,
                RemapUpvalueIndex(i.B, environmentIndex),
                i.C);
        }

        return i.Opcode switch
        {
            Lua53Opcode.GetGlobal => Lua51Instruction.CreateABx(Lua51Opcode.GetGlobal, i.A, i.C & 0xff),
            Lua53Opcode.SetTableUpvalue => Lua51Instruction.CreateABx(Lua51Opcode.SetGlobal, i.C, i.B & 0xff),
            Lua53Opcode.LoadConstant => Lua51Instruction.CreateABx(Lua51Opcode.LoadConstant, i.A, i.Bx),
            Lua53Opcode.LoadNil => Lua51Instruction.CreateAbc(
                Lua51Opcode.LoadNil,
                i.A,
                checked(i.A + i.B),
                0),
            Lua53Opcode.Jump or Lua53Opcode.NumericForLoop or Lua53Opcode.NumericForPrepare => Lua51Instruction.CreateASignedBx(Map(i.Opcode), i.A, i.SignedBx),
            _ => Lua51Instruction.CreateAbc(Map(i.Opcode), i.A, i.B, i.C),
        };
    }

    private readonly record struct GlobalAccess(bool IsSet, int ConstantIndex);
    private static Lua51Opcode Map(Lua53Opcode op) => op switch
    {
        Lua53Opcode.Move => Lua51Opcode.Move,
        Lua53Opcode.LoadConstant => Lua51Opcode.LoadConstant,
        Lua53Opcode.LoadBoolean => Lua51Opcode.LoadBoolean,
        Lua53Opcode.LoadNil => Lua51Opcode.LoadNil,
        Lua53Opcode.GetUpvalue => Lua51Opcode.GetUpvalue,
        Lua53Opcode.GetTable => Lua51Opcode.GetTable,
        Lua53Opcode.SetUpvalue => Lua51Opcode.SetUpvalue,
        Lua53Opcode.SetTable => Lua51Opcode.SetTable,
        Lua53Opcode.NewTable => Lua51Opcode.NewTable,
        Lua53Opcode.Self => Lua51Opcode.Self,
        Lua53Opcode.Add => Lua51Opcode.Add,
        Lua53Opcode.Subtract => Lua51Opcode.Subtract,
        Lua53Opcode.Multiply => Lua51Opcode.Multiply,
        Lua53Opcode.Divide => Lua51Opcode.Divide,
        Lua53Opcode.Modulo => Lua51Opcode.Modulo,
        Lua53Opcode.Power => Lua51Opcode.Power,
        Lua53Opcode.UnaryMinus => Lua51Opcode.UnaryMinus,
        Lua53Opcode.LogicalNot => Lua51Opcode.LogicalNot,
        Lua53Opcode.Length => Lua51Opcode.Length,
        Lua53Opcode.Concatenate => Lua51Opcode.Concatenate,
        Lua53Opcode.Jump => Lua51Opcode.Jump,
        Lua53Opcode.Equal => Lua51Opcode.Equal,
        Lua53Opcode.LessThan => Lua51Opcode.LessThan,
        Lua53Opcode.LessOrEqual => Lua51Opcode.LessOrEqual,
        Lua53Opcode.Test => Lua51Opcode.Test,
        Lua53Opcode.TestSet => Lua51Opcode.TestSet,
        Lua53Opcode.Call => Lua51Opcode.Call,
        Lua53Opcode.TailCall => Lua51Opcode.TailCall,
        Lua53Opcode.Return => Lua51Opcode.Return,
        Lua53Opcode.NumericForLoop => Lua51Opcode.NumericForLoop,
        Lua53Opcode.NumericForPrepare => Lua51Opcode.NumericForPrepare,
        Lua53Opcode.GenericForCall => Lua51Opcode.GenericForLoop,
        Lua53Opcode.GenericForLoop => Lua51Opcode.Jump,
        Lua53Opcode.SetList => Lua51Opcode.SetList,
        Lua53Opcode.Closure => Lua51Opcode.Closure,
        Lua53Opcode.VarArg => Lua51Opcode.VarArg,
        _ => throw new InvalidDataException($"Opcode {op} is not representable in Lua 5.1"),
    };
    private static Lua51Constant Translate(Lua53Constant c) => c.Kind switch
    {
        Lua53ConstantKind.Nil => Lua51Constant.Nil,
        Lua53ConstantKind.False => Lua51Constant.False,
        Lua53ConstantKind.True => Lua51Constant.True,
        Lua53ConstantKind.Integer => Lua51Constant.FromNumber(c.IntegerValue),
        Lua53ConstantKind.Float => Lua51Constant.FromNumber(c.FloatValue),
        Lua53ConstantKind.ShortString or Lua53ConstantKind.LongString => Lua51Constant.FromString(new Lua51String(c.StringValue!.Value.ToArray())),
        _ => throw new InvalidDataException("Unknown constant kind"),
    };
    private static void WritePrototype(List<byte> b, Lua51Prototype p, bool strip)
    {
        WriteString(b, strip ? null : p.Source); WriteInt(b, p.LineDefined); WriteInt(b, p.LastLineDefined); b.Add(p.UpvalueCount); b.Add(p.ParameterCount); b.Add(p.VarArgFlags); b.Add(p.MaximumStackSize);
        WriteInt(b, p.Code.Length); foreach (var i in p.Code) WriteUInt(b, i.RawValue); WriteInt(b, p.Constants.Length); foreach (var c in p.Constants) WriteConstant(b, c);
        WriteInt(b, p.NestedPrototypes.Length); foreach (var n in p.NestedPrototypes) WritePrototype(b, n, strip); WriteInt(b, strip ? 0 : p.LineInfo.Length); if (!strip) foreach (var x in p.LineInfo) WriteInt(b, x);
        WriteInt(b, strip ? 0 : p.LocalVariables.Length); if (!strip) foreach (var l in p.LocalVariables) { WriteString(b, l.Name); WriteInt(b, l.StartProgramCounter); WriteInt(b, l.EndProgramCounter); }
        WriteInt(b, strip ? 0 : p.UpvalueNames.Length); if (!strip) foreach (var n in p.UpvalueNames) WriteString(b, n);
    }
    private static void WriteConstant(List<byte> b, Lua51Constant c) { b.Add(c.Kind switch { Lua51ConstantKind.Nil => (byte)0, Lua51ConstantKind.False or Lua51ConstantKind.True => (byte)1, Lua51ConstantKind.Number => (byte)3, Lua51ConstantKind.String => (byte)4, _ => throw new InvalidDataException() }); if (c.Kind is Lua51ConstantKind.False or Lua51ConstantKind.True) b.Add((byte)(c.Kind == Lua51ConstantKind.True ? 1 : 0)); else if (c.Kind == Lua51ConstantKind.Number) { Span<byte> x = stackalloc byte[8]; BinaryPrimitives.WriteInt64LittleEndian(x, BitConverter.DoubleToInt64Bits(c.NumberValue)); b.AddRange(x.ToArray()); } else if (c.Kind == Lua51ConstantKind.String) WriteString(b, c.StringValue); }
    private static void WriteString(List<byte> b, Lua51String? s) { if (s is not { } v) { WriteSizeT(b, 0); return; } WriteSizeT(b, checked((ulong)v.Length + 1)); b.AddRange(v.Bytes); b.Add(0); }
    private static void WriteInt(List<byte> b, int x) { Span<byte> v = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(v, x); b.AddRange(v.ToArray()); }
    private static void WriteUInt(List<byte> b, uint x) { Span<byte> v = stackalloc byte[4]; BinaryPrimitives.WriteUInt32LittleEndian(v, x); b.AddRange(v.ToArray()); }
    private static void WriteSizeT(List<byte> b, ulong x) { Span<byte> v = stackalloc byte[8]; BinaryPrimitives.WriteUInt64LittleEndian(v, x); b.AddRange(v.ToArray()); }
}
