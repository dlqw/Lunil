# Luac 编译器与运行时技术设计

状态：已批准并进入实现
目标版本：Lua 5.4.8、.NET 10
文档版本：0.2

## 1. 目标与范围

Luac 是完全使用 C# 实现的 Lua 5.4 编译器、运行时与静态分析工具链。项目必须提供：

- Lua 5.4.8 源码解析、编译与执行；
- Lua 5.4 完整语言语义、协程、元方法、逻辑垃圾回收和标准库；
- 基线解释器、基于 CoreCLR CIL 的分级 JIT、持久化 CIL AOT 和 .NET NativeAOT 集成；
- PUC Lua 5.4 binary chunk 的读取与生成；
- 默认 LuaLS 注解方言及可选旧 EmmyLua IDE 方言；
- 基于注解的类型推断、流分析、模块分析和编译器诊断；
- 内容寻址的增量编译缓存；
- 可嵌入、可限制能力并支持确定性执行的宿主 API。

项目不实现 Lua C API，也不承诺加载原生 Lua C 模块。核心实现不依赖 PUC Lua、C/C++、LLVM 或其他本机编译器。允许在隔离且经过审计的底层实现中局部使用 `unsafe` 和 `System.Runtime.CompilerServices.Unsafe`。

## 2. 兼容性契约

### 2.1 严格兼容

以下行为必须通过 PUC Lua 5.4.8 测试套件及差分测试验证：

- 词法、语法、作用域、求值顺序和多返回值调整；
- 64 位整数、双精度浮点数、位运算和数值转换；
- 闭包、upvalue、`_ENV`、尾调用和 vararg；
- 元表与全部元方法；
- 协程的 create/resume/yield/wrap/close 行为；
- `<const>`、`<close>`、to-be-closed 变量和错误传播；
- 弱表、ephemeron、finalizer 及对象复活的可观察语义；
- binary chunk 的读取、生成及 `string.dump`；
- 标准库的 Lua 层 API 与错误条件；
- debug hook、局部变量、upvalue 身份及修改行为。

### 2.2 Lua 允许的实现差异

下列行为不得成为程序正确性的依赖，也不承诺与某次 PUC Lua 运行逐字节相同：

- 未规定的表遍历顺序；
- 默认 `tostring` 中的对象标识文本；
- GC 的精确触发时刻及 `collectgarbage("count")` 的瞬时数值；
- 平台相关的 I/O、进程、locale 和错误文本；
- 浮点数学库在平台实现允许范围内的最后位差异；
- `string.dump` 输出的逐字节一致性，但输出必须可被相同目标格式的 PUC Lua 5.4 读取；
- 优化后内部临时变量的调试名称，公开的 Lua 语义信息除外。

### 2.3 明确不支持

- Lua C API ABI；
- 依赖 `lua54.dll` 符号的 Lua C 模块；
- 将 CoreCLR 已生成的本机机器码作为可移植缓存；
- 在 NativeAOT 进程中动态生成或加载新 CIL；动态 Lua 源码在此环境走解释器；
- 将 EmmyLua/LuaLS 注解作为无需动态守卫的运行时真值。

`package.cpath`、`package.loadlib` 以及 C loader 对应的 searcher 槽位仍存在，但返回稳定、可诊断的“不支持原生模块”结果。`lightuserdata` 仍作为 Lua 类型存在，内部身份不得暴露不稳定的 CLR 对象地址。

## 3. 全局架构

```text
Lua 字节源码
    │
    ├── 保真 Lexer / CST ─── LuaLS / Legacy EmmyLua 注解
    │              │                     │
    │              └──── Binder / 类型与流分析
    │                                │
    └──────────── Lua 语义 HIR ──────┘
                         │
                Lua 5.4 可恢复寄存器 IR
                         │
          ┌──────────────┼──────────────────┐
          │              │                  │
       解释器         CIL JIT          持久化 CIL AOT
          │              │                  │
          └──────────────┴──────────────────┘
                         │
        统一 Runtime ABI / 元方法 / Lua 逻辑 GC
                         │
          标准库与文件、进程、时钟等能力提供器

PUC Lua 5.4 chunk ── Reader/Writer/Verifier ── Lua 5.4 IR
```

架构不变量：

