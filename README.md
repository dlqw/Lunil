<p align="center">
  <img src="assets/lunil-logo.svg" width="168" alt="Lunil logo">
</p>

<h1 align="center">Lunil</h1>

<p align="center">
  Versioned Lua 5.1–5.5 compilation, analysis, and managed execution for .NET, Unity, and Godot.
</p>

<p align="center">
  <strong>English</strong> · <a href="README.zh-CN.md">简体中文</a>
</p>

<p align="center">
  <a href="https://github.com/dlqw/Lunil/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/dlqw/Lunil/ci.yml?branch=main&style=flat-square&label=CI"></a>
  <a href="https://github.com/dlqw/Lunil/releases"><img alt="Stable release" src="https://img.shields.io/badge/stable-0.18.0-16a34a?style=flat-square"></a>
  <img alt=".NET 10 and .NET Standard 2.1" src="https://img.shields.io/badge/.NET-10%20%7C%20Standard%202.1-512BD4?style=flat-square&logo=dotnet">
  <img alt="Lua 5.4" src="https://img.shields.io/badge/Lua-5.4-2C2D72?style=flat-square&logo=lua">
</p>

Lunil is a pure C# Lua toolchain. Lua 5.4 is the default language contract, validated against the
PUC Lua 5.4.8 compatibility baseline, with explicit Lua 5.1, 5.2, 5.3, and 5.5 modes. Source and
versioned binary chunks lower to one verified canonical IR,
then run through the portable interpreter or the profile-guided .NET 10 JIT.

## Capabilities

| Area | What Lunil provides |
| --- | --- |
| Language and compiler | Version-specific Lua 5.1–5.5 syntax and chunk contracts, lossless UTF-8 syntax, annotations, semantic binding, canonical lowering, and independent IR verification. |
| Managed runtime | Lua values, tables, closures, coroutines, metatables, protected calls, logical GC, resource budgets, a reference interpreter, and adaptive .NET 10 JIT execution. |
| Static analysis | Symbols, types, control-flow graphs, stable keys, member/reference indexes, call graphs, metatable and object-model facts, class-factory definitions, closure upvalues, and nil-path analysis. |
| Workspace analysis | Module discovery, cycles, exports, cross-module calls and references, external host contracts, incremental invalidation, compact snapshots, and bounded caches for large repositories. |
| Hosting and interoperation | Restricted, trusted, and deterministic hosts; exact-allowlist CLR access; generated AOT bindings; opt-in native C ABI FFI; callbacks, tasks, timers, cancellation, and host-owned services. |
| Engines and updates | Engine-neutral game-loop scheduling, Unity and Godot packages, persistence, frame-boundary publication, and signed atomic patch deployment. |
| Type checking | Annotation-driven bounded flow analysis enabled by default: assignability, argument-count, nil-path, and cross-module export consistency diagnostics (`LUA6000` line) with CLI/LSP/embedding suppression. |
| Debugger | A Debug Adapter Protocol server (`lunil-debug-adapter`): VS Code launch of scripts and attach to game-loop hosts over a named pipe, with breakpoints, stepping, pause, stack, locals, and upvalues. |
| Editor tooling | A self-contained LSP 3.17 server and platform-specific VS Code extension with diagnostics, navigation, rename, symbols, semantic tokens, hints, call hierarchy, workspace-symbol search, class hierarchy, and find-usages. |
| Deployment | .NET 10 and `netstandard2.1` assets, NativeAOT, trimming, single-file, ReadyToRun, IL2CPP, and release bundles for six desktop RIDs. |

