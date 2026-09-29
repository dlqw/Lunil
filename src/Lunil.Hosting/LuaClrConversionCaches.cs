using System.Collections.Concurrent;
using System.Globalization;
using System.Reflection;
using Lunil.Core;

namespace Lunil.Hosting;

internal static class LuaClrTaskResultDelegateFactory
{
    internal static Func<Task, object?> Create<TResult>() =>
        static task => ((Task<TResult>)task).Result;
}

internal static class LuaClrValueTaskDelegateFactory
{
    internal static Func<object, Task> Create<TResult>() =>
        static value => ((ValueTask<TResult>)value).AsTask();
}

internal static class LuaClrDefaultValueFactory
{
    internal static Func<object> Create<T>() => static () => (object)default(T)!;
}

/// <summary>
/// Per-bridge conversion caches. Closed-type delegates and shape metadata are computed once
/// per distinct type and then reused, so steady-state conversions take a lock-free dictionary
/// lookup plus a direct delegate call instead of runtime reflection lookups. Delegate
/// factories need dynamic code; runtimes without it keep the cached metadata path.
/// </summary>
public sealed partial class LuaClrBridge
{
    private readonly ConcurrentDictionary<Type, bool> _unsignedEnumTypes = new();

    private readonly ConcurrentDictionary<Type, Dictionary<string, object>> _enumNameTables = new();

    private readonly ConcurrentDictionary<Type, Type?> _arrayElementTypes = new();

    private readonly ConcurrentDictionary<Type, CollectionInterfaceShape> _collectionInterfaceShapes = new();

    private readonly ConcurrentDictionary<Type, TaskResultReader> _taskResultReaders = new();

    private readonly ConcurrentDictionary<Type, ValueTaskConversion> _valueTaskConversions = new();

    private readonly ConcurrentDictionary<Type, Func<object>> _defaultValueFactories = new();

    private readonly record struct CollectionInterfaceShape(Type? DictionaryInterface, Type? SequenceInterface);

    private readonly record struct TaskResultReader(Func<Task, object?>? Delegate, PropertyInfo? Property)
    {
        public object? Read(Task task) => Delegate is not null ? Delegate(task) : Property?.GetValue(task);
    }

    private readonly record struct ValueTaskConversion(
        Func<object, Task>? Delegate,
        MethodInfo? Method)
    {
        public bool CanConvert => Delegate is not null || Method is not null;

        public Task ToTask(object value) =>
            Delegate is not null ? Delegate(value) : (Task)Method!.Invoke(value, null)!;
    }

    private static bool DynamicDelegatesAvailable => LunilRuntimeFeature.IsDynamicCodeAvailable;

    /// <summary>
    /// Resolves the result reader for a task type, computing it once per distinct type. On
    /// runtimes with dynamic code the reader is a strongly typed delegate over
    /// <c>Task&lt;T&gt;.Result</c>; without it the rooted result metadata is cached per type
    /// instead. The reader returns null for tasks without a result.
    /// </summary>
    internal Func<Task, object?> GetTaskResultAccessor(Type taskType)
    {
        var reader = _taskResultReaders.GetOrAdd(taskType, CreateTaskResultReader);
        return reader.Read;
    }

    private static TaskResultReader CreateTaskResultReader(Type taskType)
    {
        var genericTask = FindGenericTaskType(taskType);
        if (genericTask is null)
        {
            return new TaskResultReader(static _ => null, null);
        }

        if (DynamicDelegatesAvailable)
        {
            var resultType = genericTask.GetGenericArguments()[0];
            var factory = typeof(LuaClrTaskResultDelegateFactory)
                .GetMethod(
                    nameof(LuaClrTaskResultDelegateFactory.Create),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(resultType);
            return new TaskResultReader(
                (Func<Task, object?>)factory.Invoke(null, null)!,
                null);
        }

        var property = genericTask.GetProperty(
            nameof(Task<int>.Result),
            BindingFlags.Public | BindingFlags.Instance);
        if (property is null)
        {
            // The closed Task<T> exists but its rooted result metadata is unavailable.
            return new TaskResultReader(
                static _ => throw new LuaClrException(
                    LuaClrErrorCode.AsyncFailed,
                    "The CLR task result metadata is unavailable."),
                null);
        }

        return new TaskResultReader(null, property);
    }

    private static Type? FindGenericTaskType(Type taskType)
    {
        for (var current = taskType; current is not null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition() == typeof(Task<>))
            {
                return current;
            }
        }

        return null;
    }

