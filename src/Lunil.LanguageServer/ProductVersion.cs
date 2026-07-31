using System.Reflection;

namespace Lunil.LanguageServer;

internal static class ProductVersion
{
    public static string Current => typeof(ProductVersion).Assembly
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        .Split('+', 2)[0] ?? "0.0.0";
}