1. 所有执行后端消费同一份经过验证的 Lua 5.4 IR。
2. 元方法、错误、yield、hook 和 GC 安全点共享同一个运行时语义实现。
3. CLR 调用栈不是 Lua 协程的持久化栈；可 yield 的调用必须保存显式 Lua 帧。
4. 每个可能分配或触发 Lua GC 的位置都有根集合和寄存器活跃信息。
5. 所有表、闭包、线程、userdata 和 upvalue 写入均经过 Lua GC barrier。
6. 注解驱动的优化必须具有类型、metatable 和版本守卫。
7. 缓存损坏、缺失或版本不匹配只能导致重新编译，不能改变程序语义。
8. 文件、环境变量、进程、locale、时钟和控制台访问均通过宿主能力接口。

## 4. 源码与语法前端

### 4.1 字节源码

Lua 字符串和源码以字节为真实表示。`SourceText` 保存不可变字节，不在入口处不可逆地转换成 UTF-16。源码位置同时记录：

- 绝对 byte offset；
- 行号及行内 byte offset；
- 供 LSP 使用的 UTF-16 column；
- 可选的 UTF-8 code point column。

文件加载负责 BOM 与 shebang；`load(string)`、reader function 和文件加载分别执行 Lua 规定的预处理，不错误复用规则。Lexer 必须正确处理 CR、LF、CRLF、所有长字符串/长注释等号层级、十六进制浮点数、转义、`\z`、Unicode escape、嵌入 NUL 及非法 UTF-8 字节。

### 4.2 语法树

采用手写 Lexer 与递归下降/Pratt Parser，生成 immutable green tree 和按需 red tree：

- CST 保留 token、空白、注释、注解和错误 token；
- 严格编译与 IDE 容错解析共用语法定义，仅恢复策略不同；
- parser 对嵌套深度、token 数、节点数和诊断数设置可配置预算；
- 错误恢复必须保证前进，不能对恶意输入产生无限循环或无界递归；
- 编译器从 CST 生成 Bound Tree/HIR，不能直接把 CST 当作运行时 IR。

### 4.3 必须显式编码的语言边缘语义

- 只有表达式列表最后一个函数调用或 `...` 展开多返回值，括号强制单值；
- 表构造器仅最后一个数组字段可展开；
- 赋值前固定全部左值地址并按规则调整全部右值；
- `and`、`or` 返回操作数而非规范化布尔值；
- `local x = x` 的初始化读取外层绑定；
- `local function` 在函数体绑定递归局部变量；
- `repeat` 条件可见循环体局部变量；
- `_ENV` 按词法 upvalue 重写，并允许被替换、捕获和调试修改；
- `goto` 不得跳入新局部变量作用域，跳出作用域时关闭 open upvalue 与 `<close>` 变量；
- `<const>` 在绑定与运行时调试写入路径均受保护；
- `<close>` 逆序执行，遵循错误替换、错误链接和不可 yield 规则；
- 尾调用在待关闭变量、hook、调试和 continuation 边界处保持 Lua 语义；
- 整数溢出采用 64 位二进制补码，不能使用 CLR checked 默认行为；
- floor division、modulo、负操作数、`long.MinValue / -1` 和大位移单独实现；
- 整数与精确等值浮点数作为同一 table key，`-0.0` 归一化，NaN 禁止作为键；
- `next` 支持当前键删除后的合法继续，非法键产生 Lua 规定的错误；
- `#table` 查找 border，而非返回数组元素计数；
- 元方法查找、fallback、调用次序和链长度限制不得交给 C# 运算符隐式决定；
- `debug.upvaluejoin`/`upvalueid` 依赖真实 upvalue cell 身份。

## 5. 注解与语义分析

默认方言为 LuaLS。旧 EmmyLua IDE 方言通过 `--enable-legacy-emmylua` 开启；也提供显式 `--emmy-dialect=luals` 配置项。组合模式中：

1. 优先按 LuaLS 解释；
2. LuaLS 不接受时尝试 legacy parser；
3. 歧义时采用 LuaLS 并产生可配置的兼容性提示；
4. 未知标签保留在 CST，不阻止 Lua 编译；
5. 缓存键记录方言、兼容开关和诊断配置。

实现共享 annotation lexer 与公共类型 AST，解析器保持独立：

