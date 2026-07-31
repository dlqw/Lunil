using System.Collections;
using Lunil.Analysis;
using Lunil.Runtime.Values;

[assembly: Lunil.Hosting.LuaClrGenerateBinding(
    typeof(Lunil.Hosting.Tests.AotBindingFixture),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.Value),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.Add),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.Update),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.Changed),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetMode),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.EchoMode),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetDecimal),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetWholeDecimal),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.EchoDecimal),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetList),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetDictionary),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetEnumerable),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetTuple),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetValueTuple),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetCycle),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetThrowingList),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetThrowingDictionary),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetThrowingEnumerable),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.GetThrowingTuple),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.CountList),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.SumDictionary),
    nameof(Lunil.Hosting.Tests.AotBindingFixture.EchoTuple))]
[assembly: Lunil.Hosting.LuaClrGenerateBinding(typeof(Lunil.Hosting.Tests.AotSignalHandler))]
[assembly: Lunil.Hosting.LuaClrGenerateBinding(typeof(List<int>))]
[assembly: Lunil.Hosting.LuaClrGenerateBinding(typeof(Tuple<string, int>))]
[assembly: Lunil.Hosting.LuaClrGenerateBinding(typeof(Dictionary<string, int>))]

namespace Lunil.Hosting.Tests;

public sealed class LuaClrAotBindingTests
{
    [Fact]
    public void GeneratedRegistryProducesAStableReflectionFreeAnalysisContract()
    {
        var registry = CreateRegistry();

        var contract = registry.CreateAnalysisContract("generated-fixture");
        var roundTrip = LuaHostAnalysisContract.ParseJson(contract.ToJson());
        var add = Assert.Single(roundTrip.Functions.Values, static function =>
            function.Path.EndsWith(".Add", StringComparison.Ordinal));
        var changed = Assert.Single(roundTrip.Functions.Values, static function =>
            function.Path.EndsWith(".Changed", StringComparison.Ordinal));

        Assert.Equal(LuaHostTypeKind.Integer, add.Returns[0].Kind);
        Assert.StartsWith("dotnet://", add.Source!.Uri, StringComparison.Ordinal);
        Assert.StartsWith("dotnet-implementation://", add.Source.ImplementationUri!, StringComparison.Ordinal);
        Assert.NotNull(changed.Callback);
        Assert.Equal(LuaHostCallbackRetentionKind.Stored, changed.Callback!.Retention);
        Assert.Contains("function clr.", roundTrip.ToLuaStub(), StringComparison.Ordinal);
    }

    [Fact]
    public void GameLoopPersistenceV2ContractIncludesReadWriteDeleteClearAndSchema()
    {
        var contract = LuaGameLoopAnalysisContracts.CreatePersistenceContract(
            "game-save",
            new LuaGameLoopPersistenceSchema("save", 3, "persistence.migrate"),
            new LuaHostTypeDescriptor { Kind = LuaHostTypeKind.Table });

        Assert.Equal(4, contract.Functions.Count);
        Assert.Equal(LuaPersistenceOperationKind.Read,
            contract.Functions["persistence.read"].Persistence!.Operation);
        Assert.True(contract.Functions["persistence.read"].Persistence!.MissingReturnsNil);
        Assert.Equal(LuaPersistenceOperationKind.Delete,
            contract.Functions["persistence.delete"].Persistence!.Operation);
        Assert.Equal(LuaPersistenceOperationKind.Clear,
            contract.Functions["persistence.clear"].Persistence!.Operation);
        Assert.All(contract.Functions.Values, static function =>
            Assert.Equal(3, function.Persistence!.SchemaVersion));
    }

