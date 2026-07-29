using System;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Lunil.Core;
using Lunil.Hosting;
using Lunil.Runtime.Values;

namespace Lunil.Gameplay.Fixture
{
    public static class SharedGameplayFixture
    {
        public const int TickCount = 100000;
        public const string ModuleName = "gameplay.rules";
        public const string ModulePath = "gameplay/rules.lua";
        public const string InitialRulesSource =
            "return {revision=1,apply=function(input,tick) return (input*3+tick%11)%97 end}";

        private const string PatchedRulesSource =
            "return {revision=2,apply=function(input,tick) return (input*5+tick%17)%101 end}";
        private const string RejectedRulesSource =
            "return {revision=3,apply=function(input,tick) return (input*7+tick%19)%103 end}";

        private const string InitializeSource =
            "fixture_game={x=0,v=0,score=0,timer=0,trace=17,revision=1," +
            "update_ticks=0,fixed_ticks=0,items={}}";

        private const string FixedSource =
            "for tick=1,100000 do " +
            "local input=fixture_input or 0;" +
            "fixture_game.v=(fixture_game.v+input+(tick%13))%10007;" +
            "fixture_game.fixed_ticks=tick;" +
            "fixture_game.trace=(fixture_game.trace*48271+fixture_game.v+tick)%2147483647;" +
            "if tick<100000 then coroutine.yield() end end;" +
            "return fixture_game.trace,fixture_game.v";

        private const string UpdateSource =
            "for tick=1,100000 do " +
            "local input=fixture_input or 0;local rules=require('gameplay.rules');" +
            "local delta=rules.apply(input,tick);" +
            "fixture_game.x=(fixture_game.x+fixture_game.v+delta)%1000003;" +
            "fixture_game.score=(fixture_game.score+delta*(1+(tick%7)))%10000019;" +
            "if tick%60==0 then fixture_game.timer=fixture_game.timer+1 end;" +
            "local slot=1+(tick%64);fixture_game.items[slot]=(fixture_game.items[slot] or 0)+delta;" +
            "fixture_game.revision=rules.revision;fixture_game.update_ticks=tick;" +
            "fixture_game.trace=(fixture_game.trace*48271+fixture_game.x+fixture_game.score+" +
            "fixture_game.timer+slot+rules.revision)%2147483647;" +
            "if tick<100000 then coroutine.yield() end end;" +
            "local collection=0;for i=1,64 do collection=collection+(fixture_game.items[i] or 0) end;" +
            "return fixture_game.trace,fixture_game.x,fixture_game.v,fixture_game.score," +
            "fixture_game.timer,collection,fixture_game.revision";

        private const string SnapshotSource =
            "local collection=0;for i=1,64 do collection=collection+(fixture_game.items[i] or 0) end;" +
            "return table.concat({fixture_game.trace,fixture_game.x,fixture_game.v," +
            "fixture_game.score,fixture_game.timer,collection,fixture_game.revision," +
            "fixture_game.update_ticks,fixture_game.fixed_ticks},':')";

