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

    public string TypesLabel => IsChinese ? "类型" : "Types";

    public string LibraryMembers(int count) => IsChinese
        ? $"Lua 标准库 · {count} 个成员"
        : $"Lua standard library · {count} members";

    /// <summary>A short bilingual description of a primitive annotation type name.</summary>
    public string? PrimitiveTypeDescription(string name) => (IsChinese, name) switch
    {
        (false, "number") => "A Lua number: 64-bit float; integer values keep an integer subtype.",
        (false, "integer") or (false, "int") => "A 64-bit integer.",
        (false, "float") => "A 64-bit floating-point number.",
        (false, "string") or (false, "str") => "An immutable byte string.",
        (false, "boolean") or (false, "bool") => "true or false.",
        (false, "nil") => "The absence of a value.",
        (false, "any") => "Any Lua value; member checks are disabled.",
        (false, "unknown") => "A value whose type is not known yet; members are not checked.",
        (false, "never") or (false, "void") => "A value that never flows to this position.",
        (false, "table") => "A Lua table.",
        (false, "function") => "A function or callable value.",
        (false, "thread") => "A coroutine.",
        (false, "userdata") or (false, "lightuserdata") => "A value backed by host memory.",
        (false, "self") => "The receiver of the surrounding method.",
        (true, "number") => "Lua 数值：64 位浮点；整数值保留 integer 子类型。",
        (true, "integer") or (true, "int") => "64 位整数。",
        (true, "float") => "64 位浮点数。",
        (true, "string") or (true, "str") => "不可变字节串。",
        (true, "boolean") or (true, "bool") => "true 或 false。",
        (true, "nil") => "表示没有值。",
        (true, "any") => "任意 Lua 值；关闭成员检查。",
        (true, "unknown") => "类型未知的值；不检查成员。",
        (true, "never") or (true, "void") => "永远不会流动到该位置的值。",
        (true, "table") => "Lua 表。",
        (true, "function") => "函数或可调用值。",
        (true, "thread") => "协程。",
        (true, "userdata") or (true, "lightuserdata") => "由宿主内存支持的值。",
        (true, "self") => "所在方法的接收者。",
        _ => null,
    };

    public string ResolutionKindLabel(Lunil.Semantics.Binding.LuaNameResolutionKind kind) => kind switch
    {
        Lunil.Semantics.Binding.LuaNameResolutionKind.Local => IsChinese ? "局部变量" : "local",
        Lunil.Semantics.Binding.LuaNameResolutionKind.Upvalue => IsChinese ? "上值" : "upvalue",
        Lunil.Semantics.Binding.LuaNameResolutionKind.Global => IsChinese ? "全局变量" : "global",
        _ => kind.ToString(),
    };
}
