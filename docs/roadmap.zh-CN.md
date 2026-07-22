# Lunil 路线图

[English](roadmap.md)

本文档集中记录未来三个版本线的路线图。每个版本拥有独立的主要范围，后续版本不会改变之前
版本已经发布的兼容契约。

## 历史版本

| 版本线 | 已交付范围 |
| --- | --- |
| `0.1.0` | 建立 Lua 5.4.8/.NET 编译器基础：保真词法和语法、语义绑定、canonical register IR、verifier、基线解释器、closure、table 与 Lua value 表示。 |
| `0.2.0` | 增加逻辑堆、增量/分代 GC、弱表、ephemeron、finalizer、metatable、受保护错误、`<close>`、table 存储和运行时分配基线。 |
| `0.3.0` | 增加 continuation ABI、可恢复 native call、activation-stack coroutine 调度、Lua coroutine 库以及 GC 可见的 coroutine 状态。 |
| `0.4.0` | 增加 PUC Lua 5.4 prototype 转换、binary chunk 加载/执行、完整 opcode 转换、debug provenance 和 chunk verifier。 |
| `0.5.0-alpha.1` | 确立 Lunil 产品身份、六 RID 打包、公开文档、SemVer 发布通道、NuGet 分发和仓库发布边界。 |
| `0.6.0-alpha` | 扩展标准库和 execution kernel，引入 typed CIL/JIT 与 persisted-CIL 实验，并统一编译后端的计数和 fallback 契约。 |
| `0.7.0` | 发布稳定编译器、分析、hosting、workspace、CLI、Lua 5.4.8 chunk 支持、标准库和首个稳定托管执行产品。 |
| `0.8.0` | 交付稳定运行时/JIT 性能线、带守卫的 table/direct-call 路径、精确 64 位计数、NativeAOT/trimming 支持，并移除 persisted/static Lua AOT 产品。 |
| `0.9.0` | 交付共享带守卫 Tier 2 字符串/table 路径、自适应 JIT 基线、版本化性能数据集、六 RID 发布包，以及稳定的 .NET 10/Lua 5.4.8 版本。 |

## 0.10.0：Lua 版本兼容与运行时对比

Lunil 0.10.0 为 Lua 5.1、Lua 5.2、Lua 5.3、Lua 5.4 和 Lua 5.5 提供显式且可选择的契约。
稳定版 `0.10.0` 已交付独立 binary chunk codec、Lua 5.1 函数环境兼容
（`getfenv`/`setfenv`/`module`）、多版本语义/JIT fixture 以及跨运行时性能接入。
0.10.x CI 还会构建经哈希固定的五个官方 PUC Lua oracle，执行 source 与 chunk differential
测试。NeoLua、Luau、GopherLua、Wasmoon 和 UniLua 的可选 benchmark 数据不属于正确性证据。
每个版本都拥有独立的语言和运行时契约：

- 对应版本的语法、词法规则、运算符和多返回值行为；
- 对应版本的 VM 指令和 binary chunk 格式，并进行显式版本校验；
- 对应版本范围内的标准库接口和经过测试的错误行为；
- coroutine、metatable、弱表、finalizer、debug、资源计数以及 close/yield 行为；
- 版本感知的 source、chunk、compiler、interpreter 和 host 配置 API；
- 每个版本的 checked-in 语义 fixture 和 PUC differential 覆盖。

Lua 5.4 仍是默认兼容基线。0.10.0 契约允许宿主显式选择每个版本，不能静默套用其他版本
的语义。

性能数据集新增以下独立运行时：

