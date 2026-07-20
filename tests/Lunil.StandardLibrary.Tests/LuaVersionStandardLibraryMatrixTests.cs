using System.Text;
using Lunil.Core;
using Lunil.Core.Text;
using Lunil.Runtime;
using Lunil.Runtime.Execution;
using Lunil.Semantics.Binding;
using Lunil.Semantics.Lowering;
using Lunil.Syntax.Parsing;
using Lunil.Runtime.Values;

namespace Lunil.StandardLibrary.Tests;

public sealed class LuaVersionStandardLibraryMatrixTests
{
    public static IEnumerable<object[]> Versions() =>
        Enum.GetValues<LuaLanguageVersion>().Select(version => new object[] { version });

    [Theory]
    [MemberData(nameof(Versions))]
    public void GeneratedProfileControlsObservableLibrarySurface(LuaLanguageVersion version)
    {
        var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
        LuaStandardLibrary.InstallAll(state);
        var features = LuaVersionFeatureTable.Get(version);

        Assert.Equal(features.HasRawLength, IsFunction(state.Globals, state, "rawlen"));
        Assert.Equal(features.HasGlobalUnpack, IsFunction(state.Globals, state, "unpack"));
        Assert.Equal(features.HasLoadString, IsFunction(state.Globals, state, "loadstring"));
        Assert.Equal(features.HasModuleLibrary, IsFunction(state.Globals, state, "module"));

        var stringModule = GetTable(state.Globals, state, "string");
        Assert.Equal(features.HasStringPack, IsFunction(stringModule, state, "pack"));
        Assert.Equal(features.HasStringPack, IsFunction(stringModule, state, "unpack"));
        Assert.Equal(features.HasStringGFind, IsFunction(stringModule, state, "gfind"));

        var tableModule = GetTable(state.Globals, state, "table");
        Assert.Equal(features.HasTableMove, IsFunction(tableModule, state, "move"));
        Assert.Equal(features.HasTablePack, IsFunction(tableModule, state, "pack"));
        Assert.Equal(features.HasTableCreate, IsFunction(tableModule, state, "create"));
        Assert.Equal(features.HasLegacyTable, IsFunction(tableModule, state, "maxn"));
        Assert.Equal(version == LuaLanguageVersion.Lua51, IsFunction(tableModule, state, "foreach"));
        Assert.Equal(version == LuaLanguageVersion.Lua51, IsFunction(tableModule, state, "foreachi"));

        var mathModule = GetTable(state.Globals, state, "math");
        Assert.Equal(features.HasLegacyMath, IsFunction(mathModule, state, "log10"));
        Assert.Equal(features.HasLegacyMath, IsFunction(mathModule, state, "pow"));
        Assert.Equal(features.HasDebugSetCStackLimit, IsFunction(
            GetTable(state.Globals, state, "debug"), state, "setcstacklimit"));

        var package = GetTable(state.Globals, state, "package");
        Assert.Equal(features.HasPackageSearchers, IsTable(package, state, "searchers"));
        Assert.Equal(features.HasPackageLoaders, IsTable(package, state, "loaders"));
        Assert.Equal(features.HasPackageSeeAll, IsFunction(package, state, "seeall"));
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public void VersionSpecificLibraryCallsUseTheSelectedContract(LuaLanguageVersion version)
    {
        var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
        LuaStandardLibrary.InstallAll(state);
        var values = Execute(
            state,
            "local t={10,20}; return " +
            "type(rawlen),type(unpack),type(loadstring),type(module)," +
            "type(table.move),type(table.pack),type(table.create)," +
            "type(string.pack),type(string.gfind),type(math.log10)," +
            "type(debug.setcstacklimit),type(package.searchers),type(package.loaders)");
        var features = LuaVersionFeatureTable.Get(version);

        Assert.Equal(features.HasRawLength ? "function" : "nil", Text(values[0]));
        Assert.Equal(features.HasGlobalUnpack ? "function" : "nil", Text(values[1]));
        Assert.Equal(features.HasLoadString ? "function" : "nil", Text(values[2]));
        Assert.Equal(features.HasModuleLibrary ? "function" : "nil", Text(values[3]));
        Assert.Equal(features.HasTableMove ? "function" : "nil", Text(values[4]));
        Assert.Equal(features.HasTablePack ? "function" : "nil", Text(values[5]));
        Assert.Equal(features.HasTableCreate ? "function" : "nil", Text(values[6]));
        Assert.Equal(features.HasStringPack ? "function" : "nil", Text(values[7]));
        Assert.Equal(features.HasStringGFind ? "function" : "nil", Text(values[8]));
        Assert.Equal(features.HasLegacyMath ? "function" : "nil", Text(values[9]));
        Assert.Equal(features.HasDebugSetCStackLimit ? "function" : "nil", Text(values[10]));
        Assert.Equal(features.HasPackageSearchers ? "table" : "nil", Text(values[11]));
        Assert.Equal(features.HasPackageLoaders ? "table" : "nil", Text(values[12]));

        if (version == LuaLanguageVersion.Lua51)
        {
            var legacy = Execute(
                state,
                "local t={a=1,b=2}; " +
                "return table.foreach(t,function(k,v) if k=='b' then return v end end)," +
                "table.foreachi({10,20},function(i,v) if i==2 then return v end end)");
            Assert.Equal(2, legacy[0].AsFloat());
            Assert.Equal(20, legacy[1].AsFloat());
        }
    }

    private static LuaTable GetTable(LuaTable globals, LuaState state, string name)
    {
        var value = globals.Get(Key(state, name));
        return Assert.IsType<LuaTable>(value.AsTable());
    }

    private static bool IsFunction(LuaTable table, LuaState state, string name) =>
        table.Get(Key(state, name)).Kind == LuaValueKind.Function;

    private static bool IsTable(LuaTable table, LuaState state, string name) =>
        table.Get(Key(state, name)).Kind == LuaValueKind.Table;

    private static LuaValue Key(LuaState state, string name) =>
        LuaValue.FromString(state.Strings.GetOrCreate(Encoding.UTF8.GetBytes(name)));

    private static string Text(LuaValue value) => value.AsString().ToString();

    private static LuaValue[] Execute(LuaState state, string source)
    {
        var syntax = LuaParser.Parse(
            SourceText.FromUtf8(source),
            parserOptions: new LuaParserOptions { LanguageVersion = state.LanguageVersion });
        var semantic = LuaBinder.Bind(
            syntax,
            LuaBinderOptions.Default with { LanguageVersion = state.LanguageVersion });
        Assert.Empty(semantic.Diagnostics);
        var lowering = LuaLowerer.Lower(semantic);
        Assert.Empty(lowering.Diagnostics);
        return new LuaInterpreter()
            .Execute(state, state.CreateMainClosure(lowering.Module!))
            .Values
            .ToArray();
    }
}
