using System.Reflection;
using System.Reflection.Emit;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Lunil.CodeGen.Cil.Jit;
using Lunil.Runtime.CodeGen;

namespace Lunil.CodeGen.Cil.Emission;

internal sealed class MetadataLuaNumericRegionIlGenerator : LuaNumericRegionIlGenerator
{
    private readonly MetadataBuilder _metadata;
    private readonly InstructionEncoder _encoder;
    private readonly MetadataMemberReferenceCache _members;
    private readonly List<Type> _locals = [];
    private int _nextLabelId;

    public MetadataLuaNumericRegionIlGenerator(
        MetadataBuilder metadata,
        InstructionEncoder encoder,
        AssemblyReferenceHandle systemRuntime,
        AssemblyReferenceHandle runtime)
    {
        _metadata = metadata;
        _encoder = encoder;
        _members = new MetadataMemberReferenceCache(metadata, systemRuntime, runtime);
    }

    public IReadOnlyList<Type> Locals => _locals;

    public int BranchInstructionCount { get; private set; }

    public void EncodeType(SignatureTypeEncoder encoder, Type type) =>
        _members.EncodeType(encoder, type);

    public override LuaNumericIlLabel DefineLabel() =>
        new(_nextLabelId++, _encoder.DefineLabel());

    public override LuaNumericIlLocal DeclareLocal(Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        var index = _locals.Count;
        _locals.Add(type);
        return new LuaNumericIlLocal(index, type);
    }

    public override void MarkLabel(LuaNumericIlLabel label) =>
        _encoder.MarkLabel((LabelHandle)label.BackendHandle!);

    public override void Emit(OpCode opcode) => _encoder.OpCode(ToMetadataOpCode(opcode));

    public override void Emit(OpCode opcode, int value)
    {
        if (opcode == OpCodes.Ldc_I4)
        {
            _encoder.LoadConstantI4(value);
            return;
        }

        throw UnsupportedOperand(opcode, typeof(int));
    }

    public override void Emit(OpCode opcode, sbyte value)
    {
        if (opcode == OpCodes.Ldc_I4_S)
        {
            _encoder.LoadConstantI4(value);
            return;
        }

        throw UnsupportedOperand(opcode, typeof(sbyte));
    }

    public override void Emit(OpCode opcode, long value)
    {
        if (opcode == OpCodes.Ldc_I8)
        {
            _encoder.LoadConstantI8(value);
            return;
        }

        throw UnsupportedOperand(opcode, typeof(long));
    }

    public override void Emit(OpCode opcode, double value)
    {
        if (opcode == OpCodes.Ldc_R8)
        {
            _encoder.LoadConstantR8(value);
            return;
        }

        throw UnsupportedOperand(opcode, typeof(double));
    }

    public override void Emit(OpCode opcode, string value)
    {
        if (opcode == OpCodes.Ldstr)
        {
            _encoder.LoadString(_metadata.GetOrAddUserString(value));
            return;
        }

        throw UnsupportedOperand(opcode, typeof(string));
    }

    public override void Emit(OpCode opcode, LuaNumericIlLabel label)
    {
        BranchInstructionCount++;
        _encoder.Branch(ToMetadataOpCode(opcode), (LabelHandle)label.BackendHandle!);
    }

    public override void Emit(OpCode opcode, LuaNumericIlLabel[] labels)
    {
        if (opcode != OpCodes.Switch)
        {
            throw UnsupportedOperand(opcode, typeof(LuaNumericIlLabel[]));
        }

        BranchInstructionCount++;
        var @switch = _encoder.Switch(labels.Length);
        foreach (var label in labels)
        {
            @switch.Branch((LabelHandle)label.BackendHandle!);
        }
    }

    public override void Emit(OpCode opcode, LuaNumericIlLocal local)
    {
        if (opcode == OpCodes.Ldloc)
        {
            _encoder.LoadLocal(local.Index);
        }
        else if (opcode == OpCodes.Stloc)
        {
            _encoder.StoreLocal(local.Index);
        }
        else if (opcode == OpCodes.Ldloca)
        {
            _encoder.LoadLocalAddress(local.Index);
        }
        else
        {
            throw UnsupportedOperand(opcode, typeof(LuaNumericIlLocal));
        }
    }

    public override void Emit(OpCode opcode, MethodInfo method) =>
        EmitMember(opcode, _members.GetOrAdd(method));

    public override void Emit(OpCode opcode, ConstructorInfo constructor) =>
        EmitMember(opcode, _members.GetOrAdd(constructor));

