# 从 Lunil 0.21 迁移到 0.22

[English](migration-0.22.0.pub.md)

Lunil 0.22 将引擎内部重构为实例化组件与 registry-only 的 CLR interop。它是 1.0 前的 minor
版本：Lua 语言行为、已验证 chunk 契约、分层执行语义以及 `LuaHost` 的构造流程保持不变；
Hosting 与 CLR interop 的 .NET API 有部分重命名或移除。以下变更只影响使用该表面的宿主。

## 1. 为每个暴露给 Lua 的 CLR 类型生成 binding

CLR interop dispatch 现在只走 registry。`LuaClrBindingMode` 枚举与
`LuaClrOptions.BindingMode` 属性已移除，reflection fallback 桥接也一并删除：member 缓存、
reflection 重载选择、reflection 调用、reflection 构造 delegate、已加载程序集类型扫描，以及
基于 `Activator` 的构造都不复存在。

- 暴露给 Lua 的每个类型、constructor、member 与 delegate 都需要注册 binding。按
  [AOT CLR binding](aot-bindings.zh-CN.pub.md) 的说明用 `[LuaClrGenerateBinding]` 声明请求并
  注册生成的 provider。
- 没有注册 binding 的 allowlist 类型会以稳定的 `no registered static binding` 错误失败，
  不再经 reflection dispatch。
- 启用 CLR interop 而未提供 `BindingRegistry` 现在是配置错误。
- indexer 无法绑定；indexer 访问以 member-not-found 错误 fail closed。
- 转换层的改进是共享的：task 与 value-task 结果、枚举、数组、集合和值类型默认参数在
  registry 路径上都通过缓存的强类型 delegate 封送。

依赖 `RegistryThenReflection` 快速访问未绑定类型的宿主应将这些类型迁移到生成 binding；
早期文档中的 AOT 指引本就要求如此，现在它是唯一模式。

## 2. 使用状态持有的运行时运算服务

`LuaRuntimeOperations` 是每个 `LuaState` 持有的 sealed 实例服务，通过 `state.Operations`
获取。语义运算保留名称与行为，只是去掉了首个 state 参数：

| 0.21 | 0.22 |
| --- | --- |
| `LuaRuntimeOperations.GetIndex(state, target, key)` | `state.Operations.GetIndex(target, key)` |
| `LuaRuntimeOperations.SetIndex(state, target, key, value)` | `state.Operations.SetIndex(target, key, value)` |
| `LuaRuntimeOperations.Unary(state, op, operand)` | `state.Operations.Unary(op, operand)` |
| `LuaRuntimeOperations.Binary(state, op, left, right)` | `state.Operations.Binary(op, left, right)` |
| `LuaRuntimeOperations.ResolveCall(state, callable, args)` | `state.Operations.ResolveCall(callable, args)` |

该服务在构造时捕获状态的版本画像，各版本的 coercion 与排序规则经捕获字段解析。
`LuaValueOperations` 的值级计算保持不变。

## 3. 更新引擎宿主的运行时注册表

`LuaGodotRuntimeRegistry` 与 `LuaUnityRuntimeRegistry` 是带进程级默认实例的 sealed 实例类：

| 0.21 | 0.22 |
| --- | --- |
| `LuaGodotRuntimeRegistry.ActiveHostCount` | `LuaGodotRuntimeRegistry.Process.ActiveHostCount` |
| `LuaGodotRuntimeRegistry.DisposeAll()` | `LuaGodotRuntimeRegistry.Process.DisposeAll()` |
| `LuaUnityRuntimeRegistry.DisposeAll()` | `LuaUnityRuntimeRegistry.Process.DisposeAll()` |

`LuaGodotGameLoop` 与 `LuaUnityGameLoop` 暴露可设置的 `Registry` 属性，默认指向进程注册表；
将其指向独立实例即可让循环免受场景重载或 play-mode 关闭的影响。Unity 编辑器生命周期
钩子与 play-mode 重置继续使用进程实例，行为不变。

## 4. 复查可观察的库细节

- 读取宿主配置的标准库模块（basic、io、os、package、debug）改为内部实例模块安装。公共
  `LuaStandardLibrary` 门面与重装重配置行为不变。
- `os.time`、`dofile`、`require` 与 `debug.debug` 现在通过按安装生成的 native closure 注册，
  因此 `debug.getinfo` 对 `os.time`、`dofile`、`debug.debug` 报告一个捕获 upvalue，对
  `require` 报告三个。`print` 仍为零个。
- FFI binding generator 以 analyzer 资产随 Lunil.StandardLibrary 包分发：
  `[assembly: LuaFfiGenerateBinding("lib", "symbol", "i32(i32, i32)")]` 会生成强类型的原生
  invoker，可在 NativeAOT 与 trimmed 运行时使用。参见[使用原生 FFI](ffi.zh-CN.pub.md)。

## 5. 未变化的部分

- 分层执行（tier 0.5 解释器、tier 1/2、loop OSR）、JIT 阈值与后端选择语义完全一致；JIT 的
  缓存与 warmup 状态移到了实例持有者。
- 生成代码 ABI 入口（`LuaCodegenAbiV1`-`V5`）保持原有静态签名。
- `LuaHost`、`LuaGameLoopHost`、patch/hot-update 契约、语言服务器与调试适配器的公共面不变；
  除上文所列 CLR 选项变化外，其余 options record 不变。
