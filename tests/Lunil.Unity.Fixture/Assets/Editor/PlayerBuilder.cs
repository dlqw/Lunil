using System;
using System.IO;
using Lunil.Gameplay.Fixture;
using Lunil.Unity.Fixture;
using UnityEditor;
using UnityEditor.Build.Reporting;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace Lunil.Unity.Fixture.Editor
{
    public static class PlayerBuilder
    {
        public static void Build()
        {
            var output = GetPathArgument("-lunilPlayerOutput");
            if (string.IsNullOrWhiteSpace(output))
                throw new ArgumentException("-lunilPlayerOutput is required.");

            BuildFixture(
                output,
                BuildTarget.StandaloneWindows64,
                BuildTargetGroup.Standalone,
                ScriptingImplementation.Mono2x,
                false);
        }

        public static void BuildIl2Cpp()
        {
            var output = GetPathArgument("-lunilPlayerOutput");
            if (string.IsNullOrWhiteSpace(output))
                throw new ArgumentException("-lunilPlayerOutput is required.");
            var requestedTarget = GetArgument("-lunilBuildTarget");
            if (string.IsNullOrWhiteSpace(requestedTarget))
                throw new ArgumentException("-lunilBuildTarget is required.");

            BuildTarget target;
            BuildTargetGroup group;
            switch (requestedTarget.ToLowerInvariant())
            {
                case "windows":
                    target = BuildTarget.StandaloneWindows64;
                    group = BuildTargetGroup.Standalone;
                    break;
                case "android":
                    target = BuildTarget.Android;
                    group = BuildTargetGroup.Android;
                    break;
                case "ios":
                    target = BuildTarget.iOS;
                    group = BuildTargetGroup.iOS;
                    break;
                case "webgl":
                    target = BuildTarget.WebGL;
                    group = BuildTargetGroup.WebGL;
                    break;
                default:
                    throw new ArgumentException("Unknown -lunilBuildTarget value: " + requestedTarget);
            }

            BuildFixture(output, target, group, ScriptingImplementation.IL2CPP, true);
        }

        private static void BuildFixture(
            string output,
            BuildTarget target,
            BuildTargetGroup group,
            ScriptingImplementation backend,
            bool comprehensiveIl2Cpp)
        {

            const string generatedDirectory = "Assets/Generated";
            Directory.CreateDirectory(generatedDirectory);
            var entry = ScriptableObject.CreateInstance<LuaScriptAsset>();
            entry.SetImportedData(
                System.Text.Encoding.UTF8.GetBytes(
                    "counter=(counter or 0)+1;coroutine.yield();counter=counter+1;return counter"),
                "@Assets/Generated/player.lua",
                "Generated.player");
            const string entryPath = generatedDirectory + "/PlayerEntry.asset";
            AssetDatabase.DeleteAsset(entryPath);
            AssetDatabase.CreateAsset(entry, entryPath);

            var patchable = ScriptableObject.CreateInstance<LuaScriptAsset>();
            patchable.SetImportedData(
                System.Text.Encoding.UTF8.GetBytes("return {value=1}"),
                "@Assets/Generated/patchable.lua",
                "patchable");
            const string patchablePath = generatedDirectory + "/Patchable.asset";
            AssetDatabase.DeleteAsset(patchablePath);
            AssetDatabase.CreateAsset(patchable, patchablePath);

            var gameplayRules = ScriptableObject.CreateInstance<LuaScriptAsset>();
            gameplayRules.SetImportedData(
                System.Text.Encoding.UTF8.GetBytes(SharedGameplayFixture.InitialRulesSource),
                "@gameplay/rules.lua",
                SharedGameplayFixture.ModuleName);
            const string gameplayRulesPath = generatedDirectory + "/GameplayRules.asset";
            AssetDatabase.DeleteAsset(gameplayRulesPath);
            AssetDatabase.CreateAsset(gameplayRules, gameplayRulesPath);

            var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
            var hostObject = new GameObject("LunilHost");
            var loop = hostObject.AddComponent<LuaUnityGameLoop>();
            loop.EntryScript = entry;
            loop.Modules = new[] { patchable, gameplayRules };
            loop.StartOnEnable = !comprehensiveIl2Cpp;
            var probe = hostObject.AddComponent<PlayerProbe>();
            probe.Loop = loop;
            probe.ComprehensiveIl2Cpp = comprehensiveIl2Cpp;
            if (comprehensiveIl2Cpp)
            {
                const string linkerPath = generatedDirectory + "/link.xml";
                File.WriteAllText(linkerPath,
                    "<linker>\n" +
                    "  <assembly fullname=\"Lunil.Unity.Fixture\">\n" +
                    "    <type fullname=\"Lunil.Generated.LuaClrGeneratedBindings\" preserve=\"all\" />\n" +
                    "  </assembly>\n" +
                    "</linker>\n",
                    new System.Text.UTF8Encoding(false));
                AssetDatabase.ImportAsset(linkerPath, ImportAssetOptions.ForceUpdate);
            }
            const string scenePath = generatedDirectory + "/PlayerFixture.unity";
            EditorSceneManager.SaveScene(scene, scenePath);

            var parent = Path.GetDirectoryName(output);
            if (!string.IsNullOrWhiteSpace(parent)) Directory.CreateDirectory(parent);
            if (!EditorUserBuildSettings.SwitchActiveBuildTarget(group, target))
                throw new InvalidOperationException("Could not switch Unity build target to " + target + ".");
            PlayerSettings.SetScriptingBackend(group, backend);
            if (comprehensiveIl2Cpp)
            {
                PlayerSettings.SetManagedStrippingLevel(group, ManagedStrippingLevel.High);
                PlayerSettings.stripEngineCode = true;
                PlayerSettings.SetApplicationIdentifier(group, "com.dlqw.lunil.fixture");
                ConfigurePlatform(target);
            }
            var report = BuildPipeline.BuildPlayer(new BuildPlayerOptions
            {
                scenes = new[] { scenePath },
                locationPathName = output,
                target = target,
                options = BuildOptions.Development
            });
            if (report.summary.result != BuildResult.Succeeded)
                throw new InvalidOperationException("Unity player build failed: " + report.summary.result);
            Debug.Log(comprehensiveIl2Cpp
                ? "LUNIL_UNITY_IL2CPP_BUILD_OK target=" + target
                : "LUNIL_UNITY_BUILD_OK");
        }

        private static void ConfigurePlatform(BuildTarget target)
        {
            if (target == BuildTarget.Android)
            {
                EditorUserBuildSettings.buildAppBundle = false;
                PlayerSettings.Android.targetArchitectures = AndroidArchitecture.X86_64;
                PlayerSettings.Android.minSdkVersion = AndroidSdkVersions.AndroidApiLevel26;
#if UNITY_6000_0_OR_NEWER
                // GameActivity loses native input focus under Unity's headless Android emulator
                // gate before the first scene frame. Activity exercises the same IL2CPP player
                // and is deterministic both locally and in CI.
                PlayerSettings.Android.applicationEntry = AndroidApplicationEntry.Activity;
#endif
            }
            else if (target == BuildTarget.WebGL)
            {
                PlayerSettings.WebGL.compressionFormat = WebGLCompressionFormat.Disabled;
                PlayerSettings.WebGL.exceptionSupport =
                    WebGLExceptionSupport.ExplicitlyThrownExceptionsOnly;
            }
        }

        private static string GetArgument(string name)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
                if (string.Equals(arguments[index], name, StringComparison.Ordinal))
                    return arguments[index + 1];
            return null;
        }

        private static string GetPathArgument(string name)
        {
            var value = GetArgument(name);
            return value == null ? null : Path.GetFullPath(value);
        }
    }
}
