using System.Text;
using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Runtime.Values;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;

namespace Lunil.StandardLibrary.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class PucLua54TestGroup
{
    public const string Name = "PUC Lua 5.4";
}

[Collection(PucLua54TestGroup.Name)]
public sealed class PucLua54StandardLibraryTests
{
    public static TheoryData<string> PortableUserModeScripts =>
    [
        "strings.lua",
        "pm.lua",
        "tpack.lua",
        "math.lua",
        "utf8.lua",
        "files.lua",
    ];

    [Theory]
    [MemberData(nameof(PortableUserModeScripts))]
    public void OfficialPortableUserModeCorpusPasses(string script)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "PucLua54", script);
        var source = File.ReadAllText(path, Encoding.UTF8);
        var state = new LuaState();
        LuaStandardLibrary.InstallAll(
            state,
            new LuaStandardLibraryOptions { Console = new CaptureConsole() });
        state.SetGlobal("_U", LuaValue.FromBoolean(true));
        state.SetGlobal("_soft", LuaValue.FromBoolean(true));
        state.SetGlobal("_port", LuaValue.FromBoolean(true));
        state.SetGlobal("_nomsg", LuaValue.FromBoolean(true));

        var lowering = LuaLowerer.Lower(
            LuaBinder.Bind(LuaParser.Parse(SourceText.FromUtf8(source))));
        Assert.Empty(lowering.Diagnostics);

        var result = new LuaInterpreter(new LuaInterpreterOptions
        {
            MaximumInstructionCount = 10_000_000,
        }).Execute(state, state.CreateMainClosure(lowering.Module!));

        Assert.Equal(LuaVmSignal.Completed, result.Signal);
    }

    private sealed class CaptureConsole : ILuaConsole
    {
        private readonly List<byte> _output = [];

        public byte[] ReadStandardInput() => [];

        public void Write(ReadOnlyMemory<byte> bytes) => _output.AddRange(bytes.ToArray());

        public void WriteLine() => _output.Add((byte)'\n');
    }
}
