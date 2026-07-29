using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Lunil.Core;
using Lunil.Gameplay.Fixture;
using Lunil.Hosting;
using Lunil.Runtime.Values;
using Lunil.Unity;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Lunil.Unity.Fixture
{
    public sealed class PlayerProbe : MonoBehaviour
    {
        public LuaUnityGameLoop Loop;
        public bool ComprehensiveIl2Cpp;
        private int _frames;
        private bool _comprehensiveCompleted;
        private Exception _failure;
        private bool _dispatcherPosted;
        private bool _backgroundDenied;
        private volatile bool _backgroundCompleted;
        private Exception _backgroundFailure;
        private SharedEngineSoakSession _soakSession;
        private bool _soakMode;

        private IEnumerator Start()
        {
            if (!ComprehensiveIl2Cpp)
            {
                try
                {
                    if (!Loop.IsInitialized) Loop.Initialize();
                    var soakSeconds = ReadDoubleArgument("-lunilSoakSeconds");
                    if (soakSeconds > 0.0)
                    {
                        var warmupSeconds = ReadDoubleArgument(
                            "-lunilSoakWarmupSeconds", Math.Min(soakSeconds / 3.0, 1800.0));
                        var sampleSeconds = ReadDoubleArgument(
                            "-lunilSoakSampleSeconds",
                            Math.Max(1.0, Math.Min(300.0, (soakSeconds - warmupSeconds) / 6.0)));
                        _soakSession = new SharedEngineSoakSession(
                            Loop.GameLoop,
                            Loop.TickUpdate,
                            "unity",
                            TimeSpan.FromSeconds(soakSeconds),
                            TimeSpan.FromSeconds(warmupSeconds),
                            TimeSpan.FromSeconds(sampleSeconds));
                        _soakMode = true;
                    }
                    else
                    {
                        var gameplay = SharedGameplayFixture.Run(
                            Loop.GameLoop,
                            fixedTick => fixedTick ? Loop.TickFixed() : Loop.TickUpdate(),
                            "unity");
                        PublishBrowserMarker(gameplay.ToMarker());
                    }
                }
                catch (Exception exception)
                {
                    _failure = exception;
                }
                yield break;
            }
            AsyncOperation unload = null;
            var hostBaseline = 0;
            try
            {
                PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_CONFIGURE");
                ConfigureAndInitialize();
                PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_BINDINGS");
                VerifyRuntimeAndBindings();
                PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_RESOURCES");
                BeginThreadAffinityVerification();
            }
            catch (Exception exception)
            {
                _failure = exception;
            }
            if (_failure != null) yield break;
            var backgroundDeadline = Time.realtimeSinceStartup + 30f;
            while (!_backgroundCompleted && Time.realtimeSinceStartup < backgroundDeadline)
                yield return null;
            try
            {
                if (!_backgroundCompleted)
                    throw new TimeoutException("Unity background dispatcher probe did not complete.");
                if (_backgroundFailure != null)
                    throw new InvalidOperationException(
                        "Unity background dispatcher probe failed.", _backgroundFailure);
                VerifyThreadAffinityAndResources();
                PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_PATCH");
                VerifySignedPatchAndStaleRejection();
                PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_SCENE_UNLOAD");
                unload = BeginSceneUnload(out hostBaseline);
            }
            catch (Exception exception)
            {
                _failure = exception;
            }
            if (_failure != null) yield break;
            while (!unload.isDone) yield return null;
            yield return null;
            try
            {
                VerifySceneUnloadCompleted(hostBaseline);
                _comprehensiveCompleted = true;
            }
            catch (Exception exception)
            {
                _failure = exception;
            }
        }

        private void Update()
        {
            if (_soakMode)
            {
                RunSoakFrame();
                return;
            }
            _frames++;
            if (_failure != null)
            {
                Debug.LogException(_failure);
                PublishBrowserMarker("LUNIL_UNITY_IL2CPP_FAILED_" +
                    _failure.GetType().Name + "_" + _failure.Message);
                Application.Quit(1);
                enabled = false;
                return;
            }
            if (_frames < 20 || (ComprehensiveIl2Cpp && !_comprehensiveCompleted)) return;
            try
            {
                var counter = Loop.GameLoop.Host.State.GetGlobal("counter");
                if (counter.AsInteger() != 2 ||
                    Loop.EntryOperation.Status != LuaGameLoopOperationStatus.Completed)
                    throw new System.InvalidOperationException("Unity player trace did not complete.");
                var marker = ComprehensiveIl2Cpp
                    ? "LUNIL_UNITY_IL2CPP_OK"
                    : "LUNIL_UNITY_PLAYER_OK";
                PublishBrowserMarker(marker);
                Application.Quit(0);
                enabled = false;
            }
            catch (System.Exception exception)
            {
                Debug.LogException(exception);
                Application.Quit(1);
            }
        }

        private void RunSoakFrame()
        {
            try
            {
                var result = _soakSession.Tick();
                if (result == null) return;
                PublishBrowserMarker(result.ToMarker());
                Loop.Shutdown();
                Application.Quit(0);
                enabled = false;
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                PublishBrowserMarker("LUNIL_ENGINE_SOAK_FAILED host=unity " +
                    exception.GetType().Name + ": " + exception.Message);
                Application.Quit(1);
                enabled = false;
            }
        }

        private static double ReadDoubleArgument(string name, double defaultValue = 0.0)
        {
            var arguments = Environment.GetCommandLineArgs();
            for (var index = 0; index + 1 < arguments.Length; index++)
            {
                if (string.Equals(arguments[index], name, StringComparison.Ordinal))
                    return double.Parse(arguments[index + 1],
                        System.Globalization.CultureInfo.InvariantCulture);
            }
            return defaultValue;
        }

        private void ConfigureAndInitialize()
        {
            var registry = new LuaClrBindingRegistry();
            var providerType = typeof(UnityBindingTarget).Assembly.GetType(
                "Lunil.Generated.LuaClrGeneratedBindings", true);
            var provider = (ILuaClrBindingProvider)Activator.CreateInstance(providerType);
            provider.RegisterBindings(registry);
            var targetType = typeof(UnityBindingTarget);
            var signalType = typeof(UnitySignalHandler);
            var functionType = typeof(Func<int, int>);
            var listType = typeof(List<int>);
            var targetName = targetType.FullName;
            Loop.ConfigureHostOptions = options => options with
            {
                HostOptions = options.HostOptions with
                {
                    ExecutionBackend = LuaHostExecutionBackend.Interpreter,
                    Clr = new LuaClrOptions
                    {
                        Capabilities = LuaClrCapabilities.TypeDiscovery |
                            LuaClrCapabilities.Construction |
                            LuaClrCapabilities.MemberAccess |
                            LuaClrCapabilities.DelegateConversion |
                            LuaClrCapabilities.EventSubscription,
                        AllowedAssemblyNames = ImmutableArray.Create(
                            targetType.Assembly.GetName().Name,
                            functionType.Assembly.GetName().Name,
                            listType.Assembly.GetName().Name),
                        AllowedTypeNames = ImmutableArray.Create(
                            targetName, signalType.FullName, functionType.FullName, listType.FullName),
                        AllowedMemberNames = ImmutableArray.Create(
                            targetName + "." + nameof(UnityBindingTarget.Value),
                            targetName + "." + nameof(UnityBindingTarget.Add),
                            targetName + "." + nameof(UnityBindingTarget.Raise),
                            targetName + "." + nameof(UnityBindingTarget.Changed)),
                        AllowedDelegateTypeNames = ImmutableArray.Create(
                            signalType.FullName, functionType.FullName),
                        AllowedEventNames = ImmutableArray.Create(
                            targetName + "." + nameof(UnityBindingTarget.Changed)),
                        BindingRegistry = registry,
                        BindingMode = LuaClrBindingMode.RegistryOnly,
                        InstallGlobalModule = true
                    }
                }
            };
            Loop.Initialize();
        }

        private void VerifyRuntimeAndBindings()
        {
            var host = Loop.GameLoop.Host;
            if (RuntimeFeature.IsDynamicCodeSupported || host.IsDynamicCodeAvailable)
                throw new InvalidOperationException("IL2CPP unexpectedly reported dynamic-code support.");
            if (host.SelectedExecutionBackend != LuaHostExecutionBackend.Interpreter)
                throw new InvalidOperationException("IL2CPP did not select the interpreter backend.");
            PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_BINDING_MEMBER");

            var targetName = typeof(UnityBindingTarget).FullName;
            var target = LuaValue.FromUserdata(host.ClrBridge.CreateInstance(
                targetName, new[] { LuaValue.FromInteger(40) }));
            var added = host.ClrBridge.InvokeMember(
                target,
                nameof(UnityBindingTarget.Add),
                new[] { LuaValue.FromInteger(20), LuaValue.FromInteger(22) });
            if (added.ReturnValue.AsInteger() != 42)
                throw new InvalidOperationException("Generated member binding returned an invalid result.");
            PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_BINDING_DELEGATE");

            var function = host.RunUtf8("return function(value) return value+1 end")
                .Execution.Values[0];
            var callback = (Func<int, int>)host.ClrBridge.CreateDelegate(
                function, typeof(Func<int, int>).FullName);
            if (callback(41) != 42)
                throw new InvalidOperationException("Generated delegate binding returned an invalid result.");
            PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_BINDING_EVENT");

            var eventCallback = host.RunUtf8("return function(value) unityEventValue=value end")
                .Execution.Values[0];
            using (host.ClrBridge.Subscribe(target, nameof(UnityBindingTarget.Changed), eventCallback))
            {
                host.ClrBridge.InvokeMember(
                    target,
                    nameof(UnityBindingTarget.Raise),
                    new[] { LuaValue.FromInteger(42) });
                Loop.TickUpdate();
                if (host.State.GetGlobal("unityEventValue").AsInteger() != 42)
                    throw new InvalidOperationException("Generated event binding did not invoke Lua.");
            }
            PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_BINDING_GENERIC");

            var genericName = "System.Collections.Generic.List" + (char)96 + "1";
            var resolved = host.ClrBridge.ResolveClosedGeneric(
                genericName, ImmutableArray.Create("System.Int32"));
            if (!string.Equals(resolved, typeof(List<int>).FullName, StringComparison.Ordinal))
                throw new InvalidOperationException("Generated closed-generic binding was not resolved.");
            PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_BINDING_GENERIC_REJECT");
            try
            {
                host.ClrBridge.ResolveClosedGeneric(genericName, ImmutableArray.Create("System.String"));
                throw new InvalidOperationException("An unregistered closed generic was accepted.");
            }
            catch (LuaClrException exception)
            {
                if (exception.Code != LuaClrErrorCode.TypeNotAllowed) throw;
            }
            PublishBrowserMarker("LUNIL_UNITY_IL2CPP_STAGE_BINDING_DONE");
        }

        private void BeginThreadAffinityVerification()
        {
            var dispatcher = Loop.GameLoop.Options.Dispatcher;
#if UNITY_WEBGL && !UNITY_EDITOR
            _backgroundDenied = dispatcher.CheckAccess();
            dispatcher.Post(() => _dispatcherPosted = true);
            _backgroundCompleted = true;
#else
            var thread = new Thread(() =>
            {
                try
                {
                    _backgroundDenied = !dispatcher.CheckAccess();
                    dispatcher.Post(() => _dispatcherPosted = true);
                }
                catch (Exception exception)
                {
                    _backgroundFailure = exception;
                }
                finally
                {
                    _backgroundCompleted = true;
                }
            });
            thread.IsBackground = true;
            thread.Start();
#endif
        }

        private void VerifyThreadAffinityAndResources()
        {
            Loop.TickUpdate();
            if (!_backgroundDenied || !_dispatcherPosted)
                throw new InvalidOperationException("Unity dispatcher thread affinity is invalid.");

            var asset = Loop.GameLoop.Options.AssetResolver.ResolveAsync(Loop.EntryScript.AssetId)
                .AsTask().GetAwaiter().GetResult();
            if (!asset.Found || asset.Value.Length == 0)
                throw new InvalidOperationException("Unity resource resolver lost the entry asset.");
            Debug.Log("LUNIL_UNITY_RESOURCE_TRACE asset=" + Loop.EntryScript.AssetId +
                " bytes=" + asset.Value.Length + " hosts=" + LuaUnityRuntimeRegistry.ActiveHostCount);
        }

        private void VerifySignedPatchAndStaleRejection()
        {
            var host = Loop.GameLoop.Host;
            var initial = host.RunUtf8("return require('patchable').value");
            if (!initial.Succeeded || initial.Execution.Values[0].AsInteger() != 1)
                throw new InvalidOperationException("The initial Unity module revision is invalid.");

            var signer = new HmacPatchSigner(
                "unity-il2cpp-fixture",
                Encoding.UTF8.GetBytes("unity-il2cpp-fixture-signing-key"));
            var bundle = LuaPatchBundle.Create(
                new LuaPatchManifest
                {
                    PatchId = "unity-il2cpp-signed-patch",
                    Channel = "unity-fixture",
                    TargetBuild = "unity-il2cpp-2",
                    BaseRevision = "unity-il2cpp-1",
                    TargetRevision = "unity-il2cpp-2",
                    LanguageVersion = LuaLanguageVersion.Lua54,
                    RuntimeAbi = "lunil-0.12",
                    CreatedAt = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
                    ExpiresAt = new DateTimeOffset(2099, 7, 26, 0, 0, 0, TimeSpan.Zero),
                    Nonce = "unity-il2cpp-signed-patch-nonce"
                },
                new[]
                {
                    new LuaPatchEntry(
                        "modules/patchable.lua",
                        "patchable",
                        LuaPatchEntryKind.Source,
                        Encoding.UTF8.GetBytes("return {value=2}"))
                },
                signer);
            using (var stream = new MemoryStream())
            {
                bundle.Write(stream);
                stream.Position = 0;
                bundle = LuaPatchBundle.Read(stream, signer);
            }

            var prepared = host.PreparePatch(bundle);
            if (!prepared.Succeeded)
                throw new InvalidOperationException("Signed Unity patch preparation failed: " + prepared.Message);
            var opened = host.TryOpenPatchUpdateWindow();
            if (!opened.Succeeded)
                throw new InvalidOperationException("Unity patch update window failed: " + opened.Message);
            using (opened.Window)
            {
                var committed = host.CommitPatch(prepared.PreparedPatch, opened.Window);
                if (!committed.Succeeded)
                    throw new InvalidOperationException("Signed Unity patch commit failed: " + committed.Message);
            }
            var updated = host.RunUtf8("return require('patchable').value");
            if (!updated.Succeeded || updated.Execution.Values[0].AsInteger() != 2)
                throw new InvalidOperationException("The signed Unity patch was not published.");
            var staleWindow = host.TryOpenPatchUpdateWindow();
            if (!staleWindow.Succeeded)
                throw new InvalidOperationException(
                    "Could not open a second Unity patch window: " + staleWindow.Message);
            using (staleWindow.Window)
            {
                var staleCommit = host.CommitPatch(prepared.PreparedPatch, staleWindow.Window);
                if (staleCommit.Succeeded)
                    throw new InvalidOperationException("A stale Unity patch generation was committed.");
            }
        }

        private AsyncOperation BeginSceneUnload(out int baseline)
        {
            baseline = LuaUnityRuntimeRegistry.ActiveHostCount;
            var scene = SceneManager.CreateScene("LunilIl2CppUnloadFixture");
            var temporaryObject = new GameObject("LunilTemporaryHost");
            temporaryObject.SetActive(false);
            var temporaryLoop = temporaryObject.AddComponent<LuaUnityGameLoop>();
            temporaryLoop.StartOnEnable = false;
            SceneManager.MoveGameObjectToScene(temporaryObject, scene);
            temporaryObject.SetActive(true);
            temporaryLoop.Initialize();
            if (LuaUnityRuntimeRegistry.ActiveHostCount != baseline + 1)
                throw new InvalidOperationException("The additive Unity scene host was not registered.");
            var unload = SceneManager.UnloadSceneAsync(scene);
            if (unload == null) throw new InvalidOperationException("Unity refused additive scene unload.");
            return unload;
        }

        private static void VerifySceneUnloadCompleted(int baseline)
        {
            if (LuaUnityRuntimeRegistry.ActiveHostCount != baseline)
                throw new InvalidOperationException("Scene unload did not dispose its Unity host.");
        }

        private sealed class HmacPatchSigner : ILuaPatchSigner, ILuaPatchSignatureVerifier
        {
            private readonly byte[] _key;

            public HmacPatchSigner(string keyId, byte[] key)
            {
                KeyId = keyId;
                _key = key;
            }

            public string Algorithm { get { return "HMAC-SHA256-UNITY-IL2CPP-FIXTURE"; } }
            public string KeyId { get; private set; }

            public byte[] SignDigest(ReadOnlySpan<byte> digest)
            {
                using (var hmac = new HMACSHA256(_key))
                    return hmac.ComputeHash(digest.ToArray());
            }

            public bool IsTrusted(string algorithm, string keyId)
            {
                return string.Equals(algorithm, Algorithm, StringComparison.Ordinal) &&
                    string.Equals(keyId, KeyId, StringComparison.Ordinal);
            }

            public bool VerifyDigest(
                string algorithm,
                string keyId,
                ReadOnlySpan<byte> digest,
                ReadOnlySpan<byte> signature)
            {
                if (!IsTrusted(algorithm, keyId)) return false;
                var expected = SignDigest(digest);
                if (expected.Length != signature.Length) return false;
                var difference = 0;
                for (var index = 0; index < expected.Length; index++)
                    difference |= expected[index] ^ signature[index];
                return difference == 0;
            }
        }

        private static void PublishBrowserMarker(string marker)
        {
            Debug.Log(marker);
#if UNITY_WEBGL && !UNITY_EDITOR
            LunilSetProbeMarker(marker);
#endif
        }

#if UNITY_WEBGL && !UNITY_EDITOR
        [DllImport("__Internal")]
        private static extern void LunilSetProbeMarker(string marker);
#endif
    }
}