    [Fact]
    public void RegistryOnlyBindingsInvokeMembersDelegatesAndNamedRefOut()
    {
        var registry = CreateRegistry();
        using var host = CreateHost(registry,
            LuaClrCapabilities.Construction | LuaClrCapabilities.MemberAccess |
            LuaClrCapabilities.DelegateConversion | LuaClrCapabilities.EventSubscription);
        var typeName = typeof(AotBindingFixture).FullName!;
        var target = LuaValue.FromUserdata(host.ClrBridge.CreateInstance(
            typeName, [LuaValue.FromInteger(40)]));
        var function = host.RunUtf8("return function(value) seen=value end").Execution!.Values[0];
        using var subscription = host.ClrBridge.Subscribe(
            target, nameof(AotBindingFixture.Changed), function);

        host.ClrBridge.SetMember(target, nameof(AotBindingFixture.Value), LuaValue.FromInteger(41));
        var added = host.ClrBridge.InvokeMember(
            target, nameof(AotBindingFixture.Add), [LuaValue.FromInteger(1)]);
        var updated = host.ClrBridge.InvokeMember(
            target, nameof(AotBindingFixture.Update), [LuaValue.FromInteger(10)]);

        Assert.Equal(41, host.State.GetGlobal("seen").AsInteger());
        Assert.Equal(42, added.ReturnValue.AsInteger());
        Assert.Equal(11, updated.RefOutValues[0].AsInteger());
        Assert.Equal("value", updated.NamedRefOutValues[0].Name);
        Assert.Equal(22, updated.NamedRefOutValues[1].Value.AsInteger());
    }

    [Fact]
    public void ConversionPoliciesProjectEnumsDecimalsCollectionsIteratorsAndTuples()
    {
        var registry = CreateRegistry();
        using var host = CreateHost(registry,
            LuaClrCapabilities.MemberAccess | LuaClrCapabilities.Async |
            LuaClrCapabilities.Disposal, true, options => options with
            {
                EnumRepresentation = LuaClrEnumRepresentation.NameAndInteger,
                DecimalRepresentation = LuaClrDecimalRepresentation.ExactString,
            });
        var typeName = typeof(AotBindingFixture).FullName!;
        var script = "local t='" + typeName + "';" +
            "local e=clr.call(t,'GetMode');local d=clr.call(t,'GetDecimal');" +
            "local a=clr.call(t,'GetList');local m=clr.call(t,'GetDictionary');" +
            "local x=clr.call(t,'GetTuple');local y=clr.call(t,'GetValueTuple');" +
            "local it=clr.call(t,'GetEnumerable');local ok1,v1=clr.next(it);" +
            "local ok2,v2=clr.next(it);local c=clr.call(t,'CountList',{1,2,3});" +
            "local s=clr.call(t,'SumDictionary',{a=4,b=5});" +
            "local z=clr.call(t,'EchoTuple',{'round',9});" +
            "return e.name,e.value,d,a[2],m.answer,x[1],x[2],y[1],y[2]," +
            "ok1,v1,ok2,v2,c,s,z[1],z[2]";
        var result = host.RunUtf8(script);

        Assert.True(result.Succeeded, result.Execution?.ToString());
        var v = result.Execution!.Values;
        Assert.Equal("Read, Write", v[0].AsString().ToString());
        Assert.Equal(3, v[1].AsInteger());
        Assert.Equal("79228162514264337593543950335", v[2].AsString().ToString());
        Assert.Equal(2, v[3].AsInteger());
        Assert.Equal(42, v[4].AsInteger());
        Assert.Equal("old", v[5].AsString().ToString());
        Assert.Equal(7, v[6].AsInteger());
        Assert.Equal("new", v[7].AsString().ToString());
        Assert.Equal(8, v[8].AsInteger());
        Assert.True(v[9].AsBoolean());
        Assert.Equal(5, v[10].AsInteger());
        Assert.True(v[11].AsBoolean());
        Assert.Equal(6, v[12].AsInteger());
        Assert.Equal(3, v[13].AsInteger());
        Assert.Equal(9, v[14].AsInteger());
        Assert.Equal("round", v[15].AsString().ToString());
        Assert.Equal(9, v[16].AsInteger());
    }

