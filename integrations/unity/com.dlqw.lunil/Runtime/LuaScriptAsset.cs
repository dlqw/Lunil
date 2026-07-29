using System;
using UnityEngine;

namespace Lunil.Unity
{
    /// <summary>Binary-safe imported Lua source and its stable Unity asset identity.</summary>
    public sealed class LuaScriptAsset : ScriptableObject
    {
        [SerializeField] private byte[] _bytes = new byte[0];
        [SerializeField] private string _assetId = string.Empty;
        [SerializeField] private string _moduleName = string.Empty;

        public ReadOnlyMemory<byte> Bytes
        {
            get { return new ReadOnlyMemory<byte>(_bytes ?? new byte[0]); }
        }

        public string AssetId
        {
            get { return _assetId ?? string.Empty; }
        }

        public string ModuleName
        {
            get { return _moduleName ?? string.Empty; }
        }

        public void SetImportedData(byte[] bytes, string assetId, string moduleName)
        {
            if (bytes == null) throw new ArgumentNullException(nameof(bytes));
            if (string.IsNullOrWhiteSpace(assetId))
                throw new ArgumentException("An asset identity is required.", nameof(assetId));
            if (string.IsNullOrWhiteSpace(moduleName))
                throw new ArgumentException("A module name is required.", nameof(moduleName));

            _bytes = (byte[])bytes.Clone();
            _assetId = assetId;
            _moduleName = moduleName;
        }
    }
}
