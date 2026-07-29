# Host Lunil in a portable .NET application

[简体中文](portable-hosting.zh-CN.pub.md)

This how-to runs Lunil through its `netstandard2.1` asset on .NET 8 or another compatible host.
The portable asset always uses the interpreter and never loads or probes `Reflection.Emit`.

## Prerequisites

- A project targeting .NET 8+ or a `netstandard2.1`-compatible runtime.
- `Lunil.Hosting` version `0.13.0` from the release package source.

## 1. Reference the host

```xml
<PackageReference Include="Lunil.Hosting" Version="0.13.0" />
```

Do not reference `Lunil.CodeGen.Cil` from a portable application. It is the .NET 10 dynamic-code
backend and is intentionally absent from Unity, Godot, NativeAOT, and portable packages.

## 2. Select the interpreter explicitly

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

Expected output: `42`.

## 3. Add a bounded game loop

Use `LuaGameLoopHost` when work must resume at Update or FixedUpdate boundaries:

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

The construction thread owns `Tick`, `TickFixed`, and `Dispose`. Use an
`ILuaGameLoopDispatcher` to marshal background completions to that thread.

## 4. Verify the sample

```bash
dotnet run --project samples/Lunil.Portable.Hosting
```

The sample reports two frames and result `2`.

## Portable limitations

- `LuaHostExecutionBackend.Jit` fails because the JIT asset is not present.
- CLR interoperation is disabled until exact capabilities and allowlists are supplied.
- For AOT or IL2CPP, use `LuaClrBindingMode.RegistryOnly` with generated bindings.
- Native module loading is unavailable unless the host supplies an implementation.

Next: [engine-neutral game-loop hosting](game-engine-hosting.pub.md) and
[AOT CLR bindings](aot-bindings.pub.md).
