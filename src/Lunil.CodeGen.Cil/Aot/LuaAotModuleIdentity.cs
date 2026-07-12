using Lunil.CodeGen.Cil.Artifacts;
using Lunil.IR.Canonical;

namespace Lunil.CodeGen.Cil;

public static class LuaAotModuleIdentity
{
    public static string ComputeContentId(LuaIrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return LuaCanonicalModuleSerializer.Sha256Hex(
            LuaCanonicalModuleSerializer.Serialize(module));
    }

    public static byte[] SerializeCanonicalModule(LuaIrModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return LuaCanonicalModuleSerializer.Serialize(module);
    }

    public static LuaIrModule DeserializeCanonicalModule(ReadOnlySpan<byte> content) =>
        LuaCanonicalModuleSerializer.Deserialize(content);
}