    [Fact]
    public void CyclesLimitsAndAllowlistConflictsFailClosed()
    {
        var registry = CreateRegistry();
        using var host = CreateHost(registry, LuaClrCapabilities.MemberAccess);
        var typeName = typeof(AotBindingFixture).FullName!;
        var cycle = Assert.Throws<LuaClrException>(() =>
            host.ClrBridge.InvokeStatic(typeName, nameof(AotBindingFixture.GetCycle)));
        Assert.Equal(LuaClrErrorCode.ConversionCycle, cycle.Code);

        using var limited = CreateHost(registry, LuaClrCapabilities.MemberAccess,
            options: value => value with
            {
                ConversionLimits = new LuaClrConversionLimits
                {
                    MaximumDepth = 8,
                    MaximumItems = 2,
                    MaximumBytes = 1024,
                },
            });
        var limit = Assert.Throws<LuaClrException>(() =>
            limited.ClrBridge.InvokeStatic(typeName, nameof(AotBindingFixture.GetList)));
        Assert.Equal(LuaClrErrorCode.ConversionLimitExceeded, limit.Code);

        var conflict = Assert.Throws<LuaClrException>(() => new LuaHost(new LuaHostOptions
        {
            InstallStandardLibrary = false,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.MemberAccess,
                AllowedAssemblyNames = [typeof(AotBindingFixture).Assembly.GetName().Name!],
                AllowedTypeNames = [typeName],
                AllowedMemberNames =
                [
                    nameof(AotBindingFixture.Value),
                    typeName + "." + nameof(AotBindingFixture.Value),
                ],
                BindingRegistry = registry,
                BindingMode = LuaClrBindingMode.RegistryOnly,
            },
        }));
        Assert.Equal(LuaClrErrorCode.BindingConflict, conflict.Code);
    }

    [Fact]
    public void HostileCollectionsExposeStableConversionErrors()
    {
        var registry = CreateRegistry();
        using var host = CreateHost(registry, LuaClrCapabilities.MemberAccess);
        var typeName = typeof(AotBindingFixture).FullName!;

        foreach (var memberName in new[]
                 {
                     nameof(AotBindingFixture.GetThrowingList),
                     nameof(AotBindingFixture.GetThrowingDictionary),
                     nameof(AotBindingFixture.GetThrowingTuple),
                 })
        {
            var exception = Assert.Throws<LuaClrException>(() =>
                host.ClrBridge.InvokeStatic(typeName, memberName));
            Assert.Equal(LuaClrErrorCode.ConversionFailed, exception.Code);
            Assert.IsType<InvalidOperationException>(exception.InnerException);
        }

        var iterator = host.ClrBridge.InvokeStatic(
            typeName, nameof(AotBindingFixture.GetThrowingEnumerable)).ReturnValue;
        var iteratorException = Assert.Throws<LuaClrException>(() =>
            host.ClrBridge.MoveNext(iterator));
        Assert.Equal(LuaClrErrorCode.IteratorClosed, iteratorException.Code);
        Assert.IsType<InvalidOperationException>(iteratorException.InnerException);
    }

    [Fact]
    public void ClosedGenericFactoryOnlyResolvesExactRegistration()
    {
        var registry = CreateRegistry();
        var listName = typeof(List<int>).FullName!;
        using var host = new LuaHost(new LuaHostOptions
        {
            InstallStandardLibrary = false,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.TypeDiscovery | LuaClrCapabilities.Construction,
                AllowedAssemblyNames = [typeof(List<int>).Assembly.GetName().Name!],
                AllowedTypeNames = [listName],
                BindingRegistry = registry,
                BindingMode = LuaClrBindingMode.RegistryOnly,
            },
        });
        var genericName = "System.Collections.Generic.List" + (char)96 + "1";
        Assert.Equal(listName, host.ClrBridge.ResolveClosedGeneric(
            genericName, ["System.Int32"]));
        Assert.Throws<LuaClrException>(() => host.ClrBridge.ResolveClosedGeneric(
            genericName, ["System.String"]));
    }

    [Fact]
    public void EnumDecimalIteratorAndRefLikeBoundariesAreExplicit()
    {
        var registry = CreateRegistry();
        var typeName = typeof(AotBindingFixture).FullName!;
        using var integerHost = CreateHost(registry,
            LuaClrCapabilities.MemberAccess | LuaClrCapabilities.Async,
            options: value => value with
            {
                EnumRepresentation = LuaClrEnumRepresentation.UnderlyingValue,
                DecimalRepresentation = LuaClrDecimalRepresentation.ExactInteger,
            });
        Assert.Equal(3, integerHost.ClrBridge.InvokeStatic(
            typeName, nameof(AotBindingFixture.GetMode)).ReturnValue.AsInteger());
        Assert.Equal(42, integerHost.ClrBridge.InvokeStatic(
            typeName, nameof(AotBindingFixture.GetWholeDecimal)).ReturnValue.AsInteger());
        Assert.Equal(3, integerHost.ClrBridge.InvokeStatic(
            typeName, nameof(AotBindingFixture.EchoMode), [LuaValue.FromInteger(3)])
            .ReturnValue.AsInteger());
        Assert.Throws<LuaClrException>(() => integerHost.ClrBridge.InvokeStatic(
            typeName, nameof(AotBindingFixture.GetDecimal)));

        var iterator = integerHost.ClrBridge.InvokeStatic(
            typeName, nameof(AotBindingFixture.GetEnumerable)).ReturnValue;
        using var cancellation = new LuaClrCancellation();
        integerHost.ClrBridge.LinkIteratorCancellation(iterator, cancellation);
        cancellation.Cancel();
        var cancelled = Assert.Throws<LuaClrException>(() =>
            integerHost.ClrBridge.MoveNext(iterator));
        Assert.Equal(LuaClrErrorCode.IteratorClosed, cancelled.Code);

        var refLikeName = typeof(RefLikeFixture).FullName!;
        using var reflectionHost = new LuaHost(new LuaHostOptions
        {
            InstallStandardLibrary = false,
            Clr = new LuaClrOptions
            {
                Capabilities = LuaClrCapabilities.MemberAccess,
                AllowedAssemblyNames = [typeof(RefLikeFixture).Assembly.GetName().Name!],
                AllowedTypeNames = [refLikeName],
                AllowedMemberNames = [refLikeName + "." + nameof(RefLikeFixture.Touch)],
            },
        });
        var invalid = Assert.Throws<LuaClrException>(() => reflectionHost.ClrBridge.InvokeStatic(
            refLikeName, nameof(RefLikeFixture.Touch)));
        Assert.Equal(LuaClrErrorCode.InvalidRefOut, invalid.Code);
    }

    private static LuaClrBindingRegistry CreateRegistry()
    {
        var registry = new LuaClrBindingRegistry();
        new Lunil.Generated.LuaClrGeneratedBindings().RegisterBindings(registry);
        return registry;
    }

    private static LuaHost CreateHost(
        LuaClrBindingRegistry registry,
        LuaClrCapabilities capabilities,
        bool installGlobalModule = false,
        Func<LuaClrOptions, LuaClrOptions>? options = null)
    {
        var fixture = typeof(AotBindingFixture).FullName!;
        var signal = typeof(AotSignalHandler).FullName!;
        var names = new[]
        {
            nameof(AotBindingFixture.Value), nameof(AotBindingFixture.Add),
            nameof(AotBindingFixture.Update), nameof(AotBindingFixture.Changed),
            nameof(AotBindingFixture.GetMode), nameof(AotBindingFixture.GetDecimal),
            nameof(AotBindingFixture.EchoMode), nameof(AotBindingFixture.GetWholeDecimal),
            nameof(AotBindingFixture.EchoDecimal),
            nameof(AotBindingFixture.GetList), nameof(AotBindingFixture.GetDictionary),
            nameof(AotBindingFixture.GetEnumerable), nameof(AotBindingFixture.GetTuple),
            nameof(AotBindingFixture.GetValueTuple), nameof(AotBindingFixture.GetCycle),
            nameof(AotBindingFixture.GetThrowingList),
            nameof(AotBindingFixture.GetThrowingDictionary),
            nameof(AotBindingFixture.GetThrowingEnumerable),
            nameof(AotBindingFixture.GetThrowingTuple),
            nameof(AotBindingFixture.CountList), nameof(AotBindingFixture.SumDictionary),
            nameof(AotBindingFixture.EchoTuple),
        };
        var clr = new LuaClrOptions
        {
            Capabilities = capabilities,
            AllowedAssemblyNames = [typeof(AotBindingFixture).Assembly.GetName().Name!],
            AllowedTypeNames = [fixture, signal],
            AllowedMemberNames = [.. names.Select(name => fixture + "." + name)],
            AllowedDelegateTypeNames = [signal],
            AllowedEventNames = [fixture + "." + nameof(AotBindingFixture.Changed)],
            BindingRegistry = registry,
            BindingMode = LuaClrBindingMode.RegistryOnly,
            InstallGlobalModule = installGlobalModule,
        };
        return new LuaHost(new LuaHostOptions
        {
            ExecutionBackend = LuaHostExecutionBackend.Interpreter,
            Clr = options?.Invoke(clr) ?? clr,
        });
    }
}

