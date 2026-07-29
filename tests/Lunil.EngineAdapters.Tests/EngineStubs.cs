using System.Collections.Concurrent;

namespace UnityEngine
{
    [AttributeUsage(AttributeTargets.Field)]
    internal sealed class SerializeField : Attribute;

    public class ScriptableObject;

    internal static class Application
    {
        public static string persistentDataPath { get; set; } = Path.GetTempPath();
    }

    internal static class Time
    {
        public static double realtimeSinceStartupAsDouble { get; set; }
    }

    internal static class Debug
    {
        public static ConcurrentQueue<(bool Error, string Text)> Messages { get; } = new();

        public static void Log(object message) => Messages.Enqueue((false, message.ToString()!));

        public static void LogError(object message) => Messages.Enqueue((true, message.ToString()!));
    }
}

namespace Godot
{
    [AttributeUsage(AttributeTargets.Class)]
    internal sealed class GlobalClassAttribute : Attribute;

    [AttributeUsage(AttributeTargets.Property)]
    internal sealed class ExportAttribute : Attribute
    {
        public ExportAttribute() { }

        public ExportAttribute(PropertyHint hint) => _ = hint;
    }

    internal enum PropertyHint
    {
        MultilineText,
    }

    public class Resource
    {
        public string ResourcePath { get; set; } = string.Empty;
    }

    internal static class ResourceLoader
    {
        private static readonly ConcurrentDictionary<string, Resource> Resources = new(StringComparer.Ordinal);

        public static void Register(string path, Resource resource) => Resources[path] = resource;

        public static void Clear() => Resources.Clear();

        public static T? Load<T>(string path) where T : Resource =>
            Resources.TryGetValue(path, out var resource) ? resource as T : null;
    }

    internal static class ProjectSettings
    {
        public static string UserRoot { get; set; } = Path.GetTempPath();

        public static string GlobalizePath(string path) => path.StartsWith("user://", StringComparison.Ordinal)
            ? Path.Combine(UserRoot, path[7..])
            : path;
    }

    internal static class Time
    {
        public static ulong TicksUsec { get; set; }

        public static ulong GetTicksUsec() => TicksUsec;
    }

    internal static class GD
    {
        public static ConcurrentQueue<(bool Error, string Text)> Messages { get; } = new();

        public static void Print(object message) => Messages.Enqueue((false, message.ToString()!));

        public static void PushError(object message) => Messages.Enqueue((true, message.ToString()!));
    }
}
