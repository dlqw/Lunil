using Lunil.Runtime.Values;

namespace Lunil.Hosting.Tests;

public sealed class LuaClrInteropRegressionTests
{
    [Fact]
    public void TypeQualifiedMemberAllowlistDoesNotExposeTheSameNameOnAnotherType()
    {
        var firstType = typeof(AllowlistFirst).FullName!;
        var secondType = typeof(AllowlistSecond).FullName!;
        using var host = CreateHost(
            LuaClrCapabilities.Construction | LuaClrCapabilities.MemberAccess,
            [firstType, secondType],
            [$"{firstType}.Value"]);

        var first = LuaValue.FromUserdata(host.ClrBridge.CreateInstance(firstType));
        var second = LuaValue.FromUserdata(host.ClrBridge.CreateInstance(secondType));

        Assert.Equal(1, host.ClrBridge.GetMember(first, "Value").AsInteger());
        var denied = Assert.Throws<LuaClrException>(() =>
            host.ClrBridge.GetMember(second, "Value"));
        Assert.Equal(LuaClrErrorCode.MemberNotAllowed, denied.Code);
    }

    [Fact]
    public void MemberCacheLimitFailsExplicitlyInsteadOfSilentlyDroppingCandidates()
    {
        var typeName = typeof(CacheBoundary).FullName!;
        using var host = CreateHost(
            LuaClrCapabilities.Construction | LuaClrCapabilities.MemberAccess,
            [typeName],
            [$"{typeName}.First", $"{typeName}.Second"],
            maximumCachedMembers: 1);
        var target = LuaValue.FromUserdata(host.ClrBridge.CreateInstance(typeName));

        var exception = Assert.Throws<LuaClrException>(() =>
            host.ClrBridge.GetMember(target, "First"));

        Assert.Equal(LuaClrErrorCode.MemberNotFound, exception.Code);
    }

    [Fact]
    public void UInt64OutsideTheLuaIntegerRangeFailsWithAStableCode()
    {
        var typeName = typeof(ConversionBoundary).FullName!;
        using var host = CreateHost(
            LuaClrCapabilities.MemberAccess,
            [typeName],
            [$"{typeName}.LargeUInt64"]);

        var exception = Assert.Throws<LuaClrException>(() =>
            host.ClrBridge.InvokeStatic(typeName, "LargeUInt64"));

        Assert.Equal(LuaClrErrorCode.InvocationFailed, exception.Code);
    }

    [Fact]
    public void RectangularAndNonZeroLowerBoundArraysBecomeOneBasedNestedTables()
    {
        var typeName = typeof(ConversionBoundary).FullName!;
        using var host = CreateHost(
            LuaClrCapabilities.MemberAccess,
            [typeName],
            [$"{typeName}.Rectangular", $"{typeName}.NonZeroLowerBound"],
            installGlobalModule: true);

        var result = host.RunUtf8(
            $"local a=clr.call('{typeName}','Rectangular'); " +
            $"local b=clr.call('{typeName}','NonZeroLowerBound'); " +
            "return a[1][1],a[2][3],b[1],b[2]");

        Assert.True(result.Succeeded, result.Execution?.ToString());
        Assert.Equal([1L, 6L, 41L, 42L],
            result.Execution!.Values.Select(static value => value.AsInteger()));
    }

    [Fact]
    public void AwaitRejectsAnIncompleteTaskUnderASynchronizationContext()
    {
        var typeName = typeof(ConversionBoundary).FullName!;
        using var host = CreateHost(
            LuaClrCapabilities.MemberAccess | LuaClrCapabilities.Async,
            [typeName],
            [$"{typeName}.PendingAsync"]);
        var task = host.ClrBridge.InvokeStatic(typeName, "PendingAsync").ReturnValue;
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new SynchronizationContext());
        try
        {
            var exception = Assert.Throws<LuaClrException>(() => host.ClrBridge.Await(task));
            Assert.Equal(LuaClrErrorCode.AsyncFailed, exception.Code);
            Assert.Contains("synchronization context", exception.Message, StringComparison.Ordinal);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    [Fact]
    public async Task AnyThreadWhenIdleUsesAtomicStateAdmission()
    {
        var delegateName = typeof(Func<int, int>).FullName!;
        using var host = new LuaHost(new LuaHostOptions
        {
            InstallStandardLibrary = false,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.DelegateConversion,
                AllowedAssemblyNames = [typeof(Func<int, int>).Assembly.GetName().Name!],
                AllowedTypeNames = [delegateName],
                AllowedDelegateTypeNames = [delegateName],
                ThreadPolicy = LuaClrThreadPolicy.AnyThreadWhenIdle,
            },
        });
        var function = host.RunUtf8("return function(value) return value+1 end").Execution!.Values[0];
        var callback = (Func<int, int>)host.ClrBridge.CreateDelegate(function, delegateName);

        Assert.Equal(42, await Task.Run(() => callback(41)));

        Monitor.Enter(host.State.ExecutionGate);
        try
        {
            Exception? error = null;
            var thread = new Thread(() => error = Record.Exception(() => callback(41)));
            thread.Start();
            Assert.True(thread.Join(TimeSpan.FromSeconds(5)));
            var denied = Assert.IsType<LuaClrException>(error);
            Assert.Equal(LuaClrErrorCode.ThreadDenied, denied.Code);
        }
        finally
        {
            Monitor.Exit(host.State.ExecutionGate);
        }
    }

    private static LuaHost CreateHost(
        LuaClrCapabilities capabilities,
        string[] typeNames,
        string[] memberNames,
        int maximumCachedMembers = 256,
        bool installGlobalModule = false) =>
        new(new LuaHostOptions
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Clr = new LuaClrOptions
            {
                Capabilities = capabilities,
                AllowedAssemblyNames = [typeof(LuaClrInteropRegressionTests).Assembly.GetName().Name!],
                AllowedTypeNames = [.. typeNames],
                AllowedMemberNames = [.. memberNames],
                MaximumCachedMembers = maximumCachedMembers,
                InstallGlobalModule = installGlobalModule,
            },
        });

    public sealed class AllowlistFirst
    {
        public int Value { get; } = 1;
    }

    public sealed class AllowlistSecond
    {
        public int Value { get; } = 2;
    }

    public sealed class CacheBoundary
    {
        public int First { get; } = 1;

        public int Second { get; } = 2;
    }

    public static class ConversionBoundary
    {
        public static ulong LargeUInt64() => ulong.MaxValue;

        public static int[,] Rectangular() => new[,]
        {
            { 1, 2, 3 },
            { 4, 5, 6 },
        };

        public static Array NonZeroLowerBound()
        {
            var result = Array.CreateInstance(typeof(int), [2], [5]);
            result.SetValue(41, 5);
            result.SetValue(42, 6);
            return result;
        }

        public static Task<int> PendingAsync() =>
            new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously).Task;
    }
}