[Flags]
public enum AotMode { None = 0, Read = 1, Write = 2 }
public delegate void AotSignalHandler(int value);

public sealed class AotBindingFixture
{
    private int _value;
    public AotBindingFixture(int value) => _value = value;
    public event AotSignalHandler? Changed;
    public int Value
    {
        get => _value;
        set { _value = value; Changed?.Invoke(value); }
    }
    public int Add(int value) => _value + value;
    public void Update(ref int value, out int doubled)
    {
        value += _value == int.MinValue ? 2 : 1;
        doubled = value * 2;
    }
    public static AotMode GetMode() => AotMode.Read | AotMode.Write;
    public static AotMode EchoMode(AotMode value) => value;
    public static decimal GetDecimal() => decimal.MaxValue;
    public static decimal GetWholeDecimal() => 42m;
    public static decimal EchoDecimal(decimal value) => value;
    public static List<int> GetList() => [1, 2, 3];
    public static Dictionary<string, int> GetDictionary() => new() { ["answer"] = 42 };
    public static IEnumerable<int> GetEnumerable() => Yield();
    public static Tuple<string, int> GetTuple() => Tuple.Create("old", 7);
    public static (string, int) GetValueTuple() => ("new", 8);
    public static IList GetCycle()
    {
        var list = new ArrayList();
        list.Add(list);
        return list;
    }
    public static IList GetThrowingList() => new ThrowingList();
    public static IDictionary GetThrowingDictionary() => new ThrowingDictionary();
    public static IEnumerable GetThrowingEnumerable() => new ThrowingEnumerable();
    public static object GetThrowingTuple() => new ThrowingTuple();
    public static int CountList(List<int> values) => values.Count;
    public static int SumDictionary(Dictionary<string, int> values) => values.Values.Sum();
    public static Tuple<string, int> EchoTuple(Tuple<string, int> value) => value;
    private static IEnumerable<int> Yield()
    {
        yield return 5;
        yield return 6;
    }
}