- `LuaLsAnnotationParser`；
- `LegacyEmmyAnnotationParser`；
- `AnnotationCompatibilityResolver`。

类型系统至少包含 `any`、`unknown`、`never`、nil、literal、union、intersection、class、alias、enum、结构化 table、数组、map、function、overload、generic、vararg、tuple/type pack、nullable、optional field、self/colon call、operator 和 callable 类型。

分析流水线为 Lua binder、模块解析、约束生成、控制流图、类型收窄、跨模块固定点、诊断与 suppression。必须识别 `x ~= nil`、`type(x) == "string"`、`assert(x)`、短路表达式、判别字段与 `---@cast` 等收窄形式。循环递归类型、循环模块依赖和过深泛型实例化使用预算与稳定的 widening 规则收敛。

注解在运行时擦除。任何利用类型信息的专门化都必须在入口或使用点检查实际 Lua tag、metatable/version 与必要的 shape；失败后进入通用路径或 deopt。

## 6. Lua 5.4 IR

Canonical IR 接近 PUC Lua 5.4 的寄存器 VM，能够无损表示 binary chunk，并增加：

- 显式基本块和 continuation PC；
- `Call`、`TailCall`、`Yield`、`Close`、`VarArg` 与开放结果区间；
- 指令副作用：可能分配、调用元方法、yield、抛出 Lua 错误、触发 hook；
- GC 活跃寄存器和 upvalue 映射；
- 源码位置、逻辑指令编号及调试变量范围；
- verifier 已证明的不变量。

优化器可在 canonical IR 上临时构造 SSA，但协程保存、binary chunk 和 deopt 恢复均回到 canonical 寄存器模型。优化 pass 必须声明其所依赖和保持的不变量，并接受独立 verifier 检查。

### 6.1 Canonical 指令 ABI

源码 lowering 和 chunk 导入必须产生相同的 `LuaIrModule`/`LuaIrFunction`。函数使用连续编号，保存父函数编号、参数数、vararg 标记、最大寄存器窗口、二进制常量、upvalue 来源、线性指令和从指令重建的基本块。当前格式版本为 2；版本 2 增加 `SetTop`，使作用域/循环边界可以清除死亡临时寄存器，避免它们成为逻辑 GC 的伪根。缓存或 AOT artifact 必须把该版本纳入兼容键。

寄存器窗口约定如下：

- `Call A B C`：`R(A)` 是函数，参数从 `R(A+1)` 开始，结果覆盖 `R(A)`；`B == -1` 或 `C == -1` 表示延伸到逻辑 frame top 的开放区间；
- `Return A B`、`VarArg A B` 与 `SetList A B C D` 使用相同的 `-1` 开放区间协议，固定结果不足时补 nil；
- `Jump A B C` 的 `B` 是绝对 continuation PC，`C >= 0` 时先关闭 `R(C)` 及以上的 open upvalue/to-be-closed 槽；
- `Closure A B` 引用模块内函数编号；子函数的每个 upvalue 明确来自父函数 register、父函数 upvalue 或宿主提供的主 `_ENV`；
- `NumericForPrepare`/`NumericForLoop` 保留 integer/float 双模式，回边前显式 `Close`，确保每轮捕获具有独立 cell；泛型 `for` 和 `repeat` 遵循相同的回边关闭规则。

`LuaIrVerifier` 不信任 lowerer、chunk reader、缓存或优化器。它检查格式版本、稠密函数编号、父子关系、寄存器/常量/upvalue/函数索引、固定与开放调用窗口、跳转目标、捕获来源和基本块重建结果。执行器只接受通过该 verifier 的模块。

### 6.2 源码 lowering 约束

lowering 只接受无 error 诊断的绑定模型。局部变量在 RHS 完成后才激活；赋值先固定全部 table/key 左值，再计算 RHS，最后按从左到右写回。表达式列表只展开最后一个未加括号的调用或 vararg，`and`/`or` 以分支保留操作数原值。词法作用域退出、goto、break、循环回边和 return 都产生可验证的关闭边界。临时寄存器可复用，但活跃局部变量和捕获来源在其词法生存期内保持稳定。

## 7. PUC Lua 5.4 Binary Chunk

