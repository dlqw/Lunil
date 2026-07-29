# 从 Lunil 0.12 迁移到 0.13

[English](migration-0.13.0.pub.md)

Lunil 0.13 新增可移植 package asset 与游戏引擎 hosting。它是 1.0 前的 minor release，包含
Hosting 和 CLR 互操作的源码级变化。既有 Lua language 与已验证 chunk 契约仍按版本显式选择。

## 1. 选择执行 asset

`Lunil.Core`、language service、compiler、IR、runtime、standard library、workspace 与 Hosting
现在同时提供 `netstandard2.1` 和 `net10.0` asset。Portable consumer 使用解释器；.NET 10 host
可以继续使用 `LuaHostExecutionBackend.Auto`。在 portable 或不支持动态代码的 runtime 上显式要求
`Jit` 会抛出 `PlatformNotSupportedException`。

Unity、Godot 或其他 portable host 不要引用 `Lunil.CodeGen.Cil`。

## 2. 更新 Hosting JIT 契约名称

公开 host 不再通过 Hosting property 暴露 `Lunil.CodeGen.Cil.Jit` type。Host-facing 代码按下表
更新：

| 0.12 | 0.13 |
| --- | --- |
| `LuaHostOptions.Jit: LuaJitExecutorOptions` | `LuaHostOptions.Jit: LuaHostJitOptions` |
| `LuaHost.JitStatistics: LuaJitStatistics?` | `LuaHost.JitStatistics: LuaHostJitStatistics?` |
| `LuaPatchJitWarmupOptions.ExecutorOptions: LuaJitWarmupOptions` | `LuaHostJitWarmupOptions` |
| `LuaPatchJitWarmupModuleResult.Warmup: LuaJitWarmupResult?` | `LuaHostJitWarmupResult?` |

Option field 保留相同的 host policy 含义。直接使用 .NET 10 CIL executor 的代码仍可引用
`Lunil.CodeGen.Cil` 及其 `LuaJit*` type。

## 3. 检查 CLR 转换行为

`LuaClrOptions` 新增 binding mode、enum/decimal representation、collection projection、
conversion limit 与 ref/out result 的显式 policy。重要默认值为：

- `BindingMode = RegistryThenReflection`；
- `EnumRepresentation = Name`；
- `DecimalRepresentation = ExactString`；
- `CollectionProjection = TablesAndIterators`；
- `RefOutRepresentation = PositionalAndNamedTable`。

如果 0.12 代码要求 CLR `decimal` 转换为可能丢失精度的 Lua float，请显式设置
`DecimalRepresentation = LuaClrDecimalRepresentation.LossyFloat`，或调整 Lua 契约以接收默认的
invariant string。

`LuaClrInvocationResult` 现在是带 `NamedRefOutValues` 的 sealed result class；不要继续依赖旧 record
equality、init-only property 或自动 deconstruction。

## 4. 删除重叠 allowlist 形式

同一个 member 只能配置 bare、type-qualified 或 assembly-qualified 中的一种 allowlist 形式。例如不要
同时包含 `Add` 与 `Game.Inventory.Add`。重叠现在返回 `LuaClrErrorCode.BindingConflict`，不会选择
模糊 entry。

AOT host 应生成准确 binding 并设置 `BindingMode = RegistryOnly`；详见
[AOT CLR binding](aot-bindings.zh-CN.pub.md)。

## 5. 按需采用 frame hosting

用 `LuaGameLoopHost` 替换应用自行维护的 Update/FixedUpdate queue，或使用 Unity/Godot adapter。
构造线程拥有 `Tick`、`TickFixed`、`CancelAll` 与 `Dispose`；跨线程 completion 必须通过
`ILuaGameLoopDispatcher` 进入。

默认每 tick 最多 1,024 个 callback、1,000,000 条 canonical instruction，总 queued-work limit 为
65,536。如果旧 host 使用其他预算，应显式设置应用所需值。

## 6. 更新引擎安装

- Unity：安装 `com.dlqw.lunil-0.13.0.tgz`。Unity 2022.3 LTS 与 Unity 6 是彼此独立的正式支持
  target；不要为了使用 Lunil 而升级 2022.3 项目。
- Godot：引用 `Lunil.Godot` `0.13.0`，把 release addon 复制到 `res://addons/lunil`，并使用
  Godot 4.4 或 4.6 .NET editor。

最后，在实际发布的准确 runtime 或 engine target 上运行应用原有的 source、chunk、patch 与 publish
检查。
