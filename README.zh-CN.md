<p align="center">
  <img src="assets/lunil-logo.svg" width="168" alt="Lunil logo">
</p>

<h1 align="center">Lunil</h1>

<p align="center">
  面向 .NET、Unity 与 Godot 的版本化 Lua 5.1–5.5 编译、分析与托管执行环境。
</p>

<p align="center">
  <a href="https://github.com/dlqw/Lunil/blob/v0.13.0/README.md">English</a> · <strong>简体中文</strong>
</p>

<p align="center">
  <a href="https://github.com/dlqw/Lunil/actions/workflows/ci.yml"><img alt="CI" src="https://img.shields.io/github/actions/workflow/status/dlqw/Lunil/ci.yml?branch=main&style=flat-square&label=CI"></a>
  <a href="https://github.com/dlqw/Lunil/releases"><img alt="稳定版本" src="https://img.shields.io/badge/stable-0.13.0-16a34a?style=flat-square"></a>
  <img alt=".NET 10 与 .NET Standard 2.1" src="https://img.shields.io/badge/.NET-10%20%7C%20Standard%202.1-512BD4?style=flat-square&logo=dotnet">
  <img alt="Lua 5.4.8" src="https://img.shields.io/badge/Lua-5.4.8-2C2D72?style=flat-square&logo=lua">
</p>

Lunil 是纯 C# Lua 工具链。默认契约为 Lua 5.4.8，也可显式选择 Lua 5.1、5.2、5.3
或 5.5。源码与版本化二进制 chunk 会降低为同一套经过验证的 canonical IR，再由可移植
解释器或 .NET 10 profile-guided JIT 执行。

## 0.13 新增内容

- `netstandard2.1` compiler、analysis、runtime、standard library、workspace 与 hosting 资产。
- `LuaGameLoopHost`：带边界预算的 Update/FixedUpdate 调度、帧边界发布、取消、timer、
  持久化与引擎服务注入。
- 面向 NativeAOT、IL2CPP 和 trimming 的 C# CLR binding registry 生成器。
- 同时正式支持 **Unity 2022.3 LTS 与 Unity 6** 的离线 Unity Package Manager 包。
- 面向 **Godot 4.4 与 4.6 .NET** 的 `Lunil.Godot` 包和 addon。
- 通过同一 engine-neutral 边界准备和原子发布签名 patch。

## 平台支持

| 宿主 | 支持级别 | 执行方式 |
| --- | --- | --- |
| .NET 10 | 稳定 | Auto JIT 或解释器 |
| `netstandard2.1` consumer | 稳定 | 解释器；不探测动态代码 |
| Unity 2022.3 LTS | 稳定 | Editor/Mono 与 IL2CPP |
| Unity 6（`6000.0`、`6000.3`） | 稳定 | Editor/Mono 与 IL2CPP |
| Godot 4.4 与 4.6 .NET 桌面端 | 稳定 | 解释器 |
| Godot 4.4 与 4.6 Android | 稳定 | 解释器 |
| Godot iOS | Preview | 导出依赖官方 macOS 工具链 |

Unity IL2CPP 覆盖 Windows 与 Android 实际运行、WebGL 浏览器运行以及 iOS generated-player
编译。准确的版本和平台矩阵见对应引擎 reference。

## 安装