内部 prototype 覆盖 PUC Lua 5.4 字段：32 位指令、常量、upvalue、嵌套 prototype、line info、absolute line info、局部变量范围、upvalue 名、参数、vararg 标记与最大栈大小。

子系统包括：

- `Lua54ChunkReader`；
- `Lua54ChunkWriter`；
- `Lua54ChunkVerifier`；
- `Lua54Prototype`；
- `Lua54InstructionCodec`；
- `Lua54ChunkTarget`。

读取器验证 signature、版本、format、`LUAC_DATA`、指令/整数/浮点宽度、endianness 哨兵与数值表示。支持 Lua 5.4 的变长 size 编码、父 source 继承、全部常量 tag、prototype 树和调试表。写入器默认输出宿主目标，也可显式指定如 `lua54-le64-double` 的 target；不支持的数值表示必须明确拒绝，不能静默截断。

外部 chunk 一律视为不可信输入。Verifier 检查：

- section 长度、递归深度、总 prototype/常量/指令/字符串预算；
- opcode、寄存器、常量、upvalue 和 prototype 索引；
- jump target、控制流入口及不可落入的关联指令；
- `LOADKX`/`EXTRAARG`、`SETLIST`/`EXTRAARG` 等配对；
- stack/register 区间、开放调用与返回约束；
- `TBC`、`CLOSE` 及控制流关闭状态；
- debug PC/range、line table、局部变量与 upvalue 名数量；
- 算术溢出、超大分配、截断输入和尾随输入策略。

内部缓存格式具有独立 magic 与 Runtime ABI 版本，不能把私有扩展伪装成 PUC chunk。

## 8. 运行时数据模型

### 8.1 LuaValue

不得使用 explicit layout 重叠 CLR 引用和数值，这会破坏 GC 布局。推荐的 16 字节表示由一个引用字段和一个 64 位 payload 组成：

```csharp
internal readonly struct LuaValue
{
    private readonly object? _tagOrReference;
    private readonly long _payload;
}
```

`null` 表示 nil；静态 tag 单例表示 integer、float、boolean 和 light-userdata；payload 保存完整 `long`、`double` bits 或稳定 identity id；字符串、表、闭包、线程和 userdata 直接保存引用。数字不装箱，不使用 `GCHandle` 维持普通值，也不牺牲 64 位整数范围。

### 8.2 字符串

`LuaString` 是不可变二进制字符串：

- byte storage 是真实表示，UTF-16 仅为可选缓存视图；
- 短字符串驻留，长字符串按内容 hash；
- hash 使用每个 runtime 的随机种子防止哈希洪泛；
- 驻留表参与 Lua 逻辑 GC，不永久固定所有短字符串；
- 拼接采用有预算的 builder/rope 临时结构，公开结果仍为连续不可变字节。

### 8.3 Table

采用 array/hash 混合结构。正整数候选键进入 array part，hash part 使用开放寻址并保留删除 tombstone。tombstone 暂存已删除键以支持合法的 `next` 继续遍历。表的 storage、metatable 和 shape 均有版本，用于 inline cache guard 和调试修改失效。

弱键、弱值、ephemeron、NaN 键拒绝、整数/浮点键归一、rehash、`__mode` 动态改变、metatable 修改与 barrier 必须覆盖专项测试。任何对象式 shape 优化都只是通用表语义的可撤销表示。

### 8.4 Lua 逻辑 GC

.NET GC 不能独立提供 Lua 所需的弱表、ephemeron、finalizer 顺序、对象复活和 `collectgarbage` 行为。因此托管对象之上实现 Lua 逻辑 GC：

- `LuaGcObject` 保存颜色、年龄、owner 与逻辑链表字段；
- `LuaHeap` 维护 allgc、finalizable、待执行 finalizer、灰集合及 remembered set；
- 支持 incremental 与 generational 模式、弱表清理和 ephemeron fixed point；
- Lua finalizer 在 Lua 安全点运行，不借助 C# finalizer 模拟；
- sweep 解除 heap 的强引用，物理内存最终由 CLR GC 回收；
- 配额按逻辑分配尺寸计量，不依赖不稳定的 CLR heap 大小。

宿主长期持有 Lua 对象必须使用注册到根集合的 `LuaHandle`。裸 `LuaValue` 仅在借用作用域内有效。不同 `LuaState` 的引用值不得交叉写入。

