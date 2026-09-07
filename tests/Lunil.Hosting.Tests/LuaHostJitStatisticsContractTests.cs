using System.Reflection;

using Lunil.CodeGen.Cil.Jit;
using Lunil.Hosting;

namespace Lunil.Hosting.Tests;

public sealed class LuaHostJitStatisticsContractTests
{
    [Fact]
    public void HostStatisticsMirrorTracksEveryJitStatisticInOrder()
    {
        var jitProperties = typeof(LuaJitStatistics)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => (property.Name, property.PropertyType))
            .ToArray();
        var hostProperties = typeof(LuaHostJitStatistics)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Select(static property => (property.Name, property.PropertyType))
            .ToArray();

        Assert.Equal(
            jitProperties.Select(static property => property.Name).ToArray(),
            hostProperties.Select(static property => property.Name).ToArray());
        Assert.Equal(
            jitProperties.Select(static property => property.PropertyType).ToArray(),
            hostProperties.Select(static property => property.PropertyType).ToArray());
    }

    [Fact]
    public void ConvertCarriesEveryCounterValueAcrossTheBoundary()
    {
        // Distinct values prove the field-by-field conversion maps each counter to the
        // mirror position with the same name, so a transposed argument cannot pass. The
        // constructor is invoked through its public contract so any newly added counter
        // automatically participates in this test.
        var constructor = typeof(LuaJitStatistics)
            .GetConstructors(BindingFlags.Public | BindingFlags.Instance)
            .Single(static candidate => candidate.GetParameters().Length > 0);
        var arguments = constructor.GetParameters()
            .Select((parameter, index) => Convert.ChangeType(index + 1, parameter.ParameterType, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        var jit = (LuaJitStatistics)constructor.Invoke(arguments);

        var host = LuaHostCilJitBackend.Convert(jit);

        var jitProperties = typeof(LuaJitStatistics).GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .ToDictionary(static property => property.Name, static property => property);
        foreach (var hostProperty in typeof(LuaHostJitStatistics).GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var expected = jitProperties[hostProperty.Name].GetValue(jit);
            var actual = hostProperty.GetValue(host);
            Assert.True(
                Equals(expected, actual),
                $"{hostProperty.Name}: expected {expected}, actual {actual}");
        }
    }

    [Theory]
    [InlineData(LuaHostJitPolicy.InterpreterOnly, LuaJitPolicy.InterpreterOnly)]
    [InlineData(LuaHostJitPolicy.Auto, LuaJitPolicy.Auto)]
    [InlineData(LuaHostJitPolicy.PreferJit, LuaJitPolicy.PreferJit)]
    [InlineData(LuaHostJitPolicy.RequireJit, LuaJitPolicy.RequireJit)]
    public void PolicyMappingIsExplicit(LuaHostJitPolicy hostPolicy, LuaJitPolicy expected)
    {
        Assert.Equal(expected, LuaHostCilJitBackend.MapPolicy(hostPolicy));
    }
}
