<p align="center">
  <img src="assets/lunil-logo.svg" width="168" alt="Lunil logo">
</p>

<h1 align="center">Lunil</h1>

<p align="center">
  Versioned Lua 5.1–5.5 compilation, analysis, and managed execution for .NET, Unity, and Godot.
</p>

<p align="center">
  <strong>English</strong> · <a href="https://github.com/dlqw/Lunil/blob/v0.14.0/README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <a href="https://github.com/dlqw/Lunil/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/dlqw/Lunil/ci.yml?branch=main&style=flat-square&label=CI"></a>
  <a href="https://github.com/dlqw/Lunil/releases"><img alt="Stable release" src="https://img.shields.io/badge/stable-0.14.0-16a34a?style=flat-square"></a>
  <img alt=".NET 10 and .NET Standard 2.1" src="https://img.shields.io/badge/.NET-10%20%7C%20Standard%202.1-512BD4?style=flat-square&logo=dotnet">
  <img alt="Lua 5.4.8" src="https://img.shields.io/badge/Lua-5.4.8-2C2D72?style=flat-square&logo=lua">
</p>

Lunil is a pure C# Lua toolchain. Lua 5.4.8 is the default contract, with explicit Lua 5.1,
5.2, 5.3, and 5.5 modes. Source and versioned binary chunks lower to one verified canonical IR,
then run through the portable interpreter or the profile-guided .NET 10 JIT.

## What 0.14 adds

- Flow-sensitive metatable and metamethod analysis, prototype-style OOP and `self` inference,
  shared closure upvalues, and path-sensitive nil-chain diagnostics.
- Cross-module export, call, reference, callback-registration, and persistence-schema indexes with
  host definitions injected from versioned C++, C#, Unity, or Godot contracts.
- Compact and incremental syntax storage plus bounded compact workspace snapshots for projects from
  editor-sized files through multi-million-line repositories.
- A self-contained LSP 3.17 server and a VS Code extension for Windows, Linux, and macOS on x64 and
  ARM64, including host-aware navigation and a virtual host-contract document.
- Stable `netstandard2.1` compiler and analysis assets alongside NativeAOT, trimming, single-file,
  ReadyToRun, and portable-host compatibility.

## Platform support

| Host | Support | Execution |
| --- | --- | --- |
| .NET 10 | Stable | Auto JIT or interpreter |
| `netstandard2.1` consumers | Stable | Interpreter; no dynamic-code probing |
| Unity 2022.3 LTS | Stable | Editor/Mono and IL2CPP |
| Unity 6 (`6000.0`, `6000.3`) | Stable | Editor/Mono and IL2CPP |
| Godot 4.4 and 4.6 .NET desktop | Stable | Interpreter |
| Godot 4.4 and 4.6 Android | Stable | Interpreter |
| Godot iOS | Preview | Export requires the official macOS toolchain |

Unity IL2CPP coverage includes Windows and Android execution, WebGL browser execution, and iOS
generated-player compilation. See the engine references for the exact version and platform matrix.

## Install

