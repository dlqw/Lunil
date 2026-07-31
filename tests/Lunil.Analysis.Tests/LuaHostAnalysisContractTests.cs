using System.Collections.Immutable;
using System.Text.Json;
using Lunil.Core.Text;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;
using Lunil.Syntax.Lexing;
using Lunil.Syntax.Parsing;

namespace Lunil.Analysis.Tests;

public sealed class LuaHostAnalysisContractTests
{
    private static readonly LuaHostTypeDescriptor Any = new() { Kind = LuaHostTypeKind.Any };
    private static readonly LuaHostTypeDescriptor String = new() { Kind = LuaHostTypeKind.String };
    private static readonly LuaHostTypeDescriptor Callback = new()
    {
        Kind = LuaHostTypeKind.Function,
        Parameters = [new LuaHostParameterContract { Name = "value", Type = String }],
    };

    [Fact]
    public void JsonAndLuaStubAreVersionedDeterministicAndPreserveSourceMappings()
    {
        var contract = CreateContract();

        var json = contract.ToJson();
        var parsed = LuaHostAnalysisContract.ParseJson(json);
        var stub = parsed.ToLuaStub();

        Assert.Equal(LuaHostAnalysisContract.CurrentSchemaVersion, parsed.SchemaVersion);
        Assert.Equal("test-host", parsed.ContractId);
        Assert.Equal("cpp://engine/events#subscribe", parsed.Functions["game.subscribe"].Source!.Uri);
        Assert.Equal("cpp-implementation://engine/events#subscribe",
            parsed.Functions["game.subscribe"].Source!.ImplementationUri);
        Assert.Equal(stub, parsed.ToLuaStub());
        Assert.Contains("function game.subscribe(callback) end", stub, StringComparison.Ordinal);
        Assert.Contains("---@module 'inventory'", stub, StringComparison.Ordinal);
    }

