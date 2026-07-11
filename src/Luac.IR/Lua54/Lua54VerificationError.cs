namespace Luac.IR.Lua54;

public sealed record Lua54VerificationError(
    string PrototypePath,
    string Message,
    int? ProgramCounter = null);
