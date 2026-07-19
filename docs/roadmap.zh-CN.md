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

Lunil 0.10.0 计划完整支持 Lua 5.1、Lua 5.2、Lua 5.3、Lua 5.4 和 Lua 5.5。每个版本都拥有
独立的语言和运行时契约：

- 对应版本的语法、词法规则、运算符和多返回值行为；
- 对应版本的 VM 指令和 binary chunk 格式，并进行显式版本校验；
- 对应版本完整标准库接口和错误行为；
- coroutine、metatable、弱表、finalizer、debug、资源计数以及 close/yield 行为；
- 版本感知的 source、chunk、compiler、interpreter 和 host 配置 API；
- 每个版本独立的 conformance 和 differential 覆盖。

Lua 5.4 仍是 0.9.x 兼容基线。0.10.0 契约必须允许宿主显式选择每个版本，不能静默套用其他版本
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

## 0.11.0：完整 CLR 互操作

0.11.0 计划提供完整且受能力控制的 CLR bridge：

- CLR 类型发现与构造；
- static/instance member、property、indexer、field、method、operator 和 event；
- overload resolution、可选参数和命名参数、generic method/type、array、enum、nullable、tuple
  以及值/引用转换；
- Lua function 到 CLR delegate 的转换，以及 CLR delegate/event 回调 Lua；
- CLR exception、取消、async/task 值和确定性的错误转换；
- 明确的生命周期、dispose、ownership、线程、reflection 和 sandbox capability 规则；
- 在 interpreter、JIT、NativeAOT、trimming 及全部支持的 Lua 版本中保持一致。

该 bridge 属于公开 Hosting contract，不是无限制的 reflection 逃逸入口。

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