### 8.5 栈、闭包与协程

`LuaThread` 保存 `LuaValue[]` 栈和逻辑帧；`LuaFrame` 保存 base、top、PC、期望返回数、close list、hook 状态与 continuation。open upvalue 指向栈槽，离开作用域后关闭到独立 cell。

yield 返回显式 `VmSignal.Yield`，而不是保留 CLR 调用栈。resume 从逻辑 PC/帧恢复。托管函数区分普通调用和支持 continuation 的 yieldable 调用。`pcall`/`xpcall` 建立逻辑保护边界，unwind 时执行 close。Lua error 以 Lua 值传播，CLR 异常只在宿主边界转换并保留可配置的诊断信息。

### 8.6 当前基线运行时边界

0.2.0 运行时基线采用引用字段加 64 位 payload 的 16-byte `LuaValue`。所有 collectable value 具有唯一 `LuaHeap` owner 和稳定对象编号；栈、table、upvalue、闭包、线程、handle、永久根及 pending continuation 的写入都执行 owner 校验与 barrier。跨 `LuaState` 写入和逻辑回收后的悬空对象访问会产生 Lua 运行时错误。

`LuaHeap` 已实现 incremental/generational 三色状态机、gray/gray-again、old-to-young remembered set、逻辑分配债务、配额、安全点、每次分配触发的 stress 模式、minor/major promotion，以及独立于 CLR GC 的 sweep。atomic 阶段处理动态 `__mode`、弱键/弱值、PUC 字符串强引用例外、ephemeron fixed point 和 finalizer separation；finalizer 在解释器安全点执行，可通过 `LuaHandle` 复活对象，且异常进入 warning 通道。

`LuaTable` 使用连续 array part 与开放寻址 hash part，hash 删除保留 tombstone 以支持 `next(table, deletedKey)`，rehash 后失效的键按 Lua 错误拒绝。键实现 integer/float 归一、NaN 拒绝、每 state 随机 seed，并分别维护 storage、shape、metatable version。逻辑容量变化计入 heap quota，所有强弱边及 metatable 写入都经过 barrier。

`LuaInterpreter` 直接执行 canonical IR，Lua 调用和 Lua 元方法只压入显式 frame。共享调度覆盖 `__index`、`__newindex`、`__call`、算术/位运算、`__len`、`__concat`、`__eq`、`__lt`、`__le` 及其 fallback；类型元表与对象元表走同一路径，并具有 2,000 层链预算。普通算术和 numeric-for 使用与 lexer 共享的 Lua 数字解析器转换完整数字字符串，integer loop 在边界处用宽中间值判定，避免 64 位回绕造成额外迭代。

Lua 错误以 `LuaValue` 传播。`pcall`/`xpcall` 使用最近的显式 protected boundary；`xpcall` handler 自身失败产生 PUC 的 `error in error handling`。`__close` 在正常返回、跳转和错误展开中逆序运行，Lua closure closer 可跨多个解释器迭代恢复；返回值和 tail-call 目标/参数在 closer 前快照，closer 错误替换当前错误，nil/false close value 被忽略。协程 yield/resume、完整标准库和 chunk-to-canonical 执行转换仍按 `tasklist/0.2.0.md` 的依赖计划推进。

## 9. 执行后端

### 9.1 解释器

解释器是语义基准，并负责 NativeAOT 环境中的动态 `load`、JIT 失败回退、精确 hook/debug 模式及不可信 chunk 的验证执行。dispatch 实现需基准比较 switch、函数表和受审计的低级优化，默认选择在所有目标平台稳定的方案。

### 9.2 CIL JIT

- Tier 0：寄存器解释器；
- Tier 1：将已验证函数编译为 `DynamicMethod` 或 collectible assembly；
- Tier 2：根据类型反馈生成带守卫的专门化 CIL；
- 可选 OSR：热点循环在安全点从解释器进入已编译代码。

优化包括数字快路径、字符串字段/数组索引 inline cache、metatable/version guard、已知闭包直接调用、多返回值栈区间复用及可证明 non-yieldable leaf 内联。guard 失败进入共享 slow path；已跨越逻辑边界的优化必须有 deopt map 恢复 Lua 帧。