Packages are attached to each [GitHub Release](https://github.com/dlqw/Lunil/releases). Download all
`*.nupkg` assets into a local source; with the GitHub CLI:

```bash
gh release download v0.14.0 --repo dlqw/Lunil --pattern "*.nupkg" --dir .lunil-packages
gh release download v0.14.0 --repo dlqw/Lunil --pattern "com.dlqw.lunil-0.14.0.tgz" --dir .lunil-engine-assets
gh release download v0.14.0 --repo dlqw/Lunil --pattern "Lunil.Godot.addon-0.14.0.zip" --dir .lunil-engine-assets
```

Add both the release directory and NuGet.org to `NuGet.Config` so third-party dependencies remain
available:

```xml
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="lunil-release" value="./.lunil-packages" />
  </packageSources>
</configuration>
```

Install the CLI or reference the host:

```bash
dotnet tool install --global Lunil.Cli --version 0.14.0
lunil --version
```

```xml
<PackageReference Include="Lunil.Hosting" Version="0.14.0" />
```

Unity users install `.lunil-engine-assets/com.dlqw.lunil-0.14.0.tgz` with Package Manager. Godot
users install the `Lunil.Godot` NuGet package, extract
`.lunil-engine-assets/Lunil.Godot.addon-0.14.0.zip`, and copy its `addons/lunil` directory into the
project as `res://addons/lunil`.

## Run Lua from .NET

```csharp
using Lunil.Hosting;
using Lunil.Runtime.Execution;

using var host = new LuaHost(LuaHostOptions.Restricted);
var run = host.RunUtf8("return 6 * 7", "@examples/answer.lua");

if (!run.CompilationSucceeded)
    throw new InvalidOperationException(string.Join("\n", run.Compilation.Diagnostics));
var execution = run.Execution;
if (execution is null || execution.Signal != LuaVmSignal.Completed)
    throw new InvalidOperationException("Lua execution did not complete.");

Console.WriteLine(execution.Values[0].AsInteger()); // 42
```

On .NET 10, `LuaHostExecutionBackend.Auto` selects the JIT when dynamic code is available. The
portable asset and AOT runtimes select the interpreter without loading or probing the JIT assembly.

## Drive a game loop

```csharp
using var loop = new LuaGameLoopHost(new LuaGameLoopHostOptions
{
    HostOptions = LuaHostOptions.Restricted with
    {
        ExecutionBackend = LuaHostExecutionBackend.Interpreter,
    },
});

var script = loop.Host.CompileUtf8(
    "counter=1; coroutine.yield(); counter=counter+1; return counter");
var operation = loop.Start(script);
loop.Tick();
loop.Tick();
Console.WriteLine(operation.Values[0].AsInteger()); // 2
```

`Tick` and `TickFixed` must run on the construction thread. Background callbacks are marshalled
through `ILuaGameLoopDispatcher`; patches use `PublishAtFrameBoundary` for atomic visibility.

## Samples

| Sample | Open or run |
| --- | --- |
| [Portable host](https://github.com/dlqw/Lunil/tree/v0.14.0/samples/Lunil.Portable.Hosting) | `dotnet run --project samples/Lunil.Portable.Hosting` |
| [Unity 2022.3](https://github.com/dlqw/Lunil/tree/v0.14.0/samples/Lunil.Unity.2022.3) | Open directly with Unity 2022.3 LTS |
| [Unity 6](https://github.com/dlqw/Lunil/tree/v0.14.0/samples/Lunil.Unity.6) | Open directly with Unity 6 |
| [Godot 4.4](https://github.com/dlqw/Lunil/tree/v0.14.0/samples/Lunil.Godot.4.4) | Open directly with Godot 4.4.1 .NET |
| [Godot 4.6](https://github.com/dlqw/Lunil/tree/v0.14.0/samples/Lunil.Godot.4.6) | Open directly with Godot 4.6.3 .NET |
| [Static analysis embedding](https://github.com/dlqw/Lunil/tree/v0.14.0/samples/Lunil.StaticAnalysis.Embedding) | `dotnet run --project samples/Lunil.StaticAnalysis.Embedding` |

The Unity projects are independent: the 2022.3 sample does not require an upgrade through Unity 6.

## Documentation

| Document | Type |
| --- | --- |
| [Portable hosting](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/portable-hosting.pub.md) | How-to |
| [Engine-neutral game-loop hosting](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/game-engine-hosting.pub.md) | How-to |
| [Unity hosting](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/unity-hosting.pub.md) · [Unity reference](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/unity-reference.pub.md) | How-to · Reference |
| [Godot hosting](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/godot-hosting.pub.md) · [Godot reference](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/godot-reference.pub.md) | How-to · Reference |
| [AOT CLR bindings](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/aot-bindings.pub.md) | How-to |
| [CLR interoperation](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/clr-interop.pub.md) · [Contracts](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/clr-interop-reference.pub.md) · [Lifecycle model](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/clr-interop-lifecycle.pub.md) | How-to · Reference · Explanation |
| [Signed patch bundles](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/signed-patch-bundles.pub.md) · [Deployment](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/deploy-signed-patch-bundles.pub.md) · [Publication model](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/signed-patch-publication.pub.md) | Reference · How-to · Explanation |
| [CLI](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/cli.pub.md) | Reference |
| [.NET NativeAOT and trimming](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/nativeaot-build-integration.pub.md) | How-to |
| [Static analysis embedding](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/static-analysis-embedding.pub.md) | How-to |
| [Large-workspace analysis](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/large-workspaces.pub.md) | How-to |
| [Language server](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/language-server.pub.md) | Reference |
| [VS Code](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/vscode.pub.md) | How-to |
| [PUC Lua prototype import](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/puc-prototype-import.pub.md) | Reference |
| [Migrate to 0.14](https://github.com/dlqw/Lunil/blob/v0.14.0/docs/migration-0.14.0.pub.md) | Migration guide |

## Compatibility

- Default language: Lua 5.4.8; explicit Lua 5.1–5.5 contracts remain available.
- Stable line: `0.14.x`; migration from 0.13 is documented and existing .NET 10 host entry points
  remain source compatible unless the migration guide states otherwise.
- Release bundles: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.
- CLR interoperation is opt-in and fail-closed. Trusted .NET hosts may explicitly select
  `RegistryThenReflection`; NativeAOT, IL2CPP, and trimming use generated `RegistryOnly` bindings.
