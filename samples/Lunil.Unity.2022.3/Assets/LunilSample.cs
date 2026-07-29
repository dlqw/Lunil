using System;
using System.Text;
using Lunil.Hosting;
using Lunil.Unity;
using UnityEngine;

public sealed class LunilSample : MonoBehaviour
{
    private LuaUnityGameLoop _loop;

    [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
    private static void Create()
    {
        new GameObject("Lunil Sample").AddComponent<LunilSample>();
    }

    private void Awake()
    {
        var script = ScriptableObject.CreateInstance<LuaScriptAsset>();
        script.SetImportedData(
            Encoding.UTF8.GetBytes(
                "counter = 1; coroutine.yield(); counter = counter + 1; return counter"),
            "@Assets/main.lua",
            "main");
        _loop = gameObject.AddComponent<LuaUnityGameLoop>();
        _loop.StartOnEnable = false;
        _loop.EntryScript = script;
        _loop.TickCompleted += OnTickCompleted;
        _loop.Initialize();
    }

    private void OnTickCompleted(LuaGameLoopTickResult result)
    {
        if (!result.Succeeded)
        {
            throw new InvalidOperationException("The Lunil Unity sample tick failed.");
        }

        if (_loop.EntryOperation.Status == LuaGameLoopOperationStatus.Completed)
        {
            Debug.Log("Lunil Unity 2022.3 sample completed with " +
                _loop.EntryOperation.Values[0].AsInteger() + ".");
            enabled = false;
        }
    }
}