        public static SharedGameplayResult Run(
            LuaGameLoopHost gameLoop,
            Func<bool, LuaGameLoopTickResult?> tick,
            string hostIdentity)
        {
#pragma warning disable CA1510 // Shared source targets the Unity 2022.3 API surface.
            if (gameLoop == null) throw new ArgumentNullException(nameof(gameLoop));
            if (tick == null) throw new ArgumentNullException(nameof(tick));
#pragma warning restore CA1510
            if (string.IsNullOrWhiteSpace(hostIdentity))
                throw new ArgumentException("A gameplay host identity is required.", nameof(hostIdentity));
            if (gameLoop.PersistentStore == null)
                throw new InvalidOperationException("The gameplay fixture requires a persistent store.");

            var initialized = gameLoop.Host.RunUtf8(InitializeSource, "=gameplay-initialize");
            Require(initialized.Succeeded, "The gameplay state did not initialize.");
            var fixedCompilation = gameLoop.Host.CompileUtf8(FixedSource, "=gameplay-fixed");
            var updateCompilation = gameLoop.Host.CompileUtf8(UpdateSource, "=gameplay-update");
            Require(fixedCompilation.Succeeded && updateCompilation.Succeeded,
                "The shared gameplay fixture did not compile.");

            var fixedOperation = gameLoop.Start(
                fixedCompilation,
                options: new LuaGameLoopStartOptions { Phase = LuaGameLoopPhase.FixedUpdate });
            var updateOperation = gameLoop.Start(updateCompilation);
            for (var index = 1; index <= TickCount; index++)
            {
                if (index == 25001)
                {
                    CommitPatch(gameLoop.Host, 1, 2, PatchedRulesSource);
                    Require(ReadRevision(gameLoop.Host) == 2,
                        "The shared gameplay patch revision was not published.");
                }
                else if (index == 75001)
                {
                    RejectPatchWithHealthRollback(gameLoop.Host);
                    Require(ReadRevision(gameLoop.Host) == 2,
                        "The shared gameplay health rollback did not restore revision 2.");
                }

                var input = DeterministicInput(index);
                gameLoop.Host.State.SetGlobal("fixture_input", LuaValue.FromInteger(input));
                var fixedTick = tick(true);
                var updateTick = tick(false);
                Require(fixedTick != null && fixedTick.Succeeded,
                    "A shared gameplay fixed tick failed: " +
                    DescribeFailure(fixedTick, fixedOperation));
                Require(updateTick != null && updateTick.Succeeded,
                    "A shared gameplay update tick failed: " +
                    DescribeFailure(updateTick, updateOperation));
            }

            Require(fixedOperation.Status == LuaGameLoopOperationStatus.Completed,
                "The shared gameplay fixed coroutine did not complete.");
            Require(updateOperation.Status == LuaGameLoopOperationStatus.Completed,
                "The shared gameplay update coroutine did not complete.");
            Require(gameLoop.ActiveOperationCount == 0 && gameLoop.PendingWorkCount == 0,
                "The shared gameplay fixture retained active work after completion.");

            var snapshotResult = gameLoop.Host.RunUtf8(SnapshotSource, "=gameplay-snapshot");
            Require(snapshotResult.Succeeded && snapshotResult.Execution != null,
                "The shared gameplay snapshot failed.");
            var snapshot = snapshotResult.Execution!.Values[0].AsString().ToString();
            var bytes = Encoding.UTF8.GetBytes(snapshot);
            var key = "gameplay/snapshot";
            gameLoop.PersistentStore.WriteAsync(key, bytes).AsTask().GetAwaiter().GetResult();
            var restored = gameLoop.PersistentStore.ReadAsync(key).AsTask().GetAwaiter().GetResult();
            Require(restored.Found && restored.Value.Span.SequenceEqual(bytes),
                "The shared gameplay persistent snapshot did not round-trip.");

            return new SharedGameplayResult(
                hostIdentity,
                snapshot,
                ComputeSha256(bytes),
                ReadRevision(gameLoop.Host),
                TickCount,
                gameLoop.ActiveOperationCount,
                gameLoop.PendingWorkCount);
        }

        private static int DeterministicInput(int tick)
        {
            var value = unchecked((uint)tick * 1103515245u + 12345u);
            return (int)((value >> 16) % 31u) - 15;
        }

        private static long ReadRevision(LuaHost host)
        {
            var result = host.RunUtf8(
                "return require('gameplay.rules').revision",
                "=gameplay-revision");
            Require(result.Succeeded && result.Execution != null,
                "The shared gameplay rule revision could not be read.");
            return result.Execution!.Values[0].AsInteger();
        }

        private static void CommitPatch(
            LuaHost host,
            int baseRevision,
            int targetRevision,
            string source)
        {
            var signer = new FixturePatchSigner();
            var prepared = host.PreparePatch(CreateBundle(
                signer, baseRevision, targetRevision, source));
            Require(prepared.Succeeded && prepared.PreparedPatch != null,
                "The shared gameplay patch could not be prepared: " + prepared.Message);
            var opened = host.TryOpenPatchUpdateWindow();
            Require(opened.Succeeded && opened.Window != null,
                "The shared gameplay patch window could not be opened: " + opened.Message);
            using (opened.Window)
            {
                var committed = host.CommitPatch(prepared.PreparedPatch!, opened.Window!);
                Require(committed.Succeeded,
                    "The shared gameplay patch could not be committed: " + committed.Message);
            }
        }

        private static void RejectPatchWithHealthRollback(LuaHost host)
        {
            var signer = new FixturePatchSigner();
            var prepared = host.PreparePatch(CreateBundle(signer, 2, 3, RejectedRulesSource));
            Require(prepared.Succeeded && prepared.PreparedPatch != null,
                "The rollback candidate could not be prepared: " + prepared.Message);
            var result = new LuaPatchCoordinator().CommitRing(
                "gameplay-health-rollback",
                new LuaPatchRolloutRing
                {
                    Name = "gameplay",
                    Targets = ImmutableArray.Create(new LuaPatchDeploymentTarget(
                        "gameplay-host", host, prepared.PreparedPatch!)),
                },
                new LuaPatchCoordinatorOptions
                {
                    HealthCheck = _ =>
                    {
                        Require(ReadRevision(host) == 3,
                            "The rollback health check did not observe revision 3.");
                        return LuaPatchRingHealthDecision.Rollback;
                    },
                });
            Require(result.Status == LuaPatchRingCommitStatus.HealthRejected,
                "The shared gameplay rollback candidate was not rejected by health policy.");
        }

