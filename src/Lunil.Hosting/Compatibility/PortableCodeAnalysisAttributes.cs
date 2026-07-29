#if NETSTANDARD2_1
namespace Lunil.Hosting.Compatibility;

[Flags]
internal enum LunilDynamicallyAccessedMemberTypes
{
    None = 0,
    PublicProperties = 0x200,
}

[AttributeUsage(AttributeTargets.Constructor | AttributeTargets.Field | AttributeTargets.Method, AllowMultiple = true)]
internal sealed class LunilDynamicDependencyAttribute : Attribute
{
    public LunilDynamicDependencyAttribute(
        LunilDynamicallyAccessedMemberTypes memberTypes,
        Type type)
    {
    }

    public LunilDynamicDependencyAttribute(string memberSignature, Type type)
    {
    }
}

[AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
internal sealed class LunilUnconditionalSuppressMessageAttribute : Attribute
{
    public LunilUnconditionalSuppressMessageAttribute(string category, string checkId)
    {
        Category = category;
        CheckId = checkId;
    }

    public string Category { get; }

    public string CheckId { get; }

    public string? Justification { get; init; }
}
#endif
