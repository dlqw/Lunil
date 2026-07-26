# 从 Lunil 0.7.0 迁移到 0.8.0

[English](migration-0.8.0.pub.md)

Lunil 0.8 移除了 Lua persisted/static AOT 产品。Runtime source/chunk compilation、参考解释器、
managed JIT execution、loop OSR 以及 .NET NativeAOT/trimming 部署仍然可用。

## 1. 移除 Lua AOT 接口

以下 0.7.x capability 没有兼容 shim：

- `LuaAotCompiler`、`LuaPersistedAotExecutor`、`LuaStaticAotExecutor`、
  `LuaStaticAotRegistry` 以及相关 artifact/load/result type；
- `Lunil.CodeGen.Cil.Artifacts`、`Lunil.CodeGen.Cil.Caching` 和
  `Lunil.CodeGen.Cil.Loading` persisted-AOT/cache API；
- persisted PE/PDB artifact、manifest、loader、static registry、disk cache 和生成的 Lua registry；
- `Lunil.Build` package、`Lunil.Build.Tasks` 与 `LunilCompile` MSBuild item。

删除 `Lunil.Build` 引用和 `LunilCompile` item。没有配置开关可以重新启用 static 或 persisted
Lua AOT。

## 2. 替换 runtime 执行方式

通过 hosting API 编译并执行源码：

```csharp
using Lunil.Hosting;

using var host = new LuaHost();
var compilation = host.CompileUtf8("return 40 + 2");
var result = host.Execute(compilation);
```

低层集成可以通过 `LuaInterpreter` 或 `LuaJitExecutor` 执行经过验证的 canonical module。
需要分发预编译输入时，使用 `lunil build --target chunk` 生成可移植 PUC chunk；每个 chunk 都会在
执行前经过验证。

JIT selection 是 runtime 优化，不是 persisted-artifact 模式。动态代码不可用时，`Auto` 与
`PreferJit` 使用参考解释器。

## 3. 删除旧 build 输入

Build output 仅支持 `chunk`。以下旧输入会被拒绝：

```text
lunil build app.lua --target aot
{ "buildTarget": "aot" }
LUNIL_BUILD_TARGET=aot
```

这些输入都会返回 `LUNIL0006`、phase `removed-feature` 和退出码 `2`；CLI 不会静默选择其他
backend。

## 4. 继续使用 .NET NativeAOT

.NET NativeAOT 发布的是 managed host，与已移除的 Lua AOT 产品不同。标准 SDK 属性
`PublishAot` 和 `PublishTrimmed` 仍受支持。发布示例见
[.NET NativeAOT 与 trimming](nativeaot-build-integration.zh-CN.pub.md)。

## 5. 处理其他兼容性变化

`LuaCompiledExit.InstructionsConsumed` 端到端使用 `long`，避免 instruction count 超过
`Int32.MaxValue` 时溢出。
