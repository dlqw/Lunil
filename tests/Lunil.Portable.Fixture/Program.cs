using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Lunil.Core;
using Lunil.Hosting;
using Lunil.Runtime.Values;
using Lunil.Portable.Fixture;

[assembly: LuaClrGenerateBinding(
    typeof(Lunil.Portable.Fixture.PortableClrFixture),
    nameof(Lunil.Portable.Fixture.PortableClrFixture.Add))]

var framework = typeof(LuaHost).Assembly
    .GetCustomAttribute<TargetFrameworkAttribute>()?
    .FrameworkName;
if (!string.Equals(framework, ".NETStandard,Version=v2.1", StringComparison.Ordinal))
{
    return Fail($"Hosting resolved '{framework}' instead of the netstandard2.1 asset.");
}

LuaLanguageVersion[] versions =
[
    LuaLanguageVersion.Lua51,
    LuaLanguageVersion.Lua52,
    LuaLanguageVersion.Lua53,
    LuaLanguageVersion.Lua54,
    LuaLanguageVersion.Lua55,
];

foreach (var version in versions)
{
    using var host = new LuaHost(new LuaHostOptions
    {
        LanguageVersion = version,
        ExecutionBackend = LuaHostExecutionBackend.Auto,
    });
    if (host.IsDynamicCodeAvailable ||
        host.SelectedExecutionBackend != LuaHostExecutionBackend.Interpreter ||
        host.JitStatistics is not null)
    {
        return Fail($"Portable backend selection was invalid for {version}.");
    }

    var result = host.RunUtf8(
        "local sum=0; for i=1,10 do sum=sum+i end; " +
        "local value={answer=sum,label='portable'}; " +
        "return value.answer,#value.label,value.label",
        "=portable-fixture");
    if (!result.Succeeded || result.Execution?.Values.Length != 3)
    {
        return Fail($"Portable execution failed for {version}.");
    }

    var values = result.Execution.Values;
    var expectedKind = version is LuaLanguageVersion.Lua51 or LuaLanguageVersion.Lua52
        ? LuaValueKind.Float
        : LuaValueKind.Integer;
    if (values[0].Kind != expectedKind ||
        values[0].AsFloat() != 55 ||
        values[1].AsInteger() != 8 ||
        values[2].AsString().ToString() != "portable")
    {
        return Fail($"Portable trace diverged for {version}.");
    }

    Console.WriteLine(
        $"{LuaLanguageVersions.GetDisplayName(version)}:{values[0].Kind}:55:8:portable");
}

using (var game = new LuaGameLoopHost(new LuaGameLoopHostOptions
{
    HostOptions = new LuaHostOptions
    {
        ExecutionBackend = LuaHostExecutionBackend.Auto,
    },
}))
{
    var operation = game.Start(game.Host.CompileUtf8(
        "local value=0; for i=1,2 do value=value+i; coroutine.yield(value) end; return value",
        "=portable-game-loop"));
    var first = game.Tick();
    var second = game.Tick();
    var third = game.Tick();
    if (!first.Succeeded || !second.Succeeded || !third.Succeeded ||
        operation.Status != LuaGameLoopOperationStatus.Completed ||
        operation.Values[0].AsInteger() != 3 ||
        first.ExecutedInstructionCount <= 0 ||
        game.Host.SelectedExecutionBackend != LuaHostExecutionBackend.Interpreter)
    {
        return Fail("The portable game-loop coroutine contract is invalid.");
    }
}

try
{
    using var _ = new LuaHost(new LuaHostOptions
    {
        ExecutionBackend = LuaHostExecutionBackend.Jit,
    });
    return Fail("The portable asset accepted a required dynamic-code backend.");
}
catch (PlatformNotSupportedException)
{
}

var bindingRegistry = new LuaClrBindingRegistry();
new Lunil.Generated.LuaClrGeneratedBindings().RegisterBindings(bindingRegistry);
var portableClrType = typeof(PortableClrFixture).FullName!;
using (var interopHost = new LuaHost(new LuaHostOptions
{
    ExecutionBackend = LuaHostExecutionBackend.Interpreter,
    InstallStandardLibrary = false,
    Clr = new LuaClrOptions
    {
        Capabilities = LuaClrCapabilities.Construction | LuaClrCapabilities.MemberAccess,
        AllowedAssemblyNames = [typeof(PortableClrFixture).Assembly.GetName().Name!],
        AllowedTypeNames = [portableClrType],
        AllowedMemberNames = [portableClrType + "." + nameof(PortableClrFixture.Add)],
        BindingRegistry = bindingRegistry,
        BindingMode = LuaClrBindingMode.RegistryOnly,
    },
}))
{
    var target = LuaValue.FromUserdata(interopHost.ClrBridge.CreateInstance(
        portableClrType, [LuaValue.FromInteger(40)]));
    if (interopHost.ClrBridge.InvokeMember(
            target, nameof(PortableClrFixture.Add), [LuaValue.FromInteger(2)])
        .ReturnValue.AsInteger() != 42)
    {
        return Fail("Portable generated CLR binding failed.");
    }
}

using (var key = ECDsa.Create(ECCurve.NamedCurves.nistP256))
{
    var signer = new LuaPatchEcdsaSigner("portable", key);
    var trustStore = new LuaPatchEcdsaTrustStore(
    [
        new LuaPatchTrustedEcdsaKey("portable", key.ExportSubjectPublicKeyInfo()),
    ]);
    var digest = SHA256.HashData("portable-signature"u8);
    var signature = signer.SignDigest(digest);
    if (signature.Length != 64 ||
        !trustStore.VerifyDigest(signer.Algorithm, signer.KeyId, digest, signature))
    {
        return Fail("Portable ECDSA signatures did not preserve the P1363 contract.");
    }

    signature[0] ^= 0x01;
    if (trustStore.VerifyDigest(signer.Algorithm, signer.KeyId, digest, signature))
    {
        return Fail("Portable ECDSA verification accepted a modified signature.");
    }
}

Console.WriteLine("LUNIL_PORTABLE_OK");
return 0;

static int Fail(string message)
{
    Console.Error.WriteLine(message);
    return 2;
}
