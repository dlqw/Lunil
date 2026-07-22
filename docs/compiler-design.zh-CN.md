# Lunil 编译器与运行时技术设计

本文介绍 Lunil 的公开架构、兼容性边界和可复现的验证方法，面向使用者、集成者和希望理解实现原理的开发者。

## 1. 目标与范围

Lunil 使用 C# 实现 Lua 5.4 编译器、运行时和静态分析工具链，提供：

- Lua 5.4 源码解析、编译和执行；
- Lua 语义、协程、元方法、逻辑垃圾回收和标准库；
- 基线解释器以及在 CoreCLR 动态代码环境中的分级 CIL JIT；
- PUC Lua 5.4 binary chunk 的读取与生成；
- LuaLS 注解和可选的旧 EmmyLua 方言；
- 基于注解的类型、流和模块分析；
- 内容寻址的编译分析缓存；
- 可嵌入、可限制能力并支持确定性执行的宿主 API。

核心实现不依赖 PUC Lua、C/C++、LLVM 或其他本机编译器。原生 Lua C API 和依赖 `lua54.dll` 的 C 模块不属于兼容性承诺。

## 2. 兼容性契约

### 2.1 Lua 语义

以下行为按 Lua 5.4 语义实现，并通过官方测试与差分测试验证：

- 词法、语法、作用域、求值顺序和多返回值调整；
- 64 位整数、双精度浮点数、位运算和数值转换；
- 闭包、upvalue、`_ENV`、尾调用和 vararg；
- 元表与元方法查找、调用顺序和错误传播；
- 协程的 create/resume/yield/wrap/close；
- `<const>`、`<close>`、to-be-closed 变量；
- 弱表、ephemeron、finalizer 和对象复活的可观察语义；
- binary chunk、`string.dump` 和标准库 API；
- debug hook、局部变量和 upvalue 的可观察身份。

未规定的表遍历顺序、对象默认文本、GC 的精确触发时刻、平台 I/O 和浮点库最后一位差异不作为程序正确性的依赖。

### 2.2 明确不支持

- Lua C API ABI 和原生 C loader；
- 把 CoreCLR 生成的机器码作为跨进程缓存；
- NativeAOT 进程中的动态 CIL 生成；此环境使用解释器；
- 把注解类型当作无需运行时守卫的事实。

`package.cpath`、`package.loadlib` 和 C loader searcher 保留稳定的“不支持原生模块”诊断。`lightuserdata` 仍是 Lua 类型，但不会暴露不稳定的 CLR 地址。

## 3. 全局架构

源码和 binary chunk 都归一到同一个经过验证的 Lua 5.4 canonical IR：

```text
Lua 字节源码 ── Lexer/CST ── Binder/分析 ── Canonical IR ─┬─ 解释器
                                                           └─ CIL JIT
PUC Lua chunk ── Reader/Verifier ──────────────────────────┘
                             │
                   统一 Runtime ABI、元方法和逻辑 GC
                             │
                    标准库与宿主能力提供器
```

架构不变量：

1. 所有执行后端消费同一份已验证 IR。
2. 元方法、错误、yield、hook 和 GC 安全点共享同一运行时语义。
3. CLR 调用栈不是 Lua 协程栈；可 yield 的调用保存显式 Lua frame。
4. 可能分配或触发 GC 的位置都有根集合和活跃寄存器信息。
5. 表、闭包、线程、userdata 和 upvalue 写入均经过 Lua GC barrier。
6. 任何专门化都必须用类型、metatable 和版本守卫保护，失败时回到通用路径。
7. 缓存损坏、缺失或不匹配只会导致重新分析或编译，不改变程序语义。
8. 文件、环境、进程、locale、时钟和控制台访问都通过宿主能力接口。

## 4. 源码与语法前端

Lua 字符串和源码以字节为真实表示。`SourceText` 保留不可变字节，同时记录 byte offset、行号、UTF-16 列和可选的 UTF-8 code-point 列。加载器分别处理 BOM、shebang、`load(string)`、reader function 和文件输入。

手写 lexer 与递归下降/Pratt parser 生成 immutable green tree 和按需 red tree：

- CST 保留 token、空白、注释、注解和错误 token；
- 严格编译与 IDE 容错共享语法定义，区别只在恢复策略；
- 嵌套深度、token、节点和诊断数量均有预算；
- 错误恢复必须前进，恶意输入不能导致无限循环或无界递归；
- Binder 从 CST 生成 Bound Tree/HIR，运行时只消费 canonical IR。

