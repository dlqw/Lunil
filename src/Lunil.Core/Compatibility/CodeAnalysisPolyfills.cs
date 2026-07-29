#if NETSTANDARD2_1
namespace System.Diagnostics.CodeAnalysis;

/// <summary>Marks constructors that initialize every required member.</summary>
[AttributeUsage(AttributeTargets.Constructor, AllowMultiple = false, Inherited = false)]
internal sealed class SetsRequiredMembersAttribute : Attribute
{
}
#endif