> [!TIP]
> See the [0.18.0 release](https://github.com/dlqw/Lunil/releases/tag/v0.18.0) for the current
> change list and the [migration guide](docs/migration-0.18.0.pub.md) for compatibility details.

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

Packages are attached to each [GitHub Release](https://github.com/dlqw/Lunil/releases). Download the
NuGet assets into a local source:

```bash
gh release download v0.18.0 --repo dlqw/Lunil --pattern "*.nupkg" --dir .lunil-packages
```

Add the release directory alongside NuGet.org in `NuGet.Config`:

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
dotnet tool install --global Lunil.Cli --version 0.18.0
lunil --version
```

```xml
<PackageReference Include="Lunil.Hosting" Version="0.18.0" />
```

Unity and Godot installation steps are in their dedicated hosting guides. The release also includes
self-contained CLI bundles and six platform-specific VS Code packages.

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

On .NET 10, `LuaHostExecutionBackend.Auto` selects the JIT when dynamic code is available. Portable
and AOT assets use the interpreter without loading or probing the JIT assembly.

## Samples

| Sample | Open or run |
| --- | --- |
| [Portable host](https://github.com/dlqw/Lunil/tree/v0.18.0/samples/Lunil.Portable.Hosting) | `dotnet run --project samples/Lunil.Portable.Hosting` |
| [Static analysis embedding](https://github.com/dlqw/Lunil/tree/v0.18.0/samples/Lunil.StaticAnalysis.Embedding) | `dotnet run --project samples/Lunil.StaticAnalysis.Embedding` |
| [Unity 2022.3](https://github.com/dlqw/Lunil/tree/v0.18.0/samples/Lunil.Unity.2022.3) | Open directly with Unity 2022.3 LTS |
| [Unity 6](https://github.com/dlqw/Lunil/tree/v0.18.0/samples/Lunil.Unity.6) | Open directly with Unity 6 |
| [Godot 4.4](https://github.com/dlqw/Lunil/tree/v0.18.0/samples/Lunil.Godot.4.4) | Open directly with Godot 4.4.1 .NET |
| [Godot 4.6](https://github.com/dlqw/Lunil/tree/v0.18.0/samples/Lunil.Godot.4.6) | Open directly with Godot 4.6.3 .NET |

The Unity projects are independent: the 2022.3 sample does not require an upgrade through Unity 6.

## Documentation

| Area | Guides | Reference and explanation |
| --- | --- | --- |
| Compiler and analysis | [Static analysis embedding](docs/static-analysis-embedding.pub.md) · [External host analysis](docs/external-host-analysis.pub.md) · [Large workspaces](docs/large-workspaces.pub.md) | [Analysis facts](docs/analysis-facts.pub.md) · [Type checking](docs/type-checking.pub.md) · [PUC Lua prototype import](docs/puc-prototype-import.pub.md) |
| Hosting and interoperation | [Portable hosting](docs/portable-hosting.pub.md) · [Game-loop hosting](docs/game-engine-hosting.pub.md) · [CLR interoperation](docs/clr-interop.pub.md) · [AOT bindings](docs/aot-bindings.pub.md) · [Native FFI](docs/ffi.pub.md) | [CLR contracts](docs/clr-interop-reference.pub.md) · [CLR lifecycle](docs/clr-interop-lifecycle.pub.md) · [FFI reference](docs/ffi-reference.pub.md) |
| Engines and updates | [Unity hosting](docs/unity-hosting.pub.md) · [Godot hosting](docs/godot-hosting.pub.md) · [Signed patch deployment](docs/deploy-signed-patch-bundles.pub.md) | [Unity reference](docs/unity-reference.pub.md) · [Godot reference](docs/godot-reference.pub.md) · [Patch bundle reference](docs/signed-patch-bundles.pub.md) · [Patch publication model](docs/signed-patch-publication.pub.md) |
| Tools and deployment | [VS Code](docs/vscode.pub.md) · [Configuring the language server](docs/configuring-the-language-server.pub.md) · [Debugging Lua](docs/debugging.pub.md) · [NativeAOT and trimming](docs/nativeaot-build-integration.pub.md) | [CLI reference](docs/cli.pub.md) · [Language server](docs/language-server.pub.md) · [Debugging reference](docs/debugging-reference.pub.md) · [0.18 migration](docs/migration-0.18.0.pub.md) |

## Compatibility

- Default language contract: Lua 5.4; PUC Lua 5.4.8 is the compatibility baseline. Explicit Lua
  5.1–5.5 contracts remain available.
- Stable line: `0.18.x`; existing .NET 10 host entry points remain source compatible unless the
  migration guide states otherwise.
- Release bundles: `win-x64`, `win-arm64`, `linux-x64`, `linux-arm64`, `osx-x64`, `osx-arm64`.
- CLR interoperation is opt-in and fail-closed. Trusted .NET hosts may explicitly select
  `RegistryThenReflection`; NativeAOT, IL2CPP, and trimming use generated `RegistryOnly` bindings.
- Native FFI is opt-in and fail-closed: disabled by default, exact library and symbol allowlists,
  and exact AOT registry bindings without dynamic code.
