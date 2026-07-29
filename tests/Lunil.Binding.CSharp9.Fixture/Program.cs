using System;
using Lunil.Hosting;
using Lunil.Runtime.Values;

[assembly: LuaClrGenerateBinding(
    typeof(Lunil.Binding.CSharp9.Fixture.CSharp9Target),
    nameof(Lunil.Binding.CSharp9.Fixture.CSharp9Target.Add))]

namespace Lunil.Binding.CSharp9.Fixture
{
    public static class Program
    {
        public static int Main()
        {
            var registry = new LuaClrBindingRegistry();
            new Lunil.Generated.LuaClrGeneratedBindings().RegisterBindings(registry);
            var typeName = typeof(CSharp9Target).FullName!;
            using (var host = new LuaHost(new LuaHostOptions
            {
                InstallStandardLibrary = false,
                Clr = new LuaClrOptions
                {
                    Capabilities = LuaClrCapabilities.Construction | LuaClrCapabilities.MemberAccess,
                    AllowedAssemblyNames = System.Collections.Immutable.ImmutableArray.Create(
                        typeof(CSharp9Target).Assembly.GetName().Name!),
                    AllowedTypeNames = System.Collections.Immutable.ImmutableArray.Create(typeName),
                    AllowedMemberNames = System.Collections.Immutable.ImmutableArray.Create(
                        typeName + "." + nameof(CSharp9Target.Add)),
                    BindingRegistry = registry,
                    BindingMode = LuaClrBindingMode.RegistryOnly,
                },
            }))
            {
                var target = LuaValue.FromUserdata(host.ClrBridge.CreateInstance(
                    typeName, new[] { LuaValue.FromInteger(40) }));
                var result = host.ClrBridge.InvokeMember(
                    target, nameof(CSharp9Target.Add), new[] { LuaValue.FromInteger(2) });
                return result.ReturnValue.AsInteger() == 42 ? 0 : 2;
            }
        }
    }

    public sealed class CSharp9Target
    {
        private readonly long _value;
        public CSharp9Target(long value) { _value = value; }
        public long Add(long value) { return _value + value; }
    }
}