    /// <summary>
    /// Converts a ValueTask result to a Task. The non-generic struct converts through a
    /// direct cast; closed ValueTask&lt;T&gt; types build one strongly typed delegate per type
    /// when dynamic code is available and otherwise cache the AsTask metadata.
    /// </summary>
    private Task? ValueTaskAsTask(object value, Type valueType)
    {
        if (valueType == typeof(ValueTask))
        {
            return ((ValueTask)value).AsTask();
        }

        var conversion = _valueTaskConversions.GetOrAdd(valueType, CreateValueTaskConversion);
        return conversion.CanConvert ? conversion.ToTask(value) : null;
    }

    private static ValueTaskConversion CreateValueTaskConversion(Type valueType)
    {
        if (DynamicDelegatesAvailable &&
            valueType.IsGenericType &&
            valueType.GetGenericTypeDefinition() == typeof(ValueTask<>))
        {
            var resultType = valueType.GetGenericArguments()[0];
            var factory = typeof(LuaClrValueTaskDelegateFactory)
                .GetMethod(
                    nameof(LuaClrValueTaskDelegateFactory.Create),
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
                .MakeGenericMethod(resultType);
            return new ValueTaskConversion(
                (Func<object, Task>)factory.Invoke(null, null)!,
                null);
        }

        return new ValueTaskConversion(
            null,
            valueType.GetMethod("AsTask", BindingFlags.Public | BindingFlags.Instance));
    }

    /// <summary>
    /// Creates a default value-type instance for an out parameter. On runtimes with dynamic
    /// code a boxed-default factory is built once per type; otherwise Activator remains.
    /// </summary>
    private object CreateDefaultValueType(Type type) =>
        DynamicDelegatesAvailable
            ? _defaultValueFactories.GetOrAdd(type, CreateDefaultValueFactory)()
            : Activator.CreateInstance(type)!;

    private static Func<object> CreateDefaultValueFactory(Type type)
    {
        var factory = typeof(LuaClrDefaultValueFactory)
            .GetMethod(
                nameof(LuaClrDefaultValueFactory.Create),
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(type);
        return (Func<object>)factory.Invoke(null, null)!;
    }

    /// <summary>Reads the underlying integer of an enum, caching the sign of its type.</summary>
    private long EnumToInteger(Enum value)
    {
        var isUnsigned = _unsignedEnumTypes.GetOrAdd(
            value.GetType(),
            static type => Enum.GetUnderlyingType(type) == typeof(ulong));
        try
        {
            if (isUnsigned)
            {
                var unsigned = Convert.ToUInt64(value, CultureInfo.InvariantCulture);
                if (unsigned > long.MaxValue)
                {
                    throw new LuaClrException(LuaClrErrorCode.ConversionFailed,
                        "The CLR enum value exceeds the Lua integer range.");
                }
                return (long)unsigned;
            }
            return Convert.ToInt64(value, CultureInfo.InvariantCulture);
        }
        catch (OverflowException exception)
        {
            throw new LuaClrException(LuaClrErrorCode.ConversionFailed,
                "The CLR enum value exceeds the Lua integer range.", exception);
        }
    }

    /// <summary>Looks up an enum value by its exact declared name using a cached name table.</summary>
    private object? TryLookupEnumName(Type enumType, string name)
    {
        var table = _enumNameTables.GetOrAdd(enumType, BuildEnumNameTable);
        return table.TryGetValue(name, out var value) ? value : null;
    }

    private static Dictionary<string, object> BuildEnumNameTable(Type enumType)
    {
        var names = Enum.GetNames(enumType);
        var values = Enum.GetValues(enumType);
        var table = new Dictionary<string, object>(names.Length, StringComparer.Ordinal);
        for (var index = 0; index < names.Length; index++)
        {
            table.TryAdd(names[index], values.GetValue(index)!);
        }

        return table;
    }

    /// <summary>Resolves the element type of an array type once per distinct type.</summary>
    private Type? GetArrayElementType(Type arrayType) =>
        _arrayElementTypes.GetOrAdd(arrayType, static type => type.GetElementType());

    /// <summary>Resolves the dictionary and sequence interfaces of a collection target once
    /// per distinct type, preserving the IDictionary → IReadOnlyDictionary and
    /// IList → IReadOnlyList → IEnumerable preference order.</summary>
    private (Type? DictionaryInterface, Type? SequenceInterface) GetCollectionInterfaceShape(Type targetType)
    {
        var shape = _collectionInterfaceShapes.GetOrAdd(targetType, BuildCollectionInterfaceShape);
        return (shape.DictionaryInterface, shape.SequenceInterface);
    }

    private CollectionInterfaceShape BuildCollectionInterfaceShape(Type targetType) => new(
        FindGenericInterface(targetType, typeof(IDictionary<,>)) ??
            FindGenericInterface(targetType, typeof(IReadOnlyDictionary<,>)),
        FindGenericInterface(targetType, typeof(IList<>)) ??
            FindGenericInterface(targetType, typeof(IReadOnlyList<>)) ??
            FindGenericInterface(targetType, typeof(IEnumerable<>)));
}
