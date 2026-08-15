namespace Lunil.LanguageServer;

public enum LunilLocale : byte
{
    English,
    SimplifiedChinese,
}

/// <summary>
/// User-facing server strings (hover cards, signature help, index status). English is
/// the fallback; Simplified Chinese is selected with the <c>lunil.locale</c> setting or
/// the <c>locale</c> initialization option, and applies without a server restart.
/// </summary>
internal sealed class ServerLocalization
{
    private volatile LunilLocale _locale;

    public LunilLocale Locale
    {
        get => _locale;
        set => _locale = value;
    }

    /// <summary>
    /// Parses a client locale tag (<c>en</c>, <c>zh-CN</c>, <c>zh_Hans</c>, ...);
    /// unknown tags fall back to English.
    /// </summary>
    public static bool TryParse(string? tag, out LunilLocale locale)
    {
        locale = LunilLocale.English;
        if (string.IsNullOrWhiteSpace(tag) || string.Equals(tag, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var normalized = tag.Replace("_", "-", StringComparison.Ordinal);
        if (normalized.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
        {
            locale = LunilLocale.SimplifiedChinese;
            return true;
        }

        if (normalized.StartsWith("en", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private bool IsChinese => _locale == LunilLocale.SimplifiedChinese;

    public string ModuleLabel => IsChinese ? "模块" : "Module";

    public string ExtendsLabel => IsChinese ? "继承" : "Extends";

    public string MembersLabel => IsChinese ? "成员" : "Members";

    public string InheritedFrom(string className) => IsChinese ? $"继承自 {className}" : $"Inherited from {className}";

    public string MoreMembers(int count) => IsChinese ? $"还有 {count} 个" : $"+{count} more";

    public string BuiltinLibraryLabel => IsChinese ? "Lua 内建库" : "Lua builtin library";

    public string DeclarationLabel => IsChinese ? "声明" : "declaration";

    public string CapturedUpvalueSuffix => IsChinese ? " · 捕获上值" : " · captured upvalue";

    public string SignatureHelpDocumentation => IsChinese ? "由 Lunil 流分析推断。" : "Inferred by Lunil flow analysis.";

    public string ResolutionKindLabel(Lunil.Semantics.Binding.LuaNameResolutionKind kind) => kind switch
    {
        Lunil.Semantics.Binding.LuaNameResolutionKind.Local => IsChinese ? "局部变量" : "local",
        Lunil.Semantics.Binding.LuaNameResolutionKind.Upvalue => IsChinese ? "上值" : "upvalue",
        Lunil.Semantics.Binding.LuaNameResolutionKind.Global => IsChinese ? "全局变量" : "global",
        _ => kind.ToString(),
    };
}