前端明确处理 Lua 边缘语义：最后一个调用或 vararg 的多返回值、赋值左值先固定、短路表达式返回原操作数、`local x = x` 的外层读取、`_ENV` 词法捕获、goto 的作用域限制、to-be-closed 变量的逆序关闭、整数溢出和 floor division、整数/浮点 table key 归一、`next` 删除后继续遍历、`#table` border 查找以及真实 upvalue cell 身份。

## 5. 注解与静态分析

默认注解方言为 LuaLS，也可启用 legacy EmmyLua。两种解析器共享 annotation lexer 和公共类型 AST；组合模式优先 LuaLS，只有 LuaLS 不接受时才尝试 legacy parser，歧义采用 LuaLS 并给出兼容性提示。未知标签及原始 payload 保留在 CST 中。

类型系统支持 `any`、`unknown`、`never`、nil、literal、union、intersection、class、alias、enum、结构化 table、数组、map、function、overload、generic、vararg、tuple/type pack、nullable 和 optional field。分析可识别 `x ~= nil`、`type(x) == "string"`、`assert(x)`、短路表达式、判别字段和 `---@cast` 等收窄形式。

分析结果是不可变快照，包含绑定、模块依赖、约束、控制流图、类型收窄、诊断和 suppression。递归类型、循环模块依赖和过深泛型实例化采用预算和稳定 widening；取消操作不会发布不完整快照。注解和静态类型在运行时擦除，任何基于它们的优化都必须重新检查实际 Lua tag、metatable 和版本。

## 6. Lua 5.4 Canonical IR

Canonical IR 接近 PUC Lua 5.4 寄存器 VM，同时增加：

- 基本块和 continuation PC；
- `Call`、`TailCall`、`Yield`、`Close`、`VarArg` 与开放结果区间；
- 指令副作用标记（分配、元方法、yield、错误和 hook）；
- GC 活跃寄存器、upvalue 映射、源码位置和调试变量范围；
- 可由独立 verifier 检查的不变量。

源码 lowering 与 chunk 导入必须生成同一 IR 数据模型。寄存器窗口遵循 Lua 的开放调用/返回协议；跳转可显式关闭 open upvalue 和 to-be-closed 槽；闭包记录每个 upvalue 来自父寄存器、父 upvalue 或宿主 `_ENV`。优化器可以临时构造 SSA，但协程保存、chunk 互操作和 deopt 恢复都回到 canonical 寄存器模型。

Verifier 检查格式版本、函数编号和父子关系、寄存器/常量/upvalue/函数索引、调用窗口、跳转目标、捕获来源和基本块边界。执行器只接受通过 verifier 的模块。

## 7. PUC Lua 5.4 Binary Chunk

chunk 子系统由 reader、writer、verifier、prototype 和 instruction codec 组成。读取器验证 signature、版本、`LUAC_DATA`、指令/整数/浮点宽度、endianness 哨兵、变长 size 编码、常量、嵌套 prototype 和调试表。写入器可按目标格式输出，遇到不支持的数值表示必须明确拒绝。

外部 chunk 一律视为不可信输入。验证包括长度和递归预算、opcode 与索引、跳转目标、`LOADKX`/`EXTRAARG` 与 `SETLIST` 配对、寄存器区间、开放调用、`TBC`/`CLOSE` 关闭状态以及调试表一致性。导入后为 prototype 分配连续函数编号并转换为 canonical continuation PC；源码和 chunk 共享解释器、continuation ABI、逻辑 GC 和 owner 检查。内部缓存格式使用独立 magic，不能伪装成 PUC chunk。

## 8. 运行时数据模型

### 8.1 LuaValue 与字符串

LuaValue 使用引用字段加 64 位 payload 的紧凑表示，不用 explicit layout 重叠 CLR 引用和数值：

```csharp
internal readonly struct LuaValue
{
    private readonly object? _tagOrReference;
    private readonly long _payload;
}
```

整数、浮点和布尔值不装箱；字符串、表、闭包、线程和 userdata 保存对象引用。LuaString 以不可变字节存储，短字符串可驻留，长字符串按内容 hash；驻留表参与逻辑 GC，拼接使用有预算的临时 builder/rope。

### 8.2 Table 与逻辑 GC

Table 使用 array/hash 混合结构，hash part 采用开放寻址和 tombstone，以支持合法的 `next` 继续遍历。storage、metatable 和 shape 都有版本，供 inline-cache 守卫和调试失效使用。弱键、弱值、ephemeron、finalizer、对象复活、NaN key 拒绝、整数/浮点 key 归一和写屏障都属于 table 语义的一部分。

