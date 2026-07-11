namespace Lunil.IR.Lua54;

public readonly record struct Lua54OpcodeInfo(
    Lua54InstructionMode Mode,
    bool SetsRegisterA = false,
    bool IsTest = false,
    bool UsesTop = false,
    bool SetsTop = false,
    bool IsMetamethod = false)
{
    private static readonly Lua54OpcodeInfo[] Infos =
    [
        Abc(a: true),                       // MOVE
        AsBx(a: true),                      // LOADI
        AsBx(a: true),                      // LOADF
        ABx(a: true),                       // LOADK
        ABx(a: true),                       // LOADKX
        Abc(a: true),                       // LOADFALSE
        Abc(a: true),                       // LFALSESKIP
        Abc(a: true),                       // LOADTRUE
        Abc(a: true),                       // LOADNIL
        Abc(a: true),                       // GETUPVAL
        Abc(),                              // SETUPVAL
        Abc(a: true),                       // GETTABUP
        Abc(a: true),                       // GETTABLE
        Abc(a: true),                       // GETI
        Abc(a: true),                       // GETFIELD
        Abc(),                              // SETTABUP
        Abc(),                              // SETTABLE
        Abc(),                              // SETI
        Abc(),                              // SETFIELD
        Abc(a: true),                       // NEWTABLE
        Abc(a: true),                       // SELF
        Abc(a: true),                       // ADDI
        Abc(a: true),                       // ADDK
        Abc(a: true),                       // SUBK
        Abc(a: true),                       // MULK
        Abc(a: true),                       // MODK
        Abc(a: true),                       // POWK
        Abc(a: true),                       // DIVK
        Abc(a: true),                       // IDIVK
        Abc(a: true),                       // BANDK
        Abc(a: true),                       // BORK
        Abc(a: true),                       // BXORK
        Abc(a: true),                       // SHRI
        Abc(a: true),                       // SHLI
        Abc(a: true),                       // ADD
        Abc(a: true),                       // SUB
        Abc(a: true),                       // MUL
        Abc(a: true),                       // MOD
        Abc(a: true),                       // POW
        Abc(a: true),                       // DIV
        Abc(a: true),                       // IDIV
        Abc(a: true),                       // BAND
        Abc(a: true),                       // BOR
        Abc(a: true),                       // BXOR
        Abc(a: true),                       // SHL
        Abc(a: true),                       // SHR
        Abc(mm: true),                      // MMBIN
        Abc(mm: true),                      // MMBINI
        Abc(mm: true),                      // MMBINK
        Abc(a: true),                       // UNM
        Abc(a: true),                       // BNOT
        Abc(a: true),                       // NOT
        Abc(a: true),                       // LEN
        Abc(a: true),                       // CONCAT
        Abc(),                              // CLOSE
        Abc(),                              // TBC
        Sj(),                               // JMP
        Abc(test: true),                    // EQ
        Abc(test: true),                    // LT
        Abc(test: true),                    // LE
        Abc(test: true),                    // EQK
        Abc(test: true),                    // EQI
        Abc(test: true),                    // LTI
        Abc(test: true),                    // LEI
        Abc(test: true),                    // GTI
        Abc(test: true),                    // GEI
        Abc(test: true),                    // TEST
        Abc(a: true, test: true),           // TESTSET
        Abc(a: true, usesTop: true, setsTop: true), // CALL
        Abc(a: true, usesTop: true, setsTop: true), // TAILCALL
        Abc(usesTop: true),                 // RETURN
        Abc(),                              // RETURN0
        Abc(),                              // RETURN1
        ABx(a: true),                       // FORLOOP
        ABx(a: true),                       // FORPREP
        ABx(),                              // TFORPREP
        Abc(),                              // TFORCALL
        ABx(a: true),                       // TFORLOOP
        Abc(usesTop: true),                 // SETLIST
        ABx(a: true),                       // CLOSURE
        Abc(a: true, setsTop: true),        // VARARG
        Abc(a: true, usesTop: true),        // VARARGPREP
        Ax(),                               // EXTRAARG
    ];

    public static int OpcodeCount => Infos.Length;

    public static bool IsDefined(Lua54Opcode opcode) => (uint)opcode < (uint)Infos.Length;

    public static Lua54OpcodeInfo Get(Lua54Opcode opcode)
    {
        if (!IsDefined(opcode))
        {
            throw new ArgumentOutOfRangeException(nameof(opcode));
        }

        return Infos[(int)opcode];
    }

    private static Lua54OpcodeInfo Abc(
        bool a = false,
        bool test = false,
        bool usesTop = false,
        bool setsTop = false,
        bool mm = false) => new(Lua54InstructionMode.Abc, a, test, usesTop, setsTop, mm);

    private static Lua54OpcodeInfo ABx(bool a = false) => new(Lua54InstructionMode.ABx, a);

    private static Lua54OpcodeInfo AsBx(bool a = false) => new(Lua54InstructionMode.ASignedBx, a);

    private static Lua54OpcodeInfo Ax() => new(Lua54InstructionMode.Ax);

    private static Lua54OpcodeInfo Sj() => new(Lua54InstructionMode.SignedJump);
}