internal sealed class ThrowingList : IList
{
    public int Count => throw new InvalidOperationException("hostile list count");
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public object? this[int index]
    {
        get => throw new InvalidOperationException("hostile list indexer");
        set => throw new NotSupportedException();
    }
    public int Add(object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object? value) => false;
    public int IndexOf(object? value) => -1;
    public void Insert(int index, object? value) => throw new NotSupportedException();
    public void Remove(object? value) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) => throw new NotSupportedException();
    public IEnumerator GetEnumerator() => Array.Empty<object>().GetEnumerator();
}

internal sealed class ThrowingDictionary : IDictionary
{
    public int Count => 1;
    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;
    public ICollection Keys => Array.Empty<object>();
    public ICollection Values => Array.Empty<object>();
    public object? this[object key]
    {
        get => null;
        set => throw new NotSupportedException();
    }
    public void Add(object key, object? value) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(object key) => false;
    public void Remove(object key) => throw new NotSupportedException();
    public void CopyTo(Array array, int index) => throw new NotSupportedException();
    public IDictionaryEnumerator GetEnumerator() =>
        throw new InvalidOperationException("hostile dictionary enumerator");
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}

internal sealed class ThrowingEnumerable : IEnumerable
{
    public IEnumerator GetEnumerator() => new ThrowingEnumerator();

    private sealed class ThrowingEnumerator : IEnumerator
    {
        public object Current => throw new InvalidOperationException("hostile iterator current");
        public bool MoveNext() => throw new InvalidOperationException("hostile iterator move-next");
        public void Reset() => throw new NotSupportedException();
    }
}

internal sealed class ThrowingTuple : System.Runtime.CompilerServices.ITuple
{
    public int Length => 1;
    public object? this[int index] => throw new InvalidOperationException("hostile tuple indexer");
}

public static class RefLikeFixture
{
    public static void Touch(ref Span<int> value) => value.Clear();
}
