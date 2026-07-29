# 在可移植 .NET 应用中托管 Lunil

[English](portable-hosting.pub.md)

本 how-to 在 .NET 8 或其他兼容宿主中使用 Lunil 的 `netstandard2.1` 资产。可移植资产始终
使用解释器，不会加载或探测 `Reflection.Emit`。

## 前置条件

- 面向 .NET 8+ 或兼容 `netstandard2.1` runtime 的项目。
- 来自 release package source 的 `Lunil.Hosting` `0.13.0`。

## 1. 引用 host

```xml
<PackageReference Include="Lunil.Hosting" Version="0.13.0" />
```

可移植应用不要引用 `Lunil.CodeGen.Cil`。它是 .NET 10 dynamic-code backend，不会进入
Unity、Godot、NativeAOT 或 portable package。

## 2. 显式选择解释器

```csharp
using Lunil.Hosting;
using Lunil.Runtime.Execution;

using var host = new LuaHost(LuaHostOptions.Restricted with
{
    ExecutionBackend = LuaHostExecutionBackend.Interpreter,
});

var run = host.RunUtf8("return 40 + 2", "@portable/main.lua");
if (!run.CompilationSucceeded)
    throw new InvalidOperationException(string.Join("\n", run.Compilation.Diagnostics));
var execution = run.Execution;
if (execution is null || execution.Signal != LuaVmSignal.Completed)
    throw new InvalidOperationException("Lua execution did not complete.");

Console.WriteLine(execution.Values[0].AsInteger());
```

预期输出为 `42`。

## 3. 添加有边界预算的游戏循环

需要在 Update 或 FixedUpdate 边界恢复工作时使用 `LuaGameLoopHost`：

```csharp
using var loop = new LuaGameLoopHost(new LuaGameLoopHostOptions
{
    HostOptions = LuaHostOptions.Restricted with
    {
        ExecutionBackend = LuaHostExecutionBackend.Interpreter,
    },
    MaximumCallbacksPerTick = 256,
    MaximumInstructionsPerTick = 250_000,
});

var compilation = loop.Host.CompileUtf8(
    "value=1; coroutine.yield(); value=value+1; return value");
var operation = loop.Start(compilation);
loop.Tick();
loop.Tick();
```

构造线程拥有 `Tick`、`TickFixed` 和 `Dispose`。后台 completion 应通过
`ILuaGameLoopDispatcher` 回到该线程。

## 4. 验证示例

```bash
dotnet run --project samples/Lunil.Portable.Hosting
```

示例会报告两帧和结果 `2`。

## 可移植限制

- JIT 资产不存在，因此 `LuaHostExecutionBackend.Jit` 会失败。
- 只有提供精确 capability 和 allowlist 后才会启用 CLR 互操作。
- AOT 或 IL2CPP 必须使用生成 binding 的 `LuaClrBindingMode.RegistryOnly`。
- 除非宿主提供实现，否则不能加载 native module。

下一步：[engine-neutral game-loop hosting](game-engine-hosting.zh-CN.pub.md)与
[AOT CLR binding](aot-bindings.zh-CN.pub.md)。