| 运行时 | 主要语义 | 实现方式 | 对比分组 |
| --- | --- | --- | --- |
| [NeoLua](https://github.com/neolithos/NeoLua) | Lua 5.3 风格 | C# / .NET DLR | 托管 .NET |
| [UniLua](https://github.com/xebecnan/UniLua) | Lua 5.2 | 纯 C# | 托管 / Unity |
| [Luau](https://github.com/luau-lang/luau) | Lua 5.1 兼容方言 | C++ VM | 生产级 Lua 方言 |
| [Wasmoon](https://github.com/ceifa/wasmoon) | 官方 Lua 5.4 | WebAssembly 与 JavaScript 绑定 | Lua 5.4 语义对照 |
| [GopherLua](https://github.com/yuin/gopher-lua) | Lua 5.1 | Go VM 与 compiler | 外部语言嵌入式 VM |

公开数据行标注确切运行时版本和源码身份。Lua 5.1–5.5、LuaJIT 以及方言运行时按语义分组，
不合并成一个总分。

## 0.11.0：受能力控制的 CLR 互操作

0.11.0 在不改变 Lua 5.1–5.5 语言契约含义的前提下增加由 Host 管理的 CLR bridge。该 bridge
默认 opt-in，并受能力控制；Restricted host 只有显式提供 allowlist 后才能使用。

| 里程碑 | 用户可见范围 | 依赖 | 验收标准 | 公开接口与验证 |
| --- | --- | --- | --- | --- |
| `0.11.0-alpha.1` | 发现 allowlist 中的 CLR 类型、构造 allowlist 中的对象，并以明确 ownership 的 Lua userdata 表示。 | 现有 host profile、userdata ownership、语言版本契约。 | Restricted host 默认拒绝发现和构造；assembly 与类型名精确匹配；构造参数使用确定性转换并拒绝不支持的值；不允许任意加载 assembly；interpreter 与 JIT 结果一致。 | Hosting API、互操作指南、能力配置示例、host 测试、trimming 分析和 package consumer smoke。 |
| `0.11.0-alpha.2` | 调用 allowlist 中的 static/instance method、property、field、indexer 和 operator。 | Alpha.1 对象身份和转换规则。 | 成员查找受 allowlist 限制并按类型身份缓存；overload resolution 确定性；可选/命名参数、enum、nullable、array、tuple 和数值转换规则明确；不可访问成员产生稳定 Lua 诊断。 | Hosting API 参考、转换矩阵、成员解析测试和 NativeAOT/trimming fixture。 |
| `0.11.0-alpha.3` | Lua function 转换为 CLR delegate，并把 CLR callback/event 回调到 Lua。 | Alpha.2 调用和 host 调度边界。 | 订阅前校验 delegate 签名；callback 保持 state ownership、错误边界、取消和 coroutine 规则；event subscription 可释放，且不会保留未 root 的 Lua closure。 | Callback 指南、生命周期示例、delegate/event 测试、GC 与重入覆盖。 |
| `0.11.0-beta.1` | 增加 Task/`ValueTask`、取消、`ref`/`out`、异常转换、dispose 和线程策略。 | Alpha.3 callback 生命周期和 scheduler 契约。 | 异步结果拥有唯一文档化的 Lua 表示；取消和 CLR exception 映射到稳定诊断；`ref`/`out` 结果保持顺序；dispose 幂等；不支持的线程调用 fail closed。 | 迁移说明、错误契约、异步和失败路径测试、package 与 publish-mode 验证。 |
| `0.11.0-rc.1` | 冻结全部支持 Lua 版本和运行时发布模式下的互操作契约。 | Beta.1 完整行为和兼容性基线。 | Lua 5.1–5.5 矩阵通过；interpreter/JIT/NativeAOT/trimming/ReadyToRun 一致；allowlist 与生命周期规则完整公开；API 和 package baseline 可复现。 | API/package baseline、发行说明、兼容性矩阵、六 RID bundle smoke，以及 CLI/consumer 示例。 |

该 bridge 属于公开 Hosting contract，但绝不是无限制的 reflection 逃逸入口。Host 必须声明
Lua 可以观察的 assembly、类型、成员、构造策略和生命周期策略。

## 0.12.0：完整热更新支持

0.12.0 计划为长时间运行的 host 提供安全热更新：

- 不重启 host 替换 module 和 source；
- 替换 function 与 closure，并提供明确的 capture/upvalue 迁移规则；
- 为 interpreter、JIT、CLR interop 和缓存 call site 提供 generation-aware invalidation；
- 定义运行中 call、coroutine、yield、callback 和 pending task 的 active-frame 行为；
- 提供发布前校验、事务式更新、rollback 和失败隔离；
- 为 table、userdata、resource 和 host-owned value 提供 state migration hook；
- 提供可观察的更新状态、诊断、版本身份和 capability 限制。

更新不能改变已经发布的 Lua 版本语义，也不能让 active frame 留在无效的 code 或 value identity
上。


0.11.0 的全部 milestone 已在稳定版完成。