    [Fact]
    public void UnknownSchemaAndInconsistentContractsFailClosed()
    {
        var json = CreateContract().ToJson().Replace(
            "\"schemaVersion\": 1",
            "\"schemaVersion\": 99",
            StringComparison.Ordinal);
        Assert.Throws<NotSupportedException>(() => LuaHostAnalysisContract.ParseJson(json));

        var invalidCallback = new LuaHostContractBuilder("invalid")
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "game.subscribe",
                Parameters = [new LuaHostParameterContract { Name = "value", Type = String }],
                Callback = new LuaHostCallbackContract { ParameterIndex = 0 },
            });
        Assert.Throws<InvalidOperationException>(() => invalidCallback.Build());

        var invalidPersistence = new LuaHostContractBuilder("invalid")
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "game.save",
                Parameters = [new LuaHostParameterContract { Name = "key", Type = String }],
                Persistence = new LuaHostPersistenceContract
                {
                    Operation = LuaPersistenceOperationKind.Write,
                    KeyParameterIndex = 0,
                    SchemaId = "save",
                    ValueType = Any,
                },
            });
        Assert.Throws<InvalidOperationException>(() => invalidPersistence.Build());

        Assert.Throws<JsonException>(() => LuaHostAnalysisContract.ParseJson("null"));
    }

    [Fact]
    public void HostCallsProjectEffectsCallbacksPersistenceAndNilPaths()
    {
        var result = Analyze(
            """
            local total = 0
            local function listener(value)
                total = value
            end
            game.subscribe(listener)
            local saved = game.load("slot-1")
            if saved ~= nil and saved.player ~= nil then
                print(saved.player.name)
            end
            game.save("slot-1", saved)
            game.delete("slot-1")
            game.clear()
            """,
            CreateContract());

        Assert.Contains(result.HostEffects, static fact =>
            fact.FunctionPath == "game.subscribe" &&
            fact.Effects.HasFlag(LuaHostEffectKind.RegistersCallback));
        var registration = Assert.Single(result.CallbackRegistrations);
        Assert.Equal(LuaHostCallbackInvocationKind.Deferred, registration.Invocation);
        Assert.Equal(LuaHostCallbackCardinality.Many, registration.Cardinality);
        Assert.Equal(LuaHostCallbackRetentionKind.Stored, registration.Retention);
        Assert.Equal("game.unsubscribe", registration.UnsubscribeFunction);
        Assert.True(registration.Escapes);
        Assert.NotNull(registration.CallbackFunctionId);

        Assert.Collection(
            result.PersistenceAccesses,
            read =>
            {
                Assert.Equal(LuaPersistenceOperationKind.Read, read.Operation);
                Assert.Equal("slot-1", read.Key);
                Assert.False(read.IsDynamicKey);
                Assert.True(read.MissingReturnsNil);
                Assert.Equal("save-v2", read.SchemaId);
                Assert.Equal(2, read.SchemaVersion);
                Assert.Equal("game.migrate", read.MigrationFunction);
            },
            write => Assert.Equal(LuaPersistenceOperationKind.Write, write.Operation),
            delete => Assert.Equal(LuaPersistenceOperationKind.Delete, delete.Operation),
            clear => Assert.Equal(LuaPersistenceOperationKind.Clear, clear.Operation));
        Assert.Contains(result.UpvalueCells, static cell => cell.Symbol.Name == "total" && cell.Escapes);
        Assert.True(result.NilPaths.Any(static path => path.Path.Contains("player", StringComparison.Ordinal)),
            string.Join(" | ", result.NilPaths.Select(static path => path.Path + "/" + path.HopCount)));
        Assert.DoesNotContain(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA6020");
    }

    [Fact]
    public void DynamicPersistenceKeysAndUnsafeNilChainsRemainConservative()
    {
        var result = Analyze(
            """
            local key = tostring(1)
            local saved = game.load(key)
            print(saved.player.name)
            """,
            CreateContract());

        var access = Assert.Single(result.PersistenceAccesses);
        Assert.True(access.IsDynamicKey);
        Assert.Null(access.Key);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA6020");
    }

    [Fact]
    public void CallbackLifetimeContractsDistinguishBorrowedAndEscapingRegistrations()
    {
        var contract = new LuaHostContractBuilder("callbacks")
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "host.withCallback",
                Parameters = [Parameter("callback", Callback)],
                Callback = new LuaHostCallbackContract
                {
                    ParameterIndex = 0,
                    Invocation = LuaHostCallbackInvocationKind.Synchronous,
                    Cardinality = LuaHostCallbackCardinality.Once,
                    Retention = LuaHostCallbackRetentionKind.Borrowed,
                },
            })
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "host.once",
                Parameters = [Parameter("callback", Callback)],
                Effects = LuaHostEffectKind.RegistersCallback,
                Callback = new LuaHostCallbackContract
                {
                    ParameterIndex = 0,
                    Invocation = LuaHostCallbackInvocationKind.Asynchronous,
                    Cardinality = LuaHostCallbackCardinality.Once,
                    Retention = LuaHostCallbackRetentionKind.Stored,
                    UnsubscribeFunction = "host.dispose",
                },
            })
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "host.dispose",
                Effects = LuaHostEffectKind.UnregistersCallback,
            })
            .Build();

        var result = Analyze(
            """
            local observed = ""
            local function callback(value) observed = value end
            host.withCallback(callback)
            host.once(callback)
            host.dispose()
            """,
            contract);

        Assert.Collection(
            result.CallbackRegistrations,
            borrowed =>
            {
                Assert.Equal(LuaHostCallbackRetentionKind.Borrowed, borrowed.Retention);
                Assert.Equal(LuaHostCallbackInvocationKind.Synchronous, borrowed.Invocation);
                Assert.False(borrowed.Escapes);
            },
            stored =>
            {
                Assert.Equal(LuaHostCallbackRetentionKind.Stored, stored.Retention);
                Assert.Equal(LuaHostCallbackInvocationKind.Asynchronous, stored.Invocation);
                Assert.Equal(LuaHostCallbackCardinality.Once, stored.Cardinality);
                Assert.True(stored.Escapes);
            });
        Assert.Contains(result.HostEffects, static fact =>
            fact.FunctionPath == "host.dispose" &&
            fact.Effects.HasFlag(LuaHostEffectKind.UnregistersCallback));
    }

    [Fact]
    public void LiteralIndexNilPathsAreNarrowedAndAssignmentsInvalidateThem()
    {
        var result = Analyze(
            """
            local saved = game.load("slot")
            if saved ~= nil and saved["player"] ~= nil then
                print(saved["player"].name)
            end
            saved = nil
            print(saved["player"].name)
            """,
            CreateContract());

        Assert.Contains(result.NilPaths, static path =>
            path.Path.Contains("player", StringComparison.Ordinal) && path.HopCount >= 1);
        Assert.Contains(result.Diagnostics, static diagnostic => diagnostic.Code == "LUA6020");
    }

    [Fact]
    public void SharedAndLoopUpvaluesUseStableCellsAcrossSiblingClosures()
    {
        var result = Analyze(
            """
            local shared = 0
            local function read()
                return shared
            end
            local function write()
                shared = "updated"
            end
            local callbacks = {}
            for i = 1, 3 do
                callbacks[i] = function() return i end
            end
            write()
            return read()
            """,
            hostContract: null);

        var shared = Assert.Single(result.UpvalueCells, static cell => cell.Symbol.Name == "shared");
        Assert.True(shared.ReaderFunctionIds.Length >= 1);
        Assert.True(shared.WriterFunctionIds.Length >= 1);
        Assert.Contains("updated", shared.Type.DisplayName, StringComparison.Ordinal);
        Assert.Contains(result.Functions, static function =>
            function.InferredReturns.Head.Any(type =>
                type.DisplayName.Contains("updated", StringComparison.Ordinal)));
        Assert.Contains(result.UpvalueCells, static cell => cell.Symbol.Name == "i" && cell.IsLoopCaptured);
    }

    [Fact]
    public void RecursiveClosuresWidenCapturedCellsWithoutUnboundedReanalysis()
    {
        var result = Analyze(
            """
            local value = 0
            local function recurse(depth)
                if depth > 0 then
                    value = "done"
                    return recurse(depth - 1)
                end
                return value
            end
            return recurse(2)
            """,
            hostContract: null);

        var cell = Assert.Single(result.UpvalueCells, static item => item.Symbol.Name == "value");
        Assert.Contains("done", cell.Type.DisplayName, StringComparison.Ordinal);
        Assert.False(result.BudgetUsage.WasExceeded);
        Assert.All(result.Functions, static function => Assert.InRange(function.FlowIterationCount, 0, 32));
    }

    private static LuaHostAnalysisContract CreateContract()
    {
        var player = new LuaHostTypeDescriptor
        {
            Kind = LuaHostTypeKind.Table,
            IsNullable = true,
            Fields = ImmutableDictionary<string, LuaHostTypeDescriptor>.Empty
                .Add("name", String),
        };
        var save = new LuaHostTypeDescriptor
        {
            Kind = LuaHostTypeKind.Table,
            Fields = ImmutableDictionary<string, LuaHostTypeDescriptor>.Empty
                .Add("player", player),
        };
        var builder = new LuaHostContractBuilder("test-host")
            .AddModule("inventory", save)
            .AddFunction(new LuaHostFunctionContract
            {
                Path = "game.subscribe",
                Parameters = [new LuaHostParameterContract { Name = "callback", Type = Callback }],
                Effects = LuaHostEffectKind.RegistersCallback | LuaHostEffectKind.MayThrow,
                Callback = new LuaHostCallbackContract
                {
                    ParameterIndex = 0,
                    Invocation = LuaHostCallbackInvocationKind.Deferred,
                    Cardinality = LuaHostCallbackCardinality.Many,
                    Retention = LuaHostCallbackRetentionKind.Stored,
                    UnsubscribeFunction = "game.unsubscribe",
                    MayThrow = true,
                },
                Source = new LuaHostSourceLocation
                {
                    Uri = "cpp://engine/events#subscribe",
                    ImplementationUri = "cpp-implementation://engine/events#subscribe",
                },
            })
            .AddFunction(PersistenceFunction("game.load", LuaPersistenceOperationKind.Read, save,
                [Parameter("key", String)], key: 0, value: null,
                returns: [save]))
            .AddFunction(PersistenceFunction("game.save", LuaPersistenceOperationKind.Write, save,
                [Parameter("key", String), Parameter("value", save)], key: 0, value: 1))
            .AddFunction(PersistenceFunction("game.delete", LuaPersistenceOperationKind.Delete, save,
                [Parameter("key", String)], key: 0, value: null))
            .AddFunction(PersistenceFunction("game.clear", LuaPersistenceOperationKind.Clear, save,
                [], key: null, value: null));
        return builder.Build();
    }

    private static LuaHostFunctionContract PersistenceFunction(
        string path,
        LuaPersistenceOperationKind operation,
        LuaHostTypeDescriptor save,
        ImmutableArray<LuaHostParameterContract> parameters,
        int? key,
        int? value,
        ImmutableArray<LuaHostTypeDescriptor> returns = default) => new()
    {
        Path = path,
        Parameters = parameters,
        Returns = returns.IsDefault ? [] : returns,
        Effects = operation switch
        {
            LuaPersistenceOperationKind.Read => LuaHostEffectKind.ReadsPersistence,
            LuaPersistenceOperationKind.Write => LuaHostEffectKind.WritesPersistence,
            LuaPersistenceOperationKind.Delete => LuaHostEffectKind.DeletesPersistence,
            _ => LuaHostEffectKind.ClearsPersistence,
        },
        Persistence = new LuaHostPersistenceContract
        {
            Operation = operation,
            KeyParameterIndex = key,
            ValueParameterIndex = value,
            SchemaId = "save-v2",
            SchemaVersion = 2,
            ValueType = save,
            MissingReturnsNil = true,
            MigrationFunction = "game.migrate",
        },
    };

    private static LuaHostParameterContract Parameter(string name, LuaHostTypeDescriptor type) =>
        new() { Name = name, Type = type };

    private static LuaAnalysisResult Analyze(string source, LuaHostAnalysisContract? hostContract)
    {
        var text = SourceText.FromUtf8(source);
        var lexing = LuaLexer.Lex(text);
        var semantics = LuaBinder.Bind(LuaParser.Parse(lexing));
        return LuaTypeAnalyzer.Analyze(
            semantics,
            LuaAnnotationParser.Parse(lexing),
            new LuaAnalysisEnvironment { HostContract = hostContract });
    }
}
