using System;
using System.IO;
using UnityEditor.AssetImporters;
using UnityEngine;

namespace Lunil.Unity.Editor
{
    [ScriptedImporter(1, "lua")]
    public sealed class LuaScriptImporter : ScriptedImporter
    {
        public override void OnImportAsset(AssetImportContext context)
        {
            var bytes = File.ReadAllBytes(context.assetPath);
            var asset = ScriptableObject.CreateInstance<LuaScriptAsset>();
            asset.SetImportedData(bytes, NormalizeAssetId(context.assetPath), CreateModuleName(context.assetPath));
            context.AddObjectToAsset("LuaScript", asset);
            context.SetMainObject(asset);
        }

        private static string NormalizeAssetId(string path)
        {
            return "@" + path.Replace('\\', '/');
        }

        private static string CreateModuleName(string path)
        {
            var normalized = path.Replace('\\', '/');
            var extension = Path.GetExtension(normalized);
            if (!string.IsNullOrEmpty(extension))
                normalized = normalized.Substring(0, normalized.Length - extension.Length);
            if (normalized.StartsWith("Assets/", StringComparison.Ordinal))
                normalized = normalized.Substring("Assets/".Length);
            return normalized.Replace('/', '.');
        }
    }
}