        private static LuaPatchBundle CreateBundle(
            FixturePatchSigner signer,
            int baseRevision,
            int targetRevision,
            string source)
        {
            return LuaPatchBundle.Create(
                new LuaPatchManifest
                {
                    PatchId = "gameplay-patch-" + targetRevision,
                    Channel = "gameplay",
                    TargetBuild = "gameplay-" + targetRevision,
                    BaseRevision = "gameplay-" + baseRevision,
                    TargetRevision = "gameplay-" + targetRevision,
                    LanguageVersion = LuaLanguageVersion.Lua54,
                    RuntimeAbi = "lunil-0.12",
                    CreatedAt = new DateTimeOffset(2026, 7, 27, 0, 0, targetRevision, TimeSpan.Zero),
                    ExpiresAt = new DateTimeOffset(2099, 7, 27, 0, 0, 0, TimeSpan.Zero),
                    Nonce = "gameplay-patch-nonce-" + targetRevision,
                },
                new[]
                {
                    new LuaPatchEntry(
                        ModulePath,
                        ModuleName,
                        LuaPatchEntryKind.Source,
                        Encoding.UTF8.GetBytes(source)),
                },
                signer);
        }

        private static string ComputeSha256(byte[] value)
        {
#pragma warning disable CA1850 // SHA256.HashData is unavailable to Unity 2022.3 shared source.
            using (var sha256 = SHA256.Create())
            {
                var hash = sha256.ComputeHash(value);
                var builder = new StringBuilder(hash.Length * 2);
                for (var index = 0; index < hash.Length; index++)
                    builder.Append(hash[index].ToString("x2", CultureInfo.InvariantCulture));
                return builder.ToString();
            }
#pragma warning restore CA1850
        }

        private static void Require(bool condition, string message)
        {
            if (!condition) throw new InvalidOperationException(message);
        }

        private static string DescribeFailure(
            LuaGameLoopTickResult? result,
            LuaGameLoopOperation operation)
        {
            if (result == null) return "tick returned null";
            if (operation.Exception != null) return operation.Exception.ToString();
            var builder = new StringBuilder();
            foreach (var failure in result.Failures)
            {
                if (builder.Length != 0) builder.Append(" | ");
                builder.Append(failure.ToString());
            }
            return builder.Length == 0 ? "unknown failure" : builder.ToString();
        }

        private sealed class FixturePatchSigner : ILuaPatchSigner, ILuaPatchSignatureVerifier
        {
            private static readonly byte[] Key =
                Encoding.UTF8.GetBytes("lunil-shared-gameplay-signing-key");

            public string Algorithm { get { return "HMAC-SHA256-GAMEPLAY-FIXTURE"; } }
            public string KeyId { get { return "gameplay-fixture"; } }

            public byte[] SignDigest(ReadOnlySpan<byte> digest)
            {
                using (var hmac = new HMACSHA256(Key))
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
    }

    public sealed class SharedGameplayResult
    {
        public SharedGameplayResult(
            string hostIdentity,
            string snapshot,
            string traceSha256,
            long revision,
            int tickCount,
            int activeOperationCount,
            int pendingWorkCount)
        {
            HostIdentity = hostIdentity;
            Snapshot = snapshot;
            TraceSha256 = traceSha256;
            Revision = revision;
            TickCount = tickCount;
            ActiveOperationCount = activeOperationCount;
            PendingWorkCount = pendingWorkCount;
        }

        public string HostIdentity { get; private set; }
        public string Snapshot { get; private set; }
        public string TraceSha256 { get; private set; }
        public long Revision { get; private set; }
        public int TickCount { get; private set; }
        public int ActiveOperationCount { get; private set; }
        public int PendingWorkCount { get; private set; }

        public string ToMarker()
        {
            return "LUNIL_GAMEPLAY_TRACE host=" + HostIdentity +
                " ticks=" + TickCount +
                " revision=" + Revision +
                " trace=" + TraceSha256 +
                " snapshot=" + Snapshot +
                " active=" + ActiveOperationCount +
                " pending=" + PendingWorkCount;
        }
    }
}
