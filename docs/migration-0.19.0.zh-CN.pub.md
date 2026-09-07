# 从 Lunil 0.18 迁移到 0.19

[English](migration-0.19.0.pub.md)

Lunil 0.19 保持 0.18 的 compiler、runtime、hosting、analysis、tooling 与 engine 入口源代码兼容。
本版本加固宿主集成边界：宿主 CLR exception 被包含为 Lua error，JIT 强制策略快速失败，热重载与
FFI 清理场景下的资源预算有界化，调试器按协议 id 关联请求。二进制 chunk 读取器共享同一解析核心
与统一资源限制。多数升级无需修改源代码；请查阅下方四条行为说明。

## 1. 更新包与工具

将所有 Lunil 包引用作为一个兼容线统一更新：

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.19.0" />
<PackageReference Include="Lunil.Hosting" Version="0.19.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.19.0
```

## 2. 宿主 CLR exception 成为可捕获的 Lua error

native function 或 resumable native 抛出任意 CLR exception 时，所在 protected call 现在以一个
Lua error 失败，其文本为 exception 类型与 message（例如 `System.IO.IOException: disk full`），
因此 `pcall` 可以守护宿主调用，执行随后继续。无保护路径表现为携带转换后错误的
`LuaRuntimeException`，而不是让原始宿主 exception 终止虚拟机。

- 依赖宿主 exception 中止进程或穿透 `pcall` 的代码，现在必须处理该 Lua error，或让 exception
  派生自新增的 `Lunil.Runtime.LuaHostException` 以保留类型化透传契约。
- `LuaClrException` 派生自 `LuaHostException`；CLR 互操作失败保持既有的类型化行为不变。

## 3. `RequireJit` 在无动态代码时快速失败

`LuaHostExecutionBackend.Auto` 与 `RequireJit` JIT 策略组合时，在无 dynamic-code 能力的运行时
（NativeAOT、IL2CPP、`netstandard2.1`）上于 `LuaHost` 构造期抛出
`PlatformNotSupportedException`。此前宿主会静默回退到解释器。需要旧行为的宿主应使用
`PreferJit` 或不带 `RequireJit` 的 `Auto` JIT 策略。

## 4. 统一的二进制 chunk 资源限制

各版本 chunk 读取器共享同一默认限制来源。Lua 5.1 与 5.2 读取器采用 Lua 5.3/5.4/5.5 的默认值：
原型深度 128 → 200、指令数 4,000,000 → 10,000,000、upvalue 数 100,000 → 1,000,000、字符串字节
16 MiB → 64 MiB、调试条目数 2,000,000 → 10,000,000。这只是放宽：此前可接受的 chunk 仍然可接受，
此前被拒绝的大而合法的 chunk 现在可以加载。

## 5. `collectgarbage` 调参生效

`collectgarbage("setpause", v)` 与 `collectgarbage("setstepmul", v)` 现在按 PUC 风格的比例语义
缩放重启收集的分配债务与逻辑收集器每步工作量（默认值 200 与 100 保持配置的节奏不变）。此前调用
这些开关的脚本不会观察到任何变化；升级时请复核收集节奏预期。

## 6. 兼容性清单

- 没有移除任何公共成员；0.18 API 面保持源代码兼容。
- 新增公共 API：`Lunil.Runtime.LuaHostException`、`Lunil.IR.LuaChunkFormatException`（各版本
  chunk exception 的公共基类）与 `Lunil.IR.LuaChunkCodec`（单一 chunk 格式分发点）。
  Lunil.Analysis 中的 `LuaClassFactoryScanner` 公开类工厂源码扫描。`LuaClrException` 现派生自
  `LuaHostException`。
- 长驻宿主：使编译模块失效会在内部上限之外回收 JIT 记账数据，销毁 FFI context 会关闭存活
  buffer 并返还分配预算。
- DAP：游戏循环 attach 中继按 `id`（响应按 `request_seq`）关联请求；请求 `id` 与 `seq` 不同的
  客户端现在按协议预期工作。
- `api/0.19.0/` 基线取代 `api/0.18.0/` 成为冻结兼容线。
