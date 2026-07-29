using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;

internal static class LunilGuard
{
    public static void NotNull(
        [NotNull] object? argument,
        [CallerArgumentExpression(nameof(argument))] string? parameterName = null)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(parameterName);
        }
    }

    public static void NotNullOrEmpty(
        [NotNull] string? argument,
        [CallerArgumentExpression(nameof(argument))] string? parameterName = null)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (argument.Length == 0)
        {
            throw new ArgumentException("The value cannot be an empty string.", parameterName);
        }
    }

    public static void NotNullOrWhiteSpace(
        [NotNull] string? argument,
        [CallerArgumentExpression(nameof(argument))] string? parameterName = null)
    {
        if (argument is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (string.IsNullOrWhiteSpace(argument))
        {
            throw new ArgumentException(
                "The value cannot be an empty string or composed entirely of whitespace.",
                parameterName);
        }
    }

    public static void NotNegative<T>(
        T value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(default!) < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be non-negative.");
        }
    }

    public static void Positive<T>(
        T value,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(default!) <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "The value must be positive.");
        }
    }

    public static void LessThanOrEqual<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(other) > 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be less than or equal to {other}.");
        }
    }

    public static void LessThan<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(other) >= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be less than {other}.");
        }
    }

    public static void GreaterThan<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(other) <= 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be greater than {other}.");
        }
    }

    public static void GreaterThanOrEqual<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
        where T : IComparable<T>
    {
        if (value.CompareTo(other) < 0)
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value must be greater than or equal to {other}.");
        }
    }

    public static void NotEqual<T>(
        T value,
        T other,
        [CallerArgumentExpression(nameof(value))] string? parameterName = null)
    {
        if (EqualityComparer<T>.Default.Equals(value, other))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"The value cannot equal {other}.");
        }
    }

    public static void NotDisposed(bool condition, Type objectType)
    {
#if NETSTANDARD2_1
        if (condition)
        {
            throw new ObjectDisposedException(objectType.FullName);
        }
#else
        ObjectDisposedException.ThrowIf(condition, objectType);
#endif
    }

    public static void NotDisposed(bool condition, object instance)
    {
#if NETSTANDARD2_1
        if (condition)
        {
            throw new ObjectDisposedException(instance.GetType().FullName);
        }
#else
        ObjectDisposedException.ThrowIf(condition, instance);
#endif
    }
}
