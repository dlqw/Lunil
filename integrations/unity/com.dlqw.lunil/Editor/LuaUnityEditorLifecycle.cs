using UnityEditor;

namespace Lunil.Unity.Editor
{
    [InitializeOnLoad]
    internal static class LuaUnityEditorLifecycle
    {
        static LuaUnityEditorLifecycle()
        {
            AssemblyReloadEvents.beforeAssemblyReload += LuaUnityRuntimeRegistry.DisposeAll;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.ExitingPlayMode ||
                change == PlayModeStateChange.ExitingEditMode)
                LuaUnityRuntimeRegistry.DisposeAll();
        }
    }
}
