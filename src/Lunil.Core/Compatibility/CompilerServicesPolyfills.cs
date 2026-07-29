#if NETSTANDARD2_1
namespace System.Runtime.CompilerServices;

/// <summary>Supports init-only properties when consuming the portable Lunil assets.</summary>
internal static class IsExternalInit
{
}

/// <summary>Captures the source expression used for an argument.</summary>
[AttributeUsage(AttributeTargets.Parameter, AllowMultiple = false, Inherited = false)]
internal sealed class CallerArgumentExpressionAttribute(string parameterName) : Attribute
{
    public string ParameterName { get; } = parameterName;
}

/// <summary>Marks members that must be initialized by object construction.</summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct | AttributeTargets.Field | AttributeTargets.Property)]
internal sealed class RequiredMemberAttribute : Attribute
{
}

/// <summary>Identifies compiler features used by portable metadata.</summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = true, Inherited = false)]
internal sealed class CompilerFeatureRequiredAttribute(string featureName) : Attribute
{
    public const string RefStructs = nameof(RefStructs);

    public const string RequiredMembers = nameof(RequiredMembers);

    public string FeatureName { get; } = featureName;

    public bool IsOptional { get; init; }
}
#endif
