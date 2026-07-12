using System.Collections.Concurrent;
using System.Collections.Frozen;
using Lunil.CodeGen.Cil.Emission;
using Lunil.IR.Canonical;
using Lunil.Runtime;
using Lunil.Runtime.Execution;

namespace Lunil.CodeGen.Cil;

public sealed class LuaStaticAotModule
{
    private readonly FrozenDictionary<int, LuaCompiledMethod> _functions;

    public LuaStaticAotModule(
        string moduleName,
        string moduleContentId,
        LuaIrModule canonicalModule,
        IReadOnlyDictionary<int, LuaCompiledMethod> functions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleContentId);
        ArgumentNullException.ThrowIfNull(canonicalModule);
        ArgumentNullException.ThrowIfNull(functions);
        if (functions.Count == 0)
        {
            throw new ArgumentException("A static AOT module must contain an entry function.", nameof(functions));
        }

        ModuleName = moduleName;
        ModuleContentId = moduleContentId;
        CanonicalModule = canonicalModule;
        if (!string.Equals(
            LuaAotModuleIdentity.ComputeContentId(canonicalModule),
            moduleContentId,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "The canonical module does not match the registered content ID.",
                nameof(canonicalModule));
        }

        _functions = functions.ToFrozenDictionary();
    }

    public string ModuleName { get; }

    public string ModuleContentId { get; }

    public LuaIrModule CanonicalModule { get; }

    public IReadOnlyDictionary<int, LuaCompiledMethod> Functions => _functions;

    public bool TryGetFunction(int functionId, out LuaCompiledMethod? function) =>
        _functions.TryGetValue(functionId, out function);

    public LuaClosure CreateMainClosure(LuaState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.CreateMainClosure(CanonicalModule);
    }
}

public static class LuaStaticAotRegistry
{
    private static readonly ConcurrentDictionary<string, LuaStaticAotModule> ModulesByContentId =
        new(StringComparer.Ordinal);
    private static readonly ConcurrentDictionary<string, string> ContentIdsByName =
        new(StringComparer.Ordinal);

    public static void Register(LuaStaticAotModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        if (!ModulesByContentId.TryAdd(module.ModuleContentId, module) &&
            !ReferenceEquals(ModulesByContentId[module.ModuleContentId], module))
        {
            throw new InvalidOperationException(
                $"Static AOT module content '{module.ModuleContentId}' is already registered.");
        }

        if (!ContentIdsByName.TryAdd(module.ModuleName, module.ModuleContentId) &&
            !string.Equals(
                ContentIdsByName[module.ModuleName],
                module.ModuleContentId,
                StringComparison.Ordinal))
        {
            ModulesByContentId.TryRemove(module.ModuleContentId, out _);
            throw new InvalidOperationException(
                $"Static AOT module name '{module.ModuleName}' maps to different content.");
        }
    }

    public static bool TryGetModule(string moduleName, out LuaStaticAotModule? module)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        if (ContentIdsByName.TryGetValue(moduleName, out var contentId))
        {
            return ModulesByContentId.TryGetValue(contentId, out module);
        }

        module = null;
        return false;
    }

    public static bool TryGetModule(LuaIrModule module, out LuaStaticAotModule? compiledModule)
    {
        ArgumentNullException.ThrowIfNull(module);
        return ModulesByContentId.TryGetValue(
            LuaAotModuleIdentity.ComputeContentId(module),
            out compiledModule);
    }

    public static bool TryGetFunction(
        LuaIrModule module,
        int functionId,
        out LuaCompiledMethod? function)
    {
        if (TryGetModule(module, out var compiledModule) && compiledModule is not null)
        {
            return compiledModule.TryGetFunction(functionId, out function);
        }

        function = null;
        return false;
    }
}
