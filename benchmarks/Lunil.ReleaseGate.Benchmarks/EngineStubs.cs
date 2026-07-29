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
        public static T? Load<T>(string path) where T : Resource => null;
    }

    internal static class ProjectSettings
    {
        public static string GlobalizePath(string path) => Path.GetTempPath();
    }

    internal static class Time
    {
        public static ulong GetTicksUsec() => 0;
    }

    internal static class GD
    {
        public static void Print(object message) { }

        public static void PushError(object message) { }
    }
}
