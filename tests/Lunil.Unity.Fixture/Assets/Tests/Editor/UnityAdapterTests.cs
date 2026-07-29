using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Lunil.Core;
using Lunil.Gameplay.Fixture;
using Lunil.Hosting;
using Lunil.Unity;
using Lunil.Unity.Fixture;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace Lunil.Unity.Fixture.Tests
{
    public sealed class UnityAdapterTests
    {
        private const string TemporaryRoot = "Assets/LunilUnityFixtureGenerated";

        [TearDown]
        public void TearDown()
        {
            LuaUnityRuntimeRegistry.DisposeAll();
            AssetDatabase.DeleteAsset(TemporaryRoot);
        }

        [Test]
        public void ScriptedImporterPreservesBinaryBytesAndIdentity()
        {
            Directory.CreateDirectory(TemporaryRoot);
            var path = TemporaryRoot + "/binary.lua";
            var expected = new byte[] { 0x61, 0x3d, 0x31, 0x00, 0xff };
            File.WriteAllBytes(path, expected);
            AssetDatabase.ImportAsset(path, ImportAssetOptions.ForceSynchronousImport);

            var asset = AssetDatabase.LoadAssetAtPath<LuaScriptAsset>(path);
            Assert.That(asset, Is.Not.Null);
            Assert.That(asset.Bytes.ToArray(), Is.EqualTo(expected));
            Assert.That(asset.AssetId, Is.EqualTo("@" + path));
            Assert.That(asset.ModuleName, Is.EqualTo("LunilUnityFixtureGenerated.binary"));
        }

        [Test]
        public void UpdateFixedUpdateDispatcherAndLifecycleAreDeterministic()
        {
            for (var iteration = 0; iteration < 32; iteration++)
            {
                var entry = CreateScript("entry" + iteration,
                    "counter=(counter or 0)+1;coroutine.yield();counter=counter+1;return counter");
                var gameObject = new GameObject("LunilFixture");
                gameObject.SetActive(false);
                var component = gameObject.AddComponent<LuaUnityGameLoop>();
                component.StartOnEnable = false;
                component.EntryScript = entry;
                gameObject.SetActive(true);
                component.Initialize();

                var posted = false;
                Task.Run(() => component.GameLoop.Options.Dispatcher.Post(() => posted = true)).Wait();
                var first = component.TickUpdate();
                var second = component.TickUpdate();
                Assert.That(posted, Is.True);
                Assert.That(first.SuspendedOperationCount, Is.EqualTo(1));
                Assert.That(second.CompletedOperationCount, Is.EqualTo(1));
                Assert.That(component.GameLoop.Host.State.GetGlobal("counter").AsInteger(), Is.EqualTo(2));

                var fixedCompilation = component.GameLoop.Host.CompileUtf8("fixedValue=41+1");
                var fixedOperation = component.GameLoop.Start(fixedCompilation, options: new LuaGameLoopStartOptions
                {
                    Phase = LuaGameLoopPhase.FixedUpdate
                });
                component.TickFixed();
                Assert.That(fixedOperation.Status, Is.EqualTo(LuaGameLoopOperationStatus.Completed));
                Assert.That(component.GameLoop.Host.State.GetGlobal("fixedValue").AsInteger(), Is.EqualTo(42));

                component.Shutdown();
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(entry);
                Assert.That(LuaUnityRuntimeRegistry.ActiveHostCount, Is.Zero);
            }
        }

        [Test]
        public void AssetResolverAndPersistentStorePreserveEmptyAndMissingValues()
        {
            var asset = CreateScript("module", string.Empty);
            var resolver = new LuaUnityAssetResolver(new[] { asset });
            var found = resolver.ResolveAsync(asset.AssetId).AsTask().GetAwaiter().GetResult();
            var missing = resolver.ResolveAsync("@missing").AsTask().GetAwaiter().GetResult();
            Assert.That(found.Found, Is.True);
            Assert.That(found.Value.Length, Is.Zero);
            Assert.That(missing.Found, Is.False);

            var store = new LuaUnityPersistentStore("LunilFixture-" + Guid.NewGuid().ToString("N"));
            store.WriteAsync("slot/一", new ReadOnlyMemory<byte>(new byte[] { 1, 2, 3 }))
                .AsTask().GetAwaiter().GetResult();
            var stored = store.ReadAsync("slot/一").AsTask().GetAwaiter().GetResult();
            Assert.That(stored.Value.ToArray(), Is.EqualTo(new byte[] { 1, 2, 3 }));
            UnityEngine.Object.DestroyImmediate(asset);
        }

        [Test]
        public void PrecompiledBindingProviderRegistersAndInvokesUnityTarget()
        {
            var providerType = typeof(UnityBindingTarget).Assembly.GetType(
                "Lunil.Generated.LuaClrGeneratedBindings",
                throwOnError: true);
            var provider = (ILuaClrBindingProvider)Activator.CreateInstance(providerType);
            var registry = new LuaClrBindingRegistry();
            provider.RegisterBindings(registry);

            Assert.That(registry.TryGet(typeof(UnityBindingTarget).FullName, out var binding), Is.True);
            var add = binding.Members.Single(member => member.Name == nameof(UnityBindingTarget.Add));
            var result = add.Invoker(new UnityBindingTarget(0), new object[] { 20, 22 });
            Assert.That(result, Is.EqualTo(42));
        }

        [Test]
        public void SignedPatchCommitsThroughUnityHostedModuleResolver()
        {
            var entry = CreateScript("entry-patch", string.Empty);
            var module = CreateScript("patchable", "return {value=1}");
            var gameObject = new GameObject("LunilPatchFixture");
            gameObject.SetActive(false);
            var component = gameObject.AddComponent<LuaUnityGameLoop>();
            component.StartOnEnable = false;
            component.EntryScript = entry;
            component.Modules = new[] { module };
            gameObject.SetActive(true);

            try
            {
                component.Initialize();
                var initial = component.GameLoop.Host.RunUtf8("return require('patchable').value");
                Assert.That(initial.Succeeded, Is.True);
                Assert.That(initial.Execution.Values[0].AsInteger(), Is.EqualTo(1));

                var signer = new HmacPatchSigner(
                    "unity-fixture",
                    Encoding.UTF8.GetBytes("unity-fixture-signing-key"));
                var bundle = LuaPatchBundle.Create(
                    new LuaPatchManifest
                    {
                        PatchId = "unity-signed-patch",
                        Channel = "unity-fixture",
                        TargetBuild = "unity-test-2",
                        BaseRevision = "unity-test-1",
                        TargetRevision = "unity-test-2",
                        LanguageVersion = LuaLanguageVersion.Lua54,
                        RuntimeAbi = "lunil-0.12",
                        CreatedAt = new DateTimeOffset(2026, 7, 26, 0, 0, 0, TimeSpan.Zero),
                        ExpiresAt = new DateTimeOffset(2099, 7, 26, 0, 0, 0, TimeSpan.Zero),
                        Nonce = "unity-signed-patch-nonce"
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
                    var prepared = component.GameLoop.Host.PreparePatch(bundle);
                    Assert.That(prepared.Succeeded, Is.True, prepared.Message);
                    var opened = component.GameLoop.Host.TryOpenPatchUpdateWindow();
                    Assert.That(opened.Succeeded, Is.True, opened.Message);
                    using (opened.Window)
                    {
                        var committed = component.GameLoop.Host.CommitPatch(
                            prepared.PreparedPatch,
                            opened.Window);
                        Assert.That(committed.Succeeded, Is.True, committed.Message);
                    }
                }

                var updated = component.GameLoop.Host.RunUtf8("return require('patchable').value");
                Assert.That(updated.Succeeded, Is.True);
                Assert.That(updated.Execution.Values[0].AsInteger(), Is.EqualTo(2));
            }
            finally
            {
                component.Shutdown();
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(entry);
                UnityEngine.Object.DestroyImmediate(module);
            }
        }

        [Test]
        public void SharedGameplayTraceMatchesThePortableReferenceForOneHundredThousandTicks()
        {
            var gameplayRules = CreateScript(
                "gameplay-rules",
                SharedGameplayFixture.InitialRulesSource,
                "@gameplay/rules.lua",
                SharedGameplayFixture.ModuleName);
            var gameObject = new GameObject("LunilSharedGameplayFixture");
            gameObject.SetActive(false);
            var component = gameObject.AddComponent<LuaUnityGameLoop>();
            component.StartOnEnable = false;
            component.Modules = new[] { gameplayRules };
            gameObject.SetActive(true);

            try
            {
                component.Initialize();
                var result = SharedGameplayFixture.Run(
                    component.GameLoop,
                    fixedTick => fixedTick ? component.TickFixed() : component.TickUpdate(),
                    "unity");
                Debug.Log(result.ToMarker());

                Assert.That(result.TickCount, Is.EqualTo(100000));
                Assert.That(result.Revision, Is.EqualTo(2));
                Assert.That(result.TraceSha256,
                    Is.EqualTo("5dca24ae91fa6dc36374459305f4bbdd3d596dc6a4dd40764c3cb5f951300d05"));
                Assert.That(result.Snapshot,
                    Is.EqualTo("1760139754:156633:9457:616802:1666:5153828:2:100000:100000"));
                Assert.That(result.ActiveOperationCount, Is.Zero);
                Assert.That(result.PendingWorkCount, Is.Zero);
            }
            finally
            {
                component.Shutdown();
                UnityEngine.Object.DestroyImmediate(gameObject);
                UnityEngine.Object.DestroyImmediate(gameplayRules);
            }

            Assert.That(LuaUnityRuntimeRegistry.ActiveHostCount, Is.Zero);
        }

        [UnityTest]
        public IEnumerator DisabledDomainReloadDisposesHostsAcrossRepeatedPlaySessions()
        {
            var previousEnabled = EditorSettings.enterPlayModeOptionsEnabled;
            var previousOptions = EditorSettings.enterPlayModeOptions;
            EditorSettings.enterPlayModeOptionsEnabled = true;
            EditorSettings.enterPlayModeOptions =
                EnterPlayModeOptions.DisableDomainReload |
                EnterPlayModeOptions.DisableSceneReload;
            try
            {
                for (var iteration = 0; iteration < 2; iteration++)
                {
                    yield return new EnterPlayMode();
                    var entry = CreateScript("domain-reload-" + iteration, "return 42");
                    var gameObject = new GameObject("LunilDisabledDomainReloadFixture");
                    gameObject.SetActive(false);
                    var component = gameObject.AddComponent<LuaUnityGameLoop>();
                    component.StartOnEnable = false;
                    component.EntryScript = entry;
                    gameObject.SetActive(true);
                    component.Initialize();
                    Assert.That(LuaUnityRuntimeRegistry.ActiveHostCount, Is.EqualTo(1));

                    yield return new ExitPlayMode();
                    Assert.That(LuaUnityRuntimeRegistry.ActiveHostCount, Is.Zero);
                    UnityEngine.Object.DestroyImmediate(gameObject);
                    UnityEngine.Object.DestroyImmediate(entry);
                }
            }
            finally
            {
                LuaUnityRuntimeRegistry.DisposeAll();
                EditorSettings.enterPlayModeOptions = previousOptions;
                EditorSettings.enterPlayModeOptionsEnabled = previousEnabled;
            }
        }

        private sealed class HmacPatchSigner : ILuaPatchSigner, ILuaPatchSignatureVerifier
        {
            private readonly byte[] _key;

            public HmacPatchSigner(string keyId, byte[] key)
            {
                KeyId = keyId;
                _key = key;
            }

            public string Algorithm { get { return "HMAC-SHA256-UNITY-FIXTURE"; } }
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

        private static LuaScriptAsset CreateScript(string name, string source)
        {
            return CreateScript(name, source, "@fixture/" + name + ".lua", name);
        }

        private static LuaScriptAsset CreateScript(
            string name,
            string source,
            string assetId,
            string moduleName)
        {
            var asset = ScriptableObject.CreateInstance<LuaScriptAsset>();
            asset.name = name;
            asset.SetImportedData(Encoding.UTF8.GetBytes(source), assetId, moduleName);
            return asset;
        }
    }
}