.NET GC 不直接替代 Lua GC。LuaHeap 维护增量/分代三色状态、gray/gray-again、old-to-young 记忆集、弱表和 finalizer 队列。Lua frame、registry、host handle、module cache 和待处理 coroutine 是显式根；宿主长期持有 Lua 对象必须使用注册到根集合的 handle。每个安全点记录根、frame top、open upvalue、to-be-closed 状态和预算，保证 yield、错误和 deopt 可恢复。

### 8.3 栈、闭包与协程

Lua 栈使用连续 slot 和显式 top；多返回值通过结果区间传递，不创建参数数组。闭包捕获稳定的 upvalue cell；调试修改 cell 会使相关优化失效。协程保存显式 Lua frame、continuation PC、结果窗口和关闭状态，不能依赖 CLR stack frame。yield、错误展开、`coroutine.close` 和 `<close>` 按统一 scheduler 处理。

## 9. 执行后端

### 9.1 解释器

reference interpreter 逐条执行 canonical IR，维护精确 canonical PC、指令预算、hook/debug epoch、GC safe point 和 owner 生命周期。所有复杂 Lua 语义（元方法、call、yield、close、错误展开）在共享 scheduler 中完成。

### 9.2 CoreCLR CIL JIT

JIT 先验证 IR，再构造包含 label、local、调用签名、sequence point 和 GC live-register map 的 method plan。可证明安全的 primitive 操作和数值区域直接生成 CIL；元方法、复杂 call、yield、close、hook 和不合格守卫回到 Runtime ABI slow path。Guard 失败从同一 canonical PC 进入通用路径，不重放已经完成的副作用。

分级编译、inline cache、loop OSR、deoptimization 和代码预算均绑定 module owner，使用有界队列和可取消的编译请求。OSR 只在已验证的自然循环回边建立 entry map；进入和退出都物化完整 Lua frame、GC roots 和 close 状态。动态代码不可用时不初始化 Reflection.Emit，自动使用解释器。

### 9.3 NativeAOT

NativeAOT 是宿主发布方式，不是额外的 Lua 后端。compiler、workspace、runtime、CLI 和 interpreter 通过 trimming/NativeAOT 发布；动态代码不可用时保持相同的 canonical IR 和解释器语义。

## 10. 标准库与宿主能力

标准库覆盖 basic、coroutine、package、string、utf8、table、math、io、os 和 debug。Lua pattern、`string.pack/unpack`、`string.format`、`math.random`、debug hook、weak table、`__gc`、`__close` 和 `coroutine.close` 使用 Lua 规则实现，而不是直接转发 .NET 等价物。

宿主通过 `IFileSystem`、`IProcessService`、`IEnvironmentProvider`、`IClock`、`IRandomSource`、`IModuleResolver` 和 `IConsole` 注入能力。Full、Sandbox 和 Deterministic 配置控制默认能力；路径穿越、符号链接、设备文件、进程和环境访问由 capability provider 统一限制。

## 11. 缓存、可观测性与错误

编译分析缓存按 module/source identity 和内容 hash 寻址，失配或损坏时安全重建。JIT plan、delegate 和 profile 只在当前进程内存中存在，并绑定 module owner；profile 导入必须校验内容身份、策略版本和完整指令流，失败时回到空 profile。

诊断包含稳定 code、severity、源码范围和可选修复建议。Lua traceback 与宿主异常分开保存，跨边界组合时不丢失 Lua error value。默认遥测只记录计数、路由、缓存和优化结果，不记录源码、字符串内容或宿主机密。

## 12. 项目边界与验证

主要组件包括 Syntax、EmmyLua、Analysis、Semantics、IR、Runtime.Abstractions、Runtime、StandardLibrary、CodeGen.Cil、Compiler、Workspace、Hosting 和 CLI。依赖方向保持单向：Syntax/EmmyLua 不依赖 Runtime，Runtime 不反向依赖 Compiler，跨层数据结构放在最窄的公共抽象中。

可复现验证包括：

- Lua 5.4.8 官方测试与固定 PUC Lua oracle 差分测试；
- interpreter、CIL JIT、loop OSR 和 NativeAOT interpreter 结果一致性；
- parser、chunk、pattern、pack/unpack、table、类型解析和恶意输入 fuzz；
- GC、弱表、ephemeron、finalizer resurrection 专项测试；
- Windows/Linux/macOS 与 x64/arm64 的兼容性测试；
- 正常优化与 debug/hook 模式分别验证；
- chunk 与 PUC Lua 的双向互操作测试；
- 冷启动、稳态吞吐、分配、编译延迟和发布体积的可重复基准。

基准和测试命令应使用仓库中公开的脚本与固定输入，以便社区独立复现结果。