    private void EmitMember(OpCode opcode, MemberReferenceHandle member)
    {
        if (opcode == OpCodes.Call)
        {
            _encoder.Call(member);
        }
        else if (opcode == OpCodes.Callvirt || opcode == OpCodes.Newobj)
        {
            _encoder.OpCode(ToMetadataOpCode(opcode));
            _encoder.Token(member);
        }
        else
        {
            throw UnsupportedOperand(opcode, typeof(MemberReferenceHandle));
        }
    }

    private static ILOpCode ToMetadataOpCode(OpCode opcode) =>
        (ILOpCode)(ushort)opcode.Value;

    private static InvalidOperationException UnsupportedOperand(OpCode opcode, Type type) =>
        new($"Numeric-region opcode {opcode} does not accept a {type.Name} operand.");
}

internal sealed class MetadataMemberReferenceCache
{
    private readonly MetadataBuilder _metadata;
    private readonly AssemblyReferenceHandle _systemRuntime;
    private readonly AssemblyReferenceHandle _runtime;
    private readonly Dictionary<Type, TypeReferenceHandle> _types = [];
    private readonly Dictionary<MethodBase, MemberReferenceHandle> _members = [];

    public MetadataMemberReferenceCache(
        MetadataBuilder metadata,
        AssemblyReferenceHandle systemRuntime,
        AssemblyReferenceHandle runtime)
    {
        _metadata = metadata;
        _systemRuntime = systemRuntime;
        _runtime = runtime;
    }

    public MemberReferenceHandle GetOrAdd(MethodBase method)
    {
        ArgumentNullException.ThrowIfNull(method);
        if (_members.TryGetValue(method, out var existing))
        {
            return existing;
        }

        var signatureBlob = new BlobBuilder();
        var signature = new BlobEncoder(signatureBlob).MethodSignature(
            SignatureCallingConvention.Default,
            genericParameterCount: 0,
            isInstanceMethod: !method.IsStatic);
        var parameters = method.GetParameters();
        signature.Parameters(
            parameters.Length,
            returnType =>
            {
                if (method is ConstructorInfo || ((MethodInfo)method).ReturnType == typeof(void))
                {
                    returnType.Void();
                }
                else
                {
                    EncodeType(returnType.Type(), ((MethodInfo)method).ReturnType);
                }
            },
            encoder =>
            {
                foreach (var parameter in parameters)
                {
                    EncodeType(encoder.AddParameter().Type(), parameter.ParameterType);
                }
            });
        var member = _metadata.AddMemberReference(
            GetOrAddType(method.DeclaringType ?? throw new InvalidOperationException(
                "A numeric-region call target has no declaring type.")),
            _metadata.GetOrAddString(method is ConstructorInfo ? ".ctor" : method.Name),
            _metadata.GetOrAddBlob(signatureBlob));
        _members.Add(method, member);
        return member;
    }

    public void EncodeType(SignatureTypeEncoder encoder, Type type)
    {
        if (type == typeof(bool))
        {
            encoder.Boolean();
        }
        else if (type == typeof(byte))
        {
            encoder.Byte();
        }
        else if (type == typeof(int))
        {
            encoder.Int32();
        }
        else if (type == typeof(long))
        {
            encoder.Int64();
        }
        else if (type == typeof(double))
        {
            encoder.Double();
        }
        else if (type == typeof(string))
        {
            encoder.String();
        }
        else
        {
            encoder.Type(GetOrAddType(type), type.IsValueType);
        }
    }

    private TypeReferenceHandle GetOrAddType(Type type)
    {
        if (_types.TryGetValue(type, out var existing))
        {
            return existing;
        }

        var assembly = type.Assembly;
        EntityHandle scope;
        if (assembly == typeof(object).Assembly ||
            assembly.GetName().Name is "System.Private.CoreLib" or "System.Runtime")
        {
            scope = _systemRuntime;
        }
        else if (assembly == typeof(LuaCodegenAbiV4).Assembly)
        {
            scope = _runtime;
        }
        else
        {
            throw new InvalidOperationException(
                $"Numeric-region metadata cannot reference assembly '{assembly.GetName().Name}'.");
        }

        var handle = _metadata.AddTypeReference(
            scope,
            _metadata.GetOrAddString(type.Namespace ?? string.Empty),
            _metadata.GetOrAddString(type.Name));
        _types.Add(type, handle);
        return handle;
    }
}