当 `RuntimeFeature.IsDynamicCodeSupported` 为 false 时 JIT 自动禁用。机器码不持久化，缓存仅保存 IR、CIL artifact 与版本化 profile。

### 9.3 AOT

持久化 CIL 使用 `System.Reflection.Metadata`/`ManagedPEBuilder` 生成确定性 ECMA-335 PE 与 Portable PDB。稳定入口 ABI 为：

```csharp
VmSignal Execute(LuaThread thread, ref LuaFrame frame);
```

大型 Lua 函数按基本块拆分，避免 CoreCLR JIT 与 NativeAOT 对超大方法退化。

.NET NativeAOT 通过 MSBuild task 在 `CoreCompile` 前生成 Lua 程序集和静态 `.g.cs` 注册清单，使 trimming/AOT 能发现全部入口。NativeAOT 运行时不动态产生 CIL；未预编译源码使用解释器。

## 10. 标准库

完整实现 basic、coroutine、package、string、utf8、table、math、io、os 和 debug。特别要求：

- `string.find/match/gmatch/gsub` 实现 Lua pattern，不用 .NET Regex 替代；
- 覆盖 `%b`、`%f`、capture、回溯预算和空匹配推进；
- `string.pack/unpack/packsize` 正确处理 alignment、endianness、溢出与越界；
- `string.format` 实现 Lua/C 规定的格式，而非直接转发 .NET formatting；
- `math.random/randomseed` 移植 Lua 5.4 算法以提供兼容序列；
- `table.sort` 检测非法 comparator；
- `load` 支持 reader function、text/binary mode 和指定 `_ENV`；
- `string.dump` 支持 strip debug information；
- `io.read("*n")` 使用 Lua 自己的数值词法；
- debug hook 具有 call、return、tail-call、line、count 事件；
- 调试修改局部变量、upvalue 或 metatable 后使相关优化失效/deopt；
- `coroutine.close` 关闭 suspended frame 中的 `<close>` 变量；
- `warn`、warning control、weak table、`__gc`、`__close` 完整覆盖。

宿主能力接口至少包括 `IFileSystem`、`IProcessService`、`IEnvironmentProvider`、`IClock`、`IRandomSource`、`IModuleResolver` 和 `IConsole`。提供 Full、Sandbox、Deterministic 配置。路径穿越、符号链接、设备文件、进程启动与环境泄漏由能力实现控制，而非散落在标准库中。

## 11. 编译缓存

项目缓存默认位于 `obj/luac/`，全局缓存位于用户缓存目录。缓存分为源码/CST 索引、绑定与类型结果、canonical IR、优化 IR/profile、持久化 CIL/PDB。

缓存键至少包含：

- 原始源码字节 SHA-256；
- Lua 版本和兼容 profile；
- LuaLS/legacy 方言及诊断配置；
- 编译器版本、artifact format 和 Runtime ABI；
- 优化、debug、hook、sandbox 配置；
- TFM、RID、架构、标准库 feature set；
- 静态依赖 hash；
- AOT、trimming 和 deterministic 配置。

缓存写入采用同目录临时文件、flush、atomic rename 与跨进程锁。读取检查 magic、版本、长度、checksum 和 IR verifier；损坏项自动隔离或删除并重编译。不使用 `BinaryFormatter`。动态 `require` 不伪装成静态依赖。相同内容可以共享代码 artifact，但 source name、路径和 traceback binding 必须独立。

## 12. Unsafe 审计政策

`unsafe` 与 `Unsafe` 仅允许出现在显式白名单的底层目录，例如 Runtime Intrinsics、Runtime Collections 和 CIL CodeGen。每处必须：

- 在源码旁说明内存、类型与生命周期不变量；
- 有安全参考实现或等价的差分 oracle；
- 有基准证明收益，未经证明不得仅凭直觉保留；
- 不向公共 API 暴露裸指针；
- 对边界、alignment、别名与 GC relocation 做显式处理；
- 通过 fuzz、GC stress、Debug/Release、JIT/NativeAOT 组合测试。

预期用途限于无装箱位转换、受控 `ref`/`Span` 操作、经证明的边界检查消除及高性能 table/stack 访问。

## 13. 项目边界与依赖