每个 [GitHub Release](https://github.com/dlqw/Lunil/releases) 都会附带 package。先把全部
`*.nupkg` asset 下载到 `.lunil-packages` 目录；GitHub CLI 命令为：

```bash
gh release download v0.13.0 --repo dlqw/Lunil --pattern "*.nupkg" --dir .lunil-packages
gh release download v0.13.0 --repo dlqw/Lunil --pattern "com.dlqw.lunil-0.13.0.tgz" --dir .lunil-engine-assets
gh release download v0.13.0 --repo dlqw/Lunil --pattern "Lunil.Godot.addon-0.13.0.zip" --dir .lunil-engine-assets
```

在 `NuGet.Config` 中同时加入 release 目录与 NuGet.org，确保第三方依赖仍可解析：

```xml
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
    <add key="lunil-release" value="./.lunil-packages" />
  </packageSources>
</configuration>
```

然后安装 CLI 或引用 host：

```bash
dotnet tool install --global Lunil.Cli --version 0.13.0
lunil --version
```

```xml
<PackageReference Include="Lunil.Hosting" Version="0.13.0" />
```

Unity 用户通过 Package Manager 安装 `.lunil-engine-assets/com.dlqw.lunil-0.13.0.tgz`。Godot 用户
安装 `Lunil.Godot` NuGet 包，解压 `.lunil-engine-assets/Lunil.Godot.addon-0.13.0.zip`，再把其中的
`addons/lunil` 目录复制到项目的 `res://addons/lunil`。

## 在 .NET 中运行 Lua

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

在 .NET 10 上，`LuaHostExecutionBackend.Auto` 会在动态代码可用时选择 JIT。可移植资产与
AOT runtime 会选择解释器，不加载或探测 JIT assembly。

## 驱动游戏循环

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

`Tick` 和 `TickFixed` 必须在构造线程调用。后台 callback 通过
`ILuaGameLoopDispatcher` 回到主线程；patch 使用 `PublishAtFrameBoundary` 保证原子可见。

## 示例

| 示例 | 打开或运行方式 |
| --- | --- |
| [可移植 host](https://github.com/dlqw/Lunil/tree/v0.13.0/samples/Lunil.Portable.Hosting) | `dotnet run --project samples/Lunil.Portable.Hosting` |
| [Unity 2022.3](https://github.com/dlqw/Lunil/tree/v0.13.0/samples/Lunil.Unity.2022.3) | 直接用 Unity 2022.3 LTS 打开 |
| [Unity 6](https://github.com/dlqw/Lunil/tree/v0.13.0/samples/Lunil.Unity.6) | 直接用 Unity 6 打开 |
| [Godot 4.4](https://github.com/dlqw/Lunil/tree/v0.13.0/samples/Lunil.Godot.4.4) | 直接用 Godot 4.4.1 .NET 打开 |
| [Godot 4.6](https://github.com/dlqw/Lunil/tree/v0.13.0/samples/Lunil.Godot.4.6) | 直接用 Godot 4.6.3 .NET 打开 |
| [静态分析嵌入](https://github.com/dlqw/Lunil/tree/v0.13.0/samples/Lunil.StaticAnalysis.Embedding) | `dotnet run --project samples/Lunil.StaticAnalysis.Embedding` |

两个 Unity 项目彼此独立；Unity 2022.3 示例不需要先由 Unity 6 升级。

## 文档

| 文档 | 类型 |
| --- | --- |
| [可移植 hosting](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/portable-hosting.zh-CN.pub.md) | How-to |
| [Engine-neutral game-loop hosting](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/game-engine-hosting.zh-CN.pub.md) | How-to |
| [Unity hosting](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/unity-hosting.zh-CN.pub.md) · [Unity reference](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/unity-reference.zh-CN.pub.md) | How-to · Reference |
| [Godot hosting](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/godot-hosting.zh-CN.pub.md) · [Godot reference](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/godot-reference.zh-CN.pub.md) | How-to · Reference |
| [AOT CLR binding](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/aot-bindings.zh-CN.pub.md) | How-to |
| [CLR 互操作](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/clr-interop.zh-CN.pub.md) · [契约](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/clr-interop-reference.zh-CN.pub.md) · [生命周期原理](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/clr-interop-lifecycle.zh-CN.pub.md) | How-to · Reference · Explanation |
| [签名 Patch Bundle](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/signed-patch-bundles.zh-CN.pub.md) · [部署](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/deploy-signed-patch-bundles.zh-CN.pub.md) · [发布原理](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/signed-patch-publication.zh-CN.pub.md) | Reference · How-to · Explanation |
| [CLI](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/cli.zh-CN.pub.md) | Reference |
| [.NET NativeAOT 与 trimming](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/nativeaot-build-integration.zh-CN.pub.md) | How-to |
| [静态分析嵌入](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/static-analysis-embedding.zh-CN.pub.md) | How-to |
| [PUC Lua prototype 导入](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/puc-prototype-import.zh-CN.pub.md) | Reference |
| [迁移到 0.13](https://github.com/dlqw/Lunil/blob/v0.13.0/docs/migration-0.13.0.zh-CN.pub.md) | Migration guide |

## 兼容性

- 默认语言为 Lua 5.4.8；继续提供显式 Lua 5.1–5.5 契约。
- 稳定线为 `0.13.x`；0.12 到 0.13 的变化见迁移指南。除迁移指南明确列出的项目外，
  既有 .NET 10 host 入口保持源码兼容。
- Release bundle：`win-x64`、`win-arm64`、`linux-x64`、`linux-arm64`、`osx-x64`、`osx-arm64`。
- CLR 互操作默认关闭并 fail closed。可信 .NET host 可显式选择 `RegistryThenReflection`；
  NativeAOT、IL2CPP 与 trimming 使用生成 binding 的 `RegistryOnly`。
