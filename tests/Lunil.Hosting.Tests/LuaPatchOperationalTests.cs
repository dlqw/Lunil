using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using Lunil.Core;
using Lunil.StandardLibrary;

namespace Lunil.Hosting.Tests;

public sealed class LuaPatchOperationalTests
{
    [Fact]
    public void PrepareAndCoordinatorEnforceHardResourceLimitsBeforeExecution()
    {
        using var first = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
            ["mods/b.lua"] = "return {value=1}",
        });
        using var second = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
            ["mods/b.lua"] = "return {value=1}",
        });
        Load(first, "require('a'); require('b')");
        Load(second, "require('a'); require('b')");
        var bundle = CreateBundle(
            Entry("a", "return {value=2}"),
            Entry("b", "return {value=2}"));
        var oneModule = LuaPatchResourceLimits.Default with { MaximumPatchModules = 1 };

        var moduleLimit = Assert.Throws<LuaPatchResourceLimitException>(() =>
            first.PreparePatch(bundle, new LuaPatchPrepareOptions
            {
                ResourceLimits = oneModule,
            }));
        Assert.Equal(nameof(LuaPatchResourceLimits.MaximumPatchModules), moduleLimit.LimitName);

        var firstPrepared = first.PreparePatch(bundle);
        var secondPrepared = second.PreparePatch(bundle);
        Assert.True(firstPrepared.Succeeded, firstPrepared.Message);
        Assert.True(secondPrepared.Succeeded, secondPrepared.Message);
        var ring = new LuaPatchRolloutRing
        {
            Name = "production",
            Targets =
            [
                new("state-a", first, firstPrepared.PreparedPatch!),
                new("state-b", second, secondPrepared.PreparedPatch!),
            ],
        };
        var oneTarget = LuaPatchResourceLimits.Default with
        {
            MaximumTargetsPerRing = 1,
        };

        var targetLimit = Assert.Throws<LuaPatchResourceLimitException>(() =>
            new LuaPatchCoordinator().CommitRing(
                "limited",
                ring,
                new LuaPatchCoordinatorOptions { ResourceLimits = oneTarget }));
        Assert.Equal(nameof(LuaPatchResourceLimits.MaximumTargetsPerRing), targetLimit.LimitName);
        Assert.Equal(1, Value(first, "a"));
        Assert.Equal(1, Value(second, "a"));
    }

    [Fact]
    public void MigrationSchemaSerializationEnforcesByteAndRuleLimits()
    {
        var schema = new LuaPatchMigrationSchema
        {
            SchemaId = "game-state",
            BaseVersion = "1",
            TargetVersion = "2",
            Modules =
            [
                new LuaPatchModuleMigrationSchema
                {
                    ModuleName = "a",
                    State =
                    [
                        new LuaPatchStateRule
                        {
                            TargetPath = "/a",
                            Kind = LuaPatchStateRuleKind.Preserve,
                        },
                        new LuaPatchStateRule
                        {
                            TargetPath = "/b",
                            Kind = LuaPatchStateRuleKind.Preserve,
                        },
                    ],
                },
            ],
        };

        var ruleLimit = Assert.Throws<LuaPatchResourceLimitException>(() =>
            LuaPatchMigrationSchemaSerializer.Serialize(
                schema,
                LuaPatchResourceLimits.Default with { MaximumStateMigrationRules = 1 }));
        Assert.Equal(
            nameof(LuaPatchResourceLimits.MaximumStateMigrationRules),
            ruleLimit.LimitName);

        var byteLimit = Assert.Throws<LuaPatchResourceLimitException>(() =>
            LuaPatchMigrationSchemaSerializer.Serialize(
                schema,
                LuaPatchResourceLimits.Default with { MaximumMigrationSchemaBytes = 32 }));
        Assert.Equal(
            nameof(LuaPatchResourceLimits.MaximumMigrationSchemaBytes),
            byteLimit.LimitName);
    }

    [Fact]
    public void HotUpdatePublishesStableActivitiesAndLowCardinalityMetrics()
    {
        var stopped = new ConcurrentQueue<Activity>();
        using var activityListener = new ActivityListener
        {
            ShouldListenTo = static source =>
                source.Name == LuaPatchTelemetry.ActivitySourceName,
            Sample = static (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = stopped.Enqueue,
        };
        ActivitySource.AddActivityListener(activityListener);

        var measurements = new ConcurrentQueue<(string Name, string? Status)>();
        using var meterListener = new MeterListener
        {
            InstrumentPublished = static (instrument, listener) =>
            {
                if (instrument.Meter.Name == LuaPatchTelemetry.MeterName)
                {
                    listener.EnableMeasurementEvents(instrument);
                }
            },
        };
        meterListener.SetMeasurementEventCallback<long>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, Status(tags))));
        meterListener.SetMeasurementEventCallback<double>((instrument, _, tags, _) =>
            measurements.Enqueue((instrument.Name, Status(tags))));
        meterListener.Start();

        using var direct = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Load(direct, "require('a')");
        var directPrepared = direct.PreparePatch(CreateBundle(Entry("a", "return {value=2}")));
        Assert.True(directPrepared.Succeeded, directPrepared.Message);
        var opened = direct.TryOpenPatchUpdateWindow();
        Assert.True(opened.Succeeded, opened.Message);
        using (var window = opened.Window!)
        {
            Assert.True(direct.CommitPatch(directPrepared.PreparedPatch!, window).Succeeded);
        }

        using var coordinated = CreateHost(new Dictionary<string, string>
        {
            ["mods/a.lua"] = "return {value=1}",
        });
        Load(coordinated, "require('a')");
        var coordinatedPrepared = coordinated.PreparePatch(
            CreateBundle(Entry("a", "return {value=2}")));
        Assert.True(coordinatedPrepared.Succeeded, coordinatedPrepared.Message);
        var rollout = new LuaPatchCoordinator().Deploy(new LuaPatchRolloutPlan
        {
            RolloutId = "telemetry-rollout",
            Rings =
            [
                new LuaPatchRolloutRing
                {
                    Name = "canary",
                    Targets =
                    [
                        new(
                            "state-a",
                            coordinated,
                            coordinatedPrepared.PreparedPatch!),
                    ],
                },
            ],
        });
        Assert.True(rollout.Succeeded);

        Assert.Contains(stopped, static activity => activity.OperationName == "lunil.patch.prepare" &&
            Equals(activity.GetTagItem("lunil.patch.status"), "Ready") &&
            Equals(activity.GetTagItem("lunil.patch.id"), "operational-patch"));
        Assert.Contains(stopped, static activity => activity.OperationName == "lunil.patch.commit" &&
            activity.Status == ActivityStatusCode.Ok);
        Assert.Contains(stopped, static activity => activity.OperationName == "lunil.patch.ring" &&
            Equals(activity.GetTagItem("lunil.ring.name"), "canary"));
        Assert.Contains(stopped, static activity => activity.OperationName == "lunil.patch.rollout" &&
            Equals(activity.GetTagItem("lunil.rollout.id"), "telemetry-rollout"));
        Assert.Contains(measurements, static measurement =>
            measurement is ("lunil.patch.preparations", "Ready"));
        Assert.Contains(measurements, static measurement =>
            measurement is ("lunil.patch.commits", "Committed"));
        Assert.Contains(measurements, static measurement =>
            measurement is ("lunil.patch.rings", "Committed"));
    }

    private static string? Status(ReadOnlySpan<KeyValuePair<string, object?>> tags)
    {
        foreach (var tag in tags)
        {
            if (tag.Key == "lunil.patch.status")
            {
                return tag.Value?.ToString();
            }
        }

        return null;
    }

    private static LuaPatchEntry Entry(string moduleName, string source) => new(
        $"modules/{moduleName}.lua",
        moduleName,
        LuaPatchEntryKind.Source,
        Encoding.UTF8.GetBytes(source));

    private static LuaPatchBundle CreateBundle(params LuaPatchEntry[] entries)
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        return LuaPatchBundle.Create(
            new LuaPatchManifest
            {
                PatchId = "operational-patch",
                Channel = "test",
                TargetBuild = "build-2",
                BaseRevision = "build-1",
                TargetRevision = "build-2",
                LanguageVersion = LuaLanguageVersion.Lua54,
                RuntimeAbi = "lunil-0.12",
                CreatedAt = new DateTimeOffset(2026, 7, 22, 0, 0, 0, TimeSpan.Zero),
                ExpiresAt = new DateTimeOffset(2026, 8, 22, 0, 0, 0, TimeSpan.Zero),
                Nonce = "operational-test",
            },
            entries.ToImmutableArray(),
            new LuaPatchEcdsaSigner("test", key));
    }

    private static LuaHost CreateHost(IReadOnlyDictionary<string, string> files) => new(
        LuaHostOptions.Default with
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            StandardLibrary = LuaHostCapabilityProfiles.Create(LuaHostProfile.Restricted) with
            {
                FileSystem = new DictionaryFileSystem(files),
            },
        });

    private static void Load(LuaHost host, string source)
    {
        var result = host.RunUtf8("package.path='mods/?.lua'; " + source);
        Assert.True(result.Succeeded);
    }

    private static long Value(LuaHost host, string moduleName) => host.RunUtf8(
        $"return require('{moduleName}').value").Execution!.Values[0].AsInteger();

    private sealed class DictionaryFileSystem(IReadOnlyDictionary<string, string> files)
        : ILuaFileSystem
    {
        public byte[] ReadAllBytes(string path) => files.TryGetValue(path, out var source)
            ? Encoding.UTF8.GetBytes(source)
            : throw new FileNotFoundException(path);

        public bool FileExists(string path) => files.ContainsKey(path);
    }
}