```text
src/
  Luac.Core/
  Luac.Syntax/
  Luac.EmmyLua/
  Luac.Semantics/
  Luac.IR/
  Luac.Runtime.Abstractions/
  Luac.Runtime/
  Luac.StandardLibrary/
  Luac.CodeGen.Cil/
  Luac.Compiler/
  Luac.Hosting/
  Luac.Build/
  Luac.Cli/

tests/
  Luac.Core.Tests/
  Luac.Syntax.Tests/
  Luac.Semantics.Tests/
  Luac.Runtime.Tests/
  Luac.StandardLibrary.Tests/
  Luac.BackendDifferential.Tests/
  Luac.Conformance.Tests/
  Luac.Fuzz.Tests/
  Luac.NativeAot.Tests/
  Luac.Benchmarks/
```

Syntax 与 EmmyLua 不引用 Runtime；CodeGen 仅依赖稳定 Runtime ABI；Runtime 不反向依赖 Compiler；标准库通过 Runtime Abstractions 获取宿主能力。跨层数据结构放在最窄的共同抽象项目，禁止形成循环引用。

## 14. 验证策略

正确性门槛：

- Lua 5.4.8 官方测试套件；
- 与固定 PUC Lua 5.4.8 oracle 的差分测试；
- interpreter、Tier 1、Tier 2、持久化 CIL 和 NativeAOT 结果一致；
- parser、chunk、pattern、pack/unpack、table 和类型解析 fuzz；
- GC、弱表、ephemeron、finalizer resurrection 专项测试；
- Windows/Linux/macOS 与 x64/arm64 测试矩阵；
- 正常优化与精确 debug/hook 模式分别验证；
- chunk 与 PUC Lua 双向互操作测试。

性能门槛：

- 纯算术热点循环稳态零分配；
- Lua 到 Lua 调用不创建参数数组；
- 常见 table 字段命中不装箱；
- 多返回值使用 Lua 栈区间；
- 分别测量冷启动、解释器吞吐、JIT 预热、稳定吞吐、AOT 体积和峰值内存；
- 对照 PUC Lua 5.4.8、MoonSharp 与本项目解释器基线；
- 未通过全部语义测试的优化不得进入默认优化级别。

0.2.0 的 Windows x64/.NET 10 Release 基线使用 `benchmarks/Luac.Runtime.Benchmarks` 固化。1,000,000 次 table get/set 的代表值约为 199 ns/op，单次 10,000 轮空 numeric-for 约 5.2 ms，带加法约 9.9 ms，1,000 个 table 的 full logical GC 约 0.71 ms。空循环的稳态执行固定分配约 5 KiB（frame/结果对象），不再随 10,000 次循环迭代增长；单元门槛为每次热执行不超过 16 KiB。数值仅作为本机回归基线，不是跨硬件的绝对门槛。

## 15. 可观测性与失败模型

诊断具有稳定代码、严重度、源码范围、相关位置和可选修复建议。Lua traceback 与宿主异常分别保存，跨边界时组合但不丢失 Lua 错误值。编译和执行预算的失败必须与 Lua 程序自身错误区分。

关键计数器包括分配量、逻辑 GC 周期、弱表/ephemeron 迭代、解释/JIT 函数数、OSR/deopt 次数、inline cache 命中率、缓存命中/损坏/重建次数。默认不记录源码、字符串内容或环境机密。

## 16. 实施顺序

1. 建立 byte source、诊断基础、Lua 5.4 chunk 模型、instruction codec、reader/writer 与 verifier。
2. 实现保真 CST、Lua binder 和 canonical IR 构建。
3. 实现 LuaValue、字符串、table、逻辑 GC、栈、闭包、元方法和解释器。
4. 完成标准库、协程、debug、binary chunk 全互操作及官方测试。
5. 实现 LuaLS 类型系统、legacy EmmyLua 输入兼容和增量模块分析。
6. 实现持久化 CIL AOT 与 Portable PDB。
7. 实现 Tier 1/Tier 2 CIL JIT、PIC、deopt 与 OSR。
8. 接入 NativeAOT build task、缓存、sandbox、宿主 API 和发布工具链。
9. 完成全平台 conformance、fuzz、性能回归及稳定发布。

每一阶段必须保留端到端可运行路径，不允许以临时代码绕开已经确定的 Lua 语义。破坏性设计变更需要新的架构记录、版本化 changelog 和迁移说明。
