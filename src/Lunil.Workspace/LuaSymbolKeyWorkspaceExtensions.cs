using Lunil.Compiler;
using Lunil.EmmyLua;
using Lunil.Semantics.Binding;

namespace Lunil.Workspace;

/// <summary>Adapts stable semantic keys to workspace module identities.</summary>
public static class LuaSymbolKeyWorkspaceExtensions
{
    /// <summary>Gets a symbol key using the module's logical identity.</summary>
    public static LuaSymbolKey GetSymbolKey(
        this LuaSemanticModel model,
        LuaSymbol symbol,
        LuaModuleIdentity module) =>
        model.GetSymbolKey(symbol, module.Name);

    /// <summary>Gets a function key using the module's logical identity.</summary>
    public static LuaSymbolKey GetFunctionKey(
        this LuaSemanticModel model,
        LuaFunctionInfo function,
        LuaModuleIdentity module) =>
        model.GetFunctionKey(function, module.Name);

    /// <summary>Resolves a symbol key using the module's logical identity.</summary>
    public static LuaSymbol? ResolveSymbolKey(
        this LuaSemanticModel model,
        LuaSymbolKey key,
        LuaModuleIdentity module) =>
        model.ResolveSymbolKey(key, module.Name);

    /// <summary>Resolves a function key using the module's logical identity.</summary>
    public static LuaFunctionInfo? ResolveFunctionKey(
        this LuaSemanticModel model,
        LuaSymbolKey key,
        LuaModuleIdentity module) =>
        model.ResolveFunctionKey(key, module.Name);

    /// <summary>Gets a class, alias, or enum annotation key using the module's logical identity.</summary>
    public static LuaSymbolKey GetAnnotationKey(
        this LuaCompilationResult compilation,
        LuaAnnotationSyntax annotation,
        LuaModuleIdentity module) =>
        compilation.GetAnnotationKey(annotation, module.Name);

    /// <summary>Resolves an annotation key using the module's logical identity.</summary>
    public static LuaAnnotationSyntax? ResolveAnnotationKey(
        this LuaCompilationResult compilation,
        LuaSymbolKey key,
        LuaModuleIdentity module) =>
        compilation.ResolveAnnotationKey(key, module.Name);
}
