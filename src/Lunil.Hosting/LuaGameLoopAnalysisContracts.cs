using Lunil.Analysis;

namespace Lunil.Hosting;

/// <summary>Creates analysis contracts that mirror the game-loop persistence host interfaces.</summary>
public static class LuaGameLoopAnalysisContracts
{
    public static LuaHostAnalysisContract CreatePersistenceContract(
        string contractId,
        LuaGameLoopPersistenceSchema schema,
        LuaHostTypeDescriptor valueType,
        bool supportsDeleteAndClear = true,
        string globalName = "persistence")
    {
        LunilGuard.NotNull(schema);
        LunilGuard.NotNull(valueType);
        var key = new LuaHostParameterContract
        {
            Name = "key",
            Type = new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.String },
        };
        var value = new LuaHostParameterContract { Name = "value", Type = valueType };
        var builder = new LuaHostContractBuilder(contractId)
            .AddFunction(CreateFunction(
                globalName + ".read",
                LuaPersistenceOperationKind.Read,
                [key],
                [valueType],
                schema,
                valueType,
                keyIndex: 0,
                valueIndex: null))
            .AddFunction(CreateFunction(
                globalName + ".write",
                LuaPersistenceOperationKind.Write,
                [key, value],
                [],
                schema,
                valueType,
                keyIndex: 0,
                valueIndex: 1));
        if (supportsDeleteAndClear)
        {
            builder
                .AddFunction(CreateFunction(
                    globalName + ".delete",
                    LuaPersistenceOperationKind.Delete,
                    [key],
                    [new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Boolean }],
                    schema,
                    valueType,
                    keyIndex: 0,
                    valueIndex: null))
                .AddFunction(CreateFunction(
                    globalName + ".clear",
                    LuaPersistenceOperationKind.Clear,
                    [],
                    [],
                    schema,
                    valueType,
                    keyIndex: null,
                    valueIndex: null));
        }

        return builder.Build();
    }

    private static LuaHostFunctionContract CreateFunction(
        string path,
        LuaPersistenceOperationKind operation,
        System.Collections.Immutable.ImmutableArray<LuaHostParameterContract> parameters,
        System.Collections.Immutable.ImmutableArray<LuaHostTypeDescriptor> returns,
        LuaGameLoopPersistenceSchema schema,
        LuaHostTypeDescriptor valueType,
        int? keyIndex,
        int? valueIndex) => new()
    {
        Path = path,
        Parameters = parameters,
        Returns = returns,
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
            KeyParameterIndex = keyIndex,
            ValueParameterIndex = valueIndex,
            SchemaId = schema.SchemaId,
            SchemaVersion = schema.Version,
            ValueType = valueType,
            MissingReturnsNil = operation == LuaPersistenceOperationKind.Read,
            MigrationFunction = schema.MigrationFunction,
        },
        Source = new LuaHostSourceLocation
        {
            Uri = "dotnet://Lunil.Hosting/ILuaGameLoopPersistentStore#" + operation,
            ImplementationUri = "dotnet-implementation://host/ILuaGameLoopPersistentStore#" + operation,
        },
    };
}
