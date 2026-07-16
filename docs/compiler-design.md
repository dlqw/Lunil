# Lunil 编译器与运行时技术设计

状态：已批准并进入实现
目标版本：Lua 5.4.8、.NET 10
文档版本：0.2

## 1. 目标与范围

Lunil 是完全使用 C# 实现的 Lua 5.4 编译器、运行时与静态分析工具链。项目必须提供：

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

`0.7.0-alpha.1` 新增 `Lunil.Compiler` 公共产品边界。它统一拥有 byte source、逻辑 source
identity、有界 lexer/parser/binder 选项、canonical lowering、独立 verifier、按阶段归属的稳定
diagnostic、阶段间 cancellation 检查和不可变 compilation result。`Lunil.Build` 的源码输入必须
复用该入口，不能继续维护第二套 parse/bind/lower 拼装逻辑。后续注解、类型、流与模块分析均接入
同一 compilation result，不绕过该边界直接向宿主发布内部 mutable state。

`0.7.0-alpha.2` 新增 `Lunil.EmmyLua` 公共注解前端并接入 `LuaCompiler`。注解结果与 Lua
syntax/semantic result 一同发布，但 canonical IR 和 runtime 仍完全擦除注解。`0.7.0-alpha.3`
新增 `Lunil.Analysis`，在 binding 后、lowering 前发布独立 immutable type/flow result；静态诊断默认
为 warning，分析宿主可提升为 error 以阻止 module 发布。分析结果同样不进入 canonical IR/runtime。
`0.7.0-alpha.4` 新增 `Lunil.Workspace`：它在 Compiler 外层维护稳定 module/source identity、resolver、
dependency graph、跨模块 export type、SCC fixed point、内容寻址 cache 与最小失效，不在单文档 analyzer
中引入隐藏 mutable global state。Hosting 和 Build 复用同一 workspace API，runtime `package`/`require`
行为及 canonical IR 保持不变。

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

当前实现覆盖 class/field/alias/enum/type/param/return/generic/overload/vararg/cast/operator/
diagnostic/marker/unknown directive，以及 named/literal/union/intersection/nullable/array/tuple/
vararg/function/generic/structural-table type syntax。所有 token、annotation、type depth 与 diagnostic
均有独立预算；未知标签及原始 payload 保留。默认 annotation syntax diagnostic 为 warning，分析宿主
可显式提升为 error；source suppression 支持 disable/enable/disable-next-line。

类型系统至少包含 `any`、`unknown`、`never`、nil、literal、union、intersection、class、alias、enum、结构化 table、数组、map、function、overload、generic、vararg、tuple/type pack、nullable、optional field、self/colon call、operator 和 callable 类型。

分析流水线为 Lua binder、模块解析、约束生成、控制流图、类型收窄、跨模块固定点、诊断与 suppression。必须识别 `x ~= nil`、`type(x) == "string"`、`assert(x)`、短路表达式、判别字段与 `---@cast` 等收窄形式。循环递归类型、循环模块依赖和过深泛型实例化使用预算与稳定的 widening 规则收敛。

当前 `Lunil.Analysis` 单文档阶段已实现 semantic type/type-pack、class/alias/enum declaration、
structural table、array/map、function/overload/callable、generic substitution、operator lookup、structural
assignability、expression/symbol inference、call/assignment/return constraint，以及每函数 CFG。流状态覆盖
nil/type/assert/判别字段/短路收窄、definite assignment、unreachable、numeric/generic-for、return-pack
inference 和 `---@cast`；diagnostic suppression 同时支持 options 与 source disable/enable/next-line。
type/constraint/CFG/flow-iteration/generic-instantiation/diagnostic 均有全局预算，递归 alias 与不收敛 loop
使用稳定 unknown/union widening。

当前 `Lunil.Workspace` 只把 direct-global 且第一个参数为 string literal 的 `require` 作为静态边；局部
shadowed `require` 不会误判，动态名称产生显式 conservative edge 并返回 `any`。resolver 可由内存文档或
root-confined Lua path pattern 提供。graph 以确定性 Tarjan SCC 表示循环模块，按 condensation DAG 的依赖
层执行；循环 SCC 使用 bounded fixed point，不收敛时对 export type 做稳定 widening。discovery cache 由
module/source identity 和 source SHA-256 寻址，analysis cache 额外纳入 direct dependency export hash；因此
leaf 实现变化但 export 不变时只重新分析 leaf，export 变化才失效反向依赖。module/source/dependency/cache/
diagnostic/fixed-point 与全局并行度均有预算，取消不会发布 partial snapshot，输出按 identity/span/code 排序。

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

源码 lowering 和 chunk 导入必须产生相同的 `LuaIrModule`/`LuaIrFunction`。函数使用连续编号，保存父函数编号、参数数、vararg 标记、最大寄存器窗口、二进制常量、upvalue 来源、线性指令和从指令重建的基本块。当前格式版本为 3；版本 2 增加 `SetTop`，版本 3 增加 binary source name、定义行、逐指令 source line/logical PC、二进制 upvalue 名与 canonical debug-local 范围。缓存或 AOT artifact 必须把该版本纳入兼容键。

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

0.4.0 的导入器在 verifier 之后递归为 prototype 预分配连续函数编号，并把 PUC PC 两遍映射为 canonical continuation PC。`MMBIN*` 与其算术指令合并为可 yield 的 `Binary`，测试与后续 `JMP` 合并为显式条件边，`EXTRAARG` 合入 `LOADKX`/`NEWTABLE`/`SETLIST`。泛型 for 展开为可恢复 `Call` 与显式 nil 测试；numeric-for 直接使用 PUC 的无符号剩余计数状态，避免 64 位端点回绕差异。开放 `top` 只允许来自紧邻的 `CALL`/`VARARG` 等生产者。

`LuaState.LoadBinaryChunk` 物化主闭包：第一个主 upvalue 绑定 state 的全局环境，其余宿主 upvalue 初始化为 nil；嵌套闭包继续使用 canonical register/upvalue cell 捕获规则。所有导入函数仍由 `LuaIrVerifier` 二次验证，并与源码 lowering 共用解释器、continuation ABI、逻辑 GC 与 owner 检查。

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

Lua 错误以 `LuaValue` 传播。`pcall`/`xpcall` 使用最近的显式 protected boundary；`xpcall` handler 自身失败产生 PUC 的 `error in error handling`。`__close` 在正常返回、跳转和错误展开中逆序运行，Lua closure closer 可跨多个解释器迭代恢复；返回值和 tail-call 目标/参数在 closer 前快照，closer 错误替换当前错误，nil/false close value 被忽略。协程 yield/resume、完整标准库和 chunk-to-canonical 执行转换均已完成，并继续由解释器、JIT/AOT 差分及官方 portable user-mode 语料回归。

## 9. 执行后端

### 9.1 解释器

解释器是语义基准，并负责 NativeAOT 环境中的动态 `load`、JIT 失败回退、精确 hook/debug 模式及不可信 chunk 的验证执行。dispatch 实现需基准比较 switch、函数表和受审计的低级优化，默认选择在所有目标平台稳定的方案。

宿主默认通过后端中立的 `LuaExecutor` 执行；`LuaInterpreter` 明确固定到 Tier 0。二者共用
`LuaExecutionEngine` scheduler，reference opcode dispatch 位于独立的 interpreter instruction
executor。当前 Runtime code-generation ABI v2 以 `LuaExecutionContext`/`LuaCompiledExit` 交换
canonical PC、精确指令计数与 tagged boundary，后端返回的计数必须与 context 中预留的区间一致。

### 9.2 CIL JIT

- Tier 0：寄存器解释器；
- Tier 1：将已验证函数编译为 `DynamicMethod` 或 collectible assembly；
- Tier 2：根据类型反馈生成带守卫的专门化 CIL；
- 可选 OSR：热点循环在安全点从解释器进入已编译代码。

优化包括数字快路径、字符串字段/数组索引 inline cache、metatable/version guard、已知闭包直接调用、多返回值栈区间复用及可证明 non-yieldable leaf 内联。guard 失败进入共享 slow path；已跨越逻辑边界的优化必须有 deopt map 恢复 Lua 帧。

当 `RuntimeFeature.IsDynamicCodeSupported` 或 `IsDynamicCodeCompiled` 为 false 时 JIT 自动禁用。机器码不持久化，缓存仅保存 IR、CIL artifact 与版本化 profile。

当前 CoreCLR Tier 1 已提供 `InterpreterOnly/Auto/PreferJit/RequireJit` public policy，按 function entry
与 verified backedge 计数触发编译。默认使用 bounded asynchronous compile queue；测试与 deterministic
场景可切换同步模式。每个 module-content/function/codegen key 只允许一个 Queued/Compiling request，
状态按 `Cold/Queued/Compiling/Ready/Failed/Invalidated` 转换，并具有 retry backoff、失败熔断、取消、
LRU code-byte budget 与显式 module/cache invalidation。cache key 和 emitted delegate 不持有
`LuaState`、closure 或 upvalue owner；动态代码不可用时不会进入 Reflection.Emit。
取消令牌贯穿 CFG/liveness、method-plan build/verification 与 CIL emission；取消的 plan 不进入 weak
cache，未执行 `EndMethod` 的 emitter 不创建 delegate，registry 在安装方法前再次检查 dispose token，
因此忽略取消后迟到的 compiler result 也不会发布为 `Ready`。release 默认 policy 为 `Auto`，
`EnableTier2=true`、`EnableLoopOsr=true`，且两个 managed-fallback 开关均为 `false`：默认迁移同时
启用资格检查保护的 Tier 1、exact-numeric Tier 2 与 guarded exact-numeric Loop OSR；所有 managed
fallback 仍必须由第二个开关明确开启。

module/cache invalidation 在 registry entry lock 上线性化：调用方已经取得的 Tier 2/Loop OSR delegate
可以完成当前 invocation；invalidation 之后开始的调度不得再次进入该旧方法。编译 completion identity、
entry state 与 dispose cancellation 在安装前共同复核，因此跨越 invalidate、clear 或 dispose 的异步旧结果
不能重新发布。numeric-region poll 不承担主动中断当前 invalidation 的职责。

Tier 1 可观测性公开 compile queue latency、compile time、compiled invocation、fallback、deopt、failure、
eviction 与 estimated code bytes 计数和结构化事件。事件订阅者异常不得改变 Lua 执行语义。

`Auto` 在 hotness 达标后还会运行确定性的 Tier 1 收益资格检查。输入仅为 verified function
facts：canonical instruction/backedge 数、direct lowering coverage、slow-path 与 semantic-boundary
density、plan instruction count 和 estimated code bytes。无重复工作、coverage 过低或 scheduler
boundary 密集的函数不进入 compile queue；结构化 eligibility event、statistics 和
`GetFunctionEligibility` 暴露拒绝原因及 break-even class。owner-free imported profile 只能提前
满足 hotness，不能绕过资格。`PreferJit` 可覆盖收益过滤，但仍不能覆盖 IR/method-plan verification、
ABI、dynamic-code 和资源上限；`RequireJit` 对不可编译函数返回稳定诊断。

当前 Tier 2 通过 Runtime ABI instruction observer 采集 owner-safe profile：entry argument tag、unary/binary
operand tag、branch/backedge、table array capacity/shape/metatable version，以及 Lua module-content/function 或
native name call target。profile 不保存未追踪 Lua object；table/call signature 最多保留四路，超过后永久标记
megamorphic。promotion 与 Tier 1 共用 bounded queue、并发去重、code-byte LRU 和取消生命周期。

profile-guided pass 实施 primitive constant folding、dead move、numeric/unary specialization、stable boolean
branch、mono/poly table PIC、known-closure call guard 与 fixed result-window reuse。每个假设均有显式 guard；
所有 canonical register、frame top 和 pending transform 在 guard 边界保持 materialized，`DeoptMap` 记录
canonical PC 与 live register。guard failure 在零重复副作用的 PC 恢复 reference executor；连续失败达到阈值
会丢弃 Tier 2、保留 Tier 1、合并新 profile 后重新 promotion。debug/hook epoch 变化走同一精确 deopt 边界。

精确 integer、float 与 mixed-numeric profile 会进一步生成 `ExactNumericSpecializedCil`
`DynamicMethod`；stable table PIC 或 known-closure call guard 则把代码形态标记为
`GuardedSpecializedCil`。outer emitter 在 CIL 中直接执行 entry guard、预算预留、ABI v2/v3 unchecked
register/table/call 访问与 canonical control flow；`NumericForPrepare`/`NumericForLoop`、固定/开放 vararg、
table shape guard、frameless leaf call 和成功后的同方法续跑不再每轮返回 scheduler。numeric-region
delegate 可与 guarded outer method 组合，region exit 只在 canonical PC 正确落入另一侧时切换。
emitter 对整个函数 CFG 做 fail-closed coverage，任何未原生发射的 instruction 都会以 `JIT2106` 在入队前
拒绝，而不是安装包含逐指令 slow path 的“部分 Tier 2”方法；tag/shape/call guard 失败从原 canonical PC
deopt。只有显式选择 managed fallback 的不支持组合才使用 `ManagedProfileProgram`。Tier 2 编译事件分别
记录 IR verification、liveness/cache hit、optimization planning、CIL emission、delegate creation、
allocation、代码形态与专门化/deopt 数量。register liveness 由 module owner-scoped weak cache 在 Tier 1、
Tier 2、Loop OSR 与 persisted AOT 之间共享。

Tier 2 与 Loop OSR 现在还共享 linear numeric-region 后端。reducible CFG 分析先用 dominator 验证
natural backedge；同一 header 的多 latch 合并为 maximal region，外层 region 证明失败时仍可选择互不
重叠的已证明内层 region。类型规划不再给 physical register 绑定单一类型，而是对 reaching definition
做版本化 integer/float/boolean 证明；join 类型冲突、未知 truthiness、被 `SetTop` 清除后又作为数值使用
或任一不支持的语义边界都会 fail closed。一个 physical register 的不同版本可各自使用 `long`/`double`
CIL local，active-kind/dirty local 保证分支 join、captured local、side exit 与 guard deopt 只物化当前版本。

预算规划先切断 verified backedge，再为每个 canonical PC 计算到下一 safepoint/region exit 的最大成本，
同时记录 basic-block 实际成本、精确 deopt PC、失败 instruction 回滚量和 cold slow-tail 入口。若切断
backedge 后仍存在环，region 必须 fail closed。region entry 与 poll 后只做一次保守 quantum 核准；合格
hot quantum 在 basic-block 入口累计实际成本，循环体使用 `ldloc`/`stloc` 和直接算术 CIL，不包含逐
instruction `remaining` 比较，也不调用预算 ABI或写 frame PC。预算不足进入独立 cold slow tail，继续在
每条 instruction 前精确 reserve，因此所有预算余数保持原 PC，零预算仍先于入口 tag guard，整数
`//`/`%` 零除数等 semantic deopt 不计失败 instruction。

dirty register、captured value、`SetTop` 的 minimum/final top、pending budget 和 PC 在 side exit、deopt、
预算退出与 safepoint 边界统一提交。backedge 使用最大 1024 次的 local countdown；非 poll 回边只累计
实际成本和 Loop OSR 逻辑 backedge，不调用 managed method。poll 前所有 GC 可见状态均已物化；只有
`None` 可重新进入 quantum，debug/GC/finalizer/budget/close/unwind 结果返回 scheduler。Loop OSR 的逻辑
backedge telemetry 在边界批量提交，因此统计不因 countdown 采样而少计。

`NumericRegionCount`、`UnboxedNumericLocalCount`、`DirectNumericInstructionCount` 与
`NumericRegionSafepointCount` 同时发布在 Tier 2/Loop OSR plan。最后一项是生成代码中的静态 backedge
safepoint site 数，不是运行期 poll 次数；四项结构指标对 arithmetic 均要求非零。另一个
`NumericRegionHotInstructionBudgetCheckCount` 只统计合格 hot path 的逐 instruction 预算比较，cold slow
tail 不计入，并且门禁要求严格为零。这样旧 switch/helper emitter 或退化后的热路径都不能仅凭相同
code-kind 名称通过门禁。详细架构决策见
[ADR 0011](adr/0011-linear-numeric-regions.md)。

自动 Tier 2 promotion 在进入 compile queue 前复用同一 optimization/liveness planner 计算
`LuaJitTier2Eligibility`。只有 code-shape 检查可保证 `ExactNumericSpecializedCil` 或
`GuardedSpecializedCil` 时才接受；精确数值 hotspot、stable branch/dead move、有界 table PIC 与
known-closure call guard 可以共存，其他 managed semantic boundary 或多态 shape 仍 fail closed。
拒绝结果通过 `Tier2EligibilityAccepted/Rejected`
事件、独立 statistics、`GetTier2PromotionEligibility` 与 `JIT2101`–`JIT2106` 诊断公开。不可逆的
polymorphic/managed 拒绝会永久缓存；尚无数值 hotspot 的 profile 使用指数 sample backoff 复查。
第一次 `NoNumericHotspot` 会给尚未执行的冷路径保留一次指数扩样机会；第二次仍无数值热点则转为
terminal rejection 并切换 plain Tier 1，避免永远承担 observer 前导。
安装阶段再次要求 qualified native code kind，因此自定义 compiler 也不能把 `ManagedProfileProgram` 发布到
默认路径。显式 `EnableTier2ManagedFallback=true` 才允许其余 managed profile-program 实验路径。
逐指令 Tier 2 profile observation 只在 Tier 1 方法安装后激活。Tier 1 同时保留 profiled/plain 两个
完整 delegate，并将两者都计入 LRU code-byte budget；terminal Tier 2 eligibility rejection 会原子切换
到 plain delegate，因此后续执行不再调用 instruction observer。invalidation、eviction、profile import 与
guard-failure reprofiling 会在同一个 entry lock 下恢复正确变体。被 Tier 1 `Auto` 收益检查拒绝的函数
不会承担 profile 采集成本。`Tier2MethodEntries`、`Tier2CompletedInvocations` 与
`Tier2UnsupportedExits` 分别统计真实方法入口、完成的 Lua 调用和 fail-closed 防御出口；旧
`Tier2Invocations` 与 method-entry 计数保持兼容。guarded table/call 决策与不变量见
[ADR 0012](adr/0012-guarded-table-call-fastpaths.md)。

Loop OSR 由 `EnableLoopOsr` 独立控制并默认开启。CFG 分析只接受 target 为基本块 leader、header
支配 backedge source 的 verified natural loop；编译请求不会在分支执行前发出，而是在 backedge 已完成且
同一 frame 的 canonical PC 已提交到 header 后发出。entry map 使用共享 liveness cache 将 interpreter
canonical register 映射到同编号 materialized slot，并显式声明 frame top、open upvalue 与
to-be-closed state 已物化。

启用 OSR 后先使用 verified backedge 计数。尚未建立具体 loop plan 时，reference interpreter 在 frame
内以 countdown 批量累计 backedge；只有到达函数/Loop OSR hotness 边界才重新进入 tier controller，
return、tail call 与异常 unwind 会提交未满 countdown 的精确余数。没有 backedge 的 callee 在第一次入口
即关闭 Loop OSR observation，不创建逐 frame 的 weak observation。具体 loop plan 建立后才恢复逐 backedge
路由，以免嵌套 loop 或多 latch 的计数归入错误 plan。

达到 `LoopOsrBackedgeThreshold` 时执行一次完整 natural-loop/`LuaJitLoopOsrEligibility` 分析。另有一个
只处理确定性负例的 warmup preflight：函数至少进入四次并累计至少
`min(LoopOsrBackedgeThreshold, 64)` 个 backedge 后，若所有 natural loop 从静态结构上都不可能进入默认
specialized OSR，则提前发布同一个正式 rejection。只要存在一个 exact-numeric structural candidate，
preflight 就不发布 plan、eligibility 或编译结果，仍严格等待完整 hot threshold 和运行时 operand profile。
因此一次性冷函数不承担分析成本，反复执行的 call/table/metamethod/coroutine 负例也不会把一次性 CFG
拒绝成本拖入短稳态测量窗口。
只有完整循环可生成 `GuardedExactNumericCil` 且至少包含一个 exact numeric hotspot 时才进入运行时
资格采样；允许的组合包括
load/move/set-top、canonical branch/jump、numeric-for、guarded close 和精确 integer/float/mixed-numeric
unary/binary/comparison/bitwise。table、upvalue、closure、vararg、call/tail-call、to-be-closed marking 与
不支持的 opcode 默认标记为 `Ineligible`。`EnableLoopOsrManagedFallback=false` 是默认值；只有显式改为
`true` 才使用实验性的 `ManagedCanonicalProgram`。安装阶段再次核对 code kind，自定义 compiler 不能
绕过该边界。

specialized-only 路径在 queue admission 前由 interpreter 观察候选 loop 的全部数值 guard site。
negate 与普通 binary 要求 operand 为精确 integer/float，bitwise 要求精确 integer；第一次出现 table、
string 或其他非精确数值 operand，就以 `NonExactNumericProfile`/`JIT3105` 永久拒绝，不编译、不进入
OSR，也不消耗 guard-failure budget。只有所有 site 至少成功观察一次才发布 accepted event 并允许在
下一次 verified header 进入编译。所有 loop 完成 accepted/rejected 转换后，逐指令路径只剩 pending-count
零值分支，不再查 guard-site dictionary。显式 managed fallback 不使用该永久数值拒绝，继续保留 guard
widening 行为。
采样完成前，registry query 返回 `AwaitingExactNumericProfile` 与 `IsAutoEligible=false`；纯静态
`EvaluateLoopOsrEligibility` 仍只回答 code shape 是否可生成。

specialized emitter 不在 executor 构造、普通函数入口或结构分析阶段预热。只有运行时 guard-site
采样发布 `LoopOsrEligibilityAccepted` 后，canonical compiler 才执行一次进程级惰性准备，并发布
`LoopOsrCompilerPrepared` 及其耗时；negative profile、managed boundary、短循环与 dynamic-code
不可用路径不会触发准备。实际编译请求再次等待同一线程安全准备屏障，避免并发请求与首次 emitter
初始化竞态，同时把一次性准备与逐 loop 编译时延分别归因。

生成的 `DynamicMethod` 在 entry 验证 canonical PC 与寄存器 tag，并按上述 quantum 规则核准预算。
每个非 poll loop header 保持在本地直接分支；最多 1024 个 backedge 后才物化完整 GC 可见状态并检查
hook/debug epoch、GC/finalizer、budget、close/unwind。guard failure、poll、预算耗尽或离开 natural loop
均以当前 materialized canonical PC 和精确 consumed count 返回 scheduler，不重放已完成操作。OSR 编译与
Tier 1/Tier 2 共用 bounded queue、并发去重、LRU code-byte budget、module
invalidation、取消、dispose 防迟到安装及 owner-safe 生命周期。编译事件额外记录 IR verification、
loop analysis、liveness/cache hit、specialization planning、CIL emission、delegate creation、allocation、
code kind、专门化与 guard 数量；`JIT3101`–`JIT3105`、accepted/rejected 事件和独立 statistics 公开资格。

无 natural loop 或所有 loop 均被拒绝的函数在达到 hotness 后进入永久 fast-rejection 状态，后续
instruction/backedge 不再运行 analyzer、dictionary lookup 或锁。M6 的 managed prototype 曾只有
47.5% compact-runner 改善并增加分配；M13 的 exact-numeric CIL 生产化记录达到 8.516 倍 interpreter
arithmetic median、3.080 ms compile p95、零 allocation slope 与 100% liveness-cache hit，同时
lua-call/table/metamethod/coroutine-error-hook 的 paired on/off median 全部不低于 0.935。默认仍保持关闭；
详细决策见 [ADR 0006](adr/0006-loop-osr-performance-productionization.md)。

M8 将启动、稳态吞吐、分配、编译 p95、RSS、code bytes 和 persisted PE/PDB size 纳入同一 runner。
当时 Windows x64 三轮证据未达到 Tier 1 至少 2 倍、Tier 2 至少 4 倍以及 Tier 1 编译 p95 小于
5 ms 的门槛，因此默认曾回退为 `InterpreterOnly`。M9 完成 Runtime ABI v2、直接 lowering、缓存和
编译延迟收口后，六 RID 的 Tier 1 arithmetic bootstrap 95% 下界均超过 2 倍、编译 p95 均低于
5 ms、allocation slope 均为 0，且 negative workload gate 无失败。M10 据此只将 Tier 1 默认迁移为
`Auto`；Tier 2 与 Loop OSR 继续独立 opt-in。

M11 的最终 win-x64 五进程精确数值证据达到 9.177 倍 arithmetic median speedup、bootstrap 95%
下界 6.557 倍、1.395 ms Tier 2 compile p95 与零 allocation slope；六 RID CI 的最低 bootstrap
下界为 7.086 倍、最大 compile p95 为 3.228 ms。该结果先将 exact-numeric Tier 2 生产化，详细
决策见 [ADR 0004](adr/0004-tier2-exact-numeric-productionization.md)。M12 再以 profile/code-shape
eligibility 隔离 managed table/call/metamethod/coroutine 路径，将 `EnableTier2` 默认改为 `true`，
同时保持 `EnableTier2ManagedFallback=false`；默认策略见
[ADR 0005](adr/0005-tier2-auto-default-rollout.md)。M13 进一步把 natural loop 的 exact numeric
子集发射为 `GuardedExactNumericCil`，加入独立 eligibility、code-kind 安装门控、fast rejection、
paired negative workload 与六 RID 聚合证据，但保持 `EnableLoopOsr=false`；见
[ADR 0006](adr/0006-loop-osr-performance-productionization.md)。M14 根据真实六 RID negative gate 失败，
把分析延后到 hot backedge、加入 observed exact-numeric qualification/`JIT3105`，并平衡 on/off 运行顺序；
默认仍不改变，直到新 aggregate 全部通过，见
[ADR 0007](adr/0007-loop-osr-default-rollout-readiness.md)。
M15 审查真实 CI run `29241821560` 后继续保持默认关闭：该 run 的拒绝、startup 与 guard 门槛通过，
但两个 10-operation negative median 和 osx-x64 首次 emitter 编译时延仍未达标。当前证据改为至少
30 次 warm operation、六个进程严格 3:3 平衡 on/off 顺序，并仅在 exact-numeric 资格通过后惰性准备
emitter；决策见
[ADR 0008](adr/0008-loop-osr-qualified-preparation-and-evidence.md)。
M15 后续真实六 RID CI run `29244862401` 在 30-operation、六进程严格平衡契约下全部通过：最低
arithmetic bootstrap 下界 4.349 倍、最低 OSR-on/off 下界 63.933 倍、最大 preparation p95 1.239 ms、
最大 compile p95 6.399 ms，所有 negative startup/throughput/acceptance/guard/managed-installation 门槛
均通过。M16 据此将 `EnableLoopOsr` 默认改为 `true`，同时保持 `EnableLoopOsr=false` 完整 opt-out 与
`EnableLoopOsrManagedFallback=false`；见
[ADR 0009](adr/0009-loop-osr-auto-default-rollout.md)。

执行后端共享的 scheduler、canonical PC 提交、指令预算、逻辑 GC safe point、hook/debug
和 artifact identity 已由 [ADR 0001](adr/0001-execution-backend-abi-v1.md) 冻结；当前 unchecked
register/primitive/numeric-for/close helper 与兼容矩阵由
[ADR 0002](adr/0002-execution-backend-abi-v2.md) 定义。编译代码只执行
canonical 基本块；tail call、yield、复杂 close/unwind、hook 和不合格 call 仍由共享 scheduler 负责。
Runtime ABI v3 可在无 hook、无 unwind/finalizer 且预算充足时，用 caller 栈外 scratch window 执行
受限的小型无 upvalue leaf callee；成功后 Tier 1/Tier 2 在同一编译方法内继续，失败则从原 call PC
进入 canonical frame/scheduler 路径。generic compiled backedge 使用有界 countdown poll；poll 前提交
frame PC、结果窗口与所有 Lua GC roots，Tier 2 消除 dead move 时仍清空 frame top 以下的物理栈槽。

`Lunil.CodeGen.Cil` 先把 verified canonical function lowering 为 typed method plan。plan 显式保存
label、local、call signature、canonical PC、sequence point 与 GC live-register map，并在进入任何
emitter 前验证 evaluation-stack depth/type、branch merge、ABI call、safe-point spill 和资源上限。
Reflection.Emit 与 metadata encoder 消费同一 instruction-sink 契约；尚未 lowering 的 opcode 生成
显式 deopt exit，不能落入不完整实现。Persisted CIL v1 已覆盖全部 canonical opcode：寄存器与
控制流基础操作直接 lowering，可能调用、yield、close 或执行复杂 Lua 语义的操作通过 versioned
Runtime ABI slow path 返回共享 scheduler，不在生成方法中复制解释器状态机。

M9 的 ABI-v2 Tier 1 进一步直接 lowering primitive unary/binary/bitwise/comparison、numeric-for、
upvalue access 和 guarded empty-range `Close`。primitive guard 在 reservation 和副作用之前运行；
失败从同一 canonical PC 进入 generic path，只计费一次。Tier 2 与 Loop OSR 均关闭时，method plan
省略逐指令 observation helper。canonical module/function/observation-mode 的 verified plan 使用
owner-scoped `ConditionalWeakTable` 缓存；显式 plan limit 不共享缓存，module 回收不会被 plan 反向
阻止。Reflection.Emit ABI method lookup 和首次 DynamicMethod 基础设施在 executor startup 预热；
动态代码可用且 Tier 2 未关闭时还会按进程预热完整 profile planning、deopt-map build 与
specialized emission pipeline。Loop OSR specialized emitter 改为在 hotness 和运行时数值资格通过后
惰性初始化，因此即使 release 默认启用 OSR，冷函数、短循环和 NativeAOT 进程也不承担该初始化成本。

### 9.3 AOT

持久化 CIL 使用 `System.Reflection.Metadata`/`ManagedPEBuilder` 生成确定性 ECMA-335 PE 与 Portable PDB。稳定入口 ABI 的概念形式为：

```csharp
LuaCompiledExit Execute(
    LuaExecutionContext context,
    LuaThread thread,
    LuaFrame frame);
```

具体跨程序集调用通过版本化 Runtime code-generation facade 暴露。返回值携带 poll、call、
tail-call、return 或 deopt 等 tagged exit；不在生成代码的 CLR 栈上保存 Lua continuation。

大型 Lua 函数按基本块拆分，避免 CoreCLR JIT 与 NativeAOT 对超大方法退化。

当前 persisted CIL AOT v1 生成 deterministic PE、deterministic Portable PDB、canonical module
只读资源和 JSON manifest。manifest 固化 artifact schema、IR/Runtime ABI-v2/codegen 版本、module
content ID、options fingerprint、checksum 与 function/shard map。Portable PDB 同时保存 Lua source
checksum、source-line sequence point，以及 IL offset 到 canonical/logical PC 的 Lunil custom debug
record。普通 CoreCLR 通过显式 loader 先验证 manifest、版本与资源 checksum，再使用 collectible
`AssemblyLoadContext` 注册 function delegate；PE 尾部的 deterministic SHA-256 footer 覆盖完整映像，
损坏、错 ABI、错 module identity 或不匹配的 PDB 只返回稳定诊断。

M17 的 `LuaPersistedAotExecutor` 将已验证的 `LuaAotLoadedModule` 接回同一个
`LuaExecutionEngine`。compiled entry 先匹配 canonical module content ID，再按 function ID 查找
delegate；module 不匹配、function 缺失或 collectible artifact 已释放时，从当前 canonical PC 返回
`UnsupportedInstruction` deopt，由 reference executor 精确继续。调用方拥有 loaded module 的生命周期，
executor 分别记录 compiled invocation、artifact lookup fallback、全部 deopt、预期 debug-mode deopt
与非预期 deopt。loader 同时分别记录 validation、assembly
load、delegate binding、total duration 与 current-thread allocation。六 RID evidence 对同一 workload
matrix 验证稳态吞吐、分配斜率、加载 p95、artifact 体积和零 fallback；完整决策见
[ADR 0010](adr/0010-persisted-cil-aot-performance-productionization.md)。

.NET NativeAOT 通过 `Lunil.Build` 的 MSBuild task 在 `ResolveAssemblyReferences`/
`CoreCompile` 前完成 source/chunk 验证、canonical IR lowering 和 persisted CIL emission。
generated PE 进入宿主编译引用；`.g.cs` registry 通过 `ModuleInitializer` 和直接 method group
注册所有 function/shard，不使用 runtime reflection/discovery。registry 同时内嵌经过版本与 IR
verifier 检查的 canonical module，使宿主按 module name 创建 main closure，而不必在启动时重新解析
静态源码。NativeAOT 运行时不动态产生或加载 CIL；未预编译模块由 `LuaStaticAotExecutor` 精确
deopt 到共享解释器。

构建输出按 configuration/TFM/RID 隔离在 `obj/lunil/`。manifest 键包含 source hash、input kind、
optimization/debug/sandbox metadata、TFM 与 RID；命中时复用 PE/PDB/canonical module，仍在每次
MSBuild 中重建 `Compile`/`Reference` item graph。写入使用同目录临时文件、atomic replace 和
跨进程 output lock；design-time build 写入隔离目录且不执行 persisted CIL 编译，`Clean` 删除整个
Lunil intermediate root。

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

`0.7.0-alpha.1` 的 `Lunil.Hosting` 将现有标准库 capability provider 组合为显式
Trusted、Restricted 与 Deterministic profile。默认 Restricted 禁止文件系统、环境变量和进程
能力，并使用 binary-safe buffered console；Deterministic 进一步固定 UTC/Unix epoch、clock 和
table hash seed。宿主显式注入的 capability 优先于 profile 默认值，因此注入后确定性保证由宿主
承担。state/heap/stack/call-depth/instruction budget 仍由现有 Runtime option 直接控制，不在 Hosting
复制第二套配额实现。

`0.8.0-alpha.1` 在该 capability/profile 边界上增加独立的执行后端选择。`LuaHost` 的默认
`Auto` 在 CoreCLR 动态代码可用时惰性创建合格 `LuaJitExecutor`，在 NativeAOT 或禁用动态代码的
runtime 选择 reference interpreter；显式 `Interpreter` 固定 Tier 0，显式 `Jit` 则要求动态代码
能力。两条路径共享同一组 interpreter budget，JIT 拒绝仍精确回到 reference route。Host 公开
实际选择、动态代码能力和惰性 JIT telemetry，并在 dispose 时拥有/释放 executor。

## 11. 编译缓存

项目 artifact 默认位于 `obj/lunil/`；用户级 backend cache 默认位于 local app-data 下的
`Lunil/backend-cache`。当前持久化边界是 verified canonical IR、persisted CIL/PDB bundle 与
owner-free versioned profile，动态机器码只存在于进程内 code-byte LRU。

缓存键至少包含：

- 原始源码字节 SHA-256；
- Lua 版本和兼容 profile；
- LuaLS/legacy 方言及诊断配置；
- 编译器版本、artifact format 和 Runtime ABI；
- 优化、debug、hook、sandbox 配置；
- TFM、RID、架构、标准库 feature set；
- 静态依赖 hash；
- AOT、trimming 和 deterministic 配置。

缓存写入采用 durable flush、同卷临时目录、atomic directory rename 与跨进程独占锁。读取检查
descriptor/path identity、schema/ABI/target 兼容、长度、checksum、IR verifier、PE manifest/resources
和 PDB binding；损坏项进入独立 quota 的 quarantine 后重新编译。entry 使用 access marker LRU，
lock/I/O/权限失败 fail-soft。不使用 `BinaryFormatter`。动态 `require` 不伪装成静态依赖。相同内容
可以共享代码 artifact，但 source name、路径和 traceback binding 必须独立。完整格式见
[backend cache contract](backend-cache-contract.md)。

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
  Lunil.Core/
  Lunil.Syntax/
  Lunil.EmmyLua/
  Lunil.Analysis/
  Lunil.Semantics/
  Lunil.IR/
  Lunil.Runtime.Abstractions/
  Lunil.Runtime/
  Lunil.StandardLibrary/
  Lunil.CodeGen.Cil/
  Lunil.Compiler/
  Lunil.Workspace/
  Lunil.Hosting/
  Lunil.Build/
  Lunil.Cli/

tests/
  Lunil.Core.Tests/
  Lunil.Syntax.Tests/
  Lunil.Analysis.Tests/
  Lunil.Workspace.Tests/
  Lunil.Semantics.Tests/
  Lunil.Runtime.Tests/
  Lunil.StandardLibrary.Tests/
  Lunil.BackendDifferential.Tests/
  Lunil.Conformance.Tests/
  Lunil.Fuzz.Tests/
  Lunil.NativeAot.Tests/
  Lunil.Benchmarks/
```

Syntax 与 EmmyLua 不引用 Runtime；CodeGen 仅依赖稳定 Runtime ABI；Runtime 不反向依赖 Compiler；标准库通过 Runtime Abstractions 获取宿主能力。跨层数据结构放在最窄的共同抽象项目，禁止形成循环引用。

`0.7.0-alpha.1` 已落地 `Lunil.Compiler` 与 `Lunil.Hosting`；`0.7.0-alpha.2` 已落地
`Lunil.EmmyLua`；`0.7.0-alpha.3` 已落地 `Lunil.Analysis`；`0.7.0-alpha.4` 已落地
`Lunil.Workspace`；`0.7.0-alpha.5` 已落地 `Lunil.Cli`。Analysis 依赖 Core/Syntax/EmmyLua/Semantics；Compiler 依赖
Analysis/Core/Syntax/EmmyLua/Semantics/IR；Workspace 依赖 Compiler/Analysis；Hosting 依赖
Workspace/Compiler/IR/Runtime/StandardLibrary/CodeGen.Cil；Build 复用 Workspace 与 Compiler；CLI 组合
Compiler/Workspace/Hosting/IR/CodeGen/Runtime/StandardLibrary，同时保持命令解析、配置、诊断、
sandbox filesystem 与各命令执行器之间的单向依赖。
`Lunil.Runtime.Abstractions` 只在确认能够减少公共依赖且不破坏 NativeAOT/包边界时再拆分，
不得为满足目录名称而提交无行为的空项目。

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
- 以原生 PUC Lua 5.4.8 为固定 `1.000x` 归一化基准，同时对照 LuaJIT、MoonSharp 与
  Lunil 全部执行配置；
- Auto 与 Tier 2 必须在每个跨运行时 workload 上达到 MoonSharp 的 1.05 倍配对中位数，
  CI95 下界不低于 1.00 倍，并保持单一路由和零 timed-region fallback/deopt；
- 未通过全部语义测试的优化不得进入默认优化级别。

0.2.0 的 Windows x64/.NET 10 Release 基线使用 `benchmarks/Lunil.Runtime.Benchmarks` 固化。1,000,000 次 table get/set 的代表值约为 199 ns/op，单次 10,000 轮空 numeric-for 约 5.2 ms，带加法约 9.9 ms，1,000 个 table 的 full logical GC 约 0.71 ms。空循环的稳态执行固定分配约 5 KiB（frame/结果对象），不再随 10,000 次循环迭代增长；单元门槛为每次热执行不超过 16 KiB。数值仅作为本机回归基线，不是跨硬件的绝对门槛。

M8 可用 `scripts/Measure-BackendPerformance.ps1` 重复运行同进程内九次 cold sample 与跨进程
多轮统计。2026-07-12 Windows x64 三轮中位数显示 interpreter/Tier1/Tier2/Loop OSR 稳态分别为
5.796/11.656/12.135/3.500 ms/op；Tier 1 编译 p95 为 18.966 ms。结果只决定当前默认策略，
不会作为不同硬件间的绝对门槛；原始结果继续由六 RID CI 保存。

2026-07-13 的 M13 win-x64 五进程记录将相同 runner 扩展为 interpreter/Tier 1/Tier 2/
Loop-OSR-off/Loop-OSR 五行，并为 OSR 单独生成 qualification decision。exact-numeric OSR 的
arithmetic median 为 interpreter 的 8.516 倍、disabled 配置的 128.737 倍，compile p95 3.080 ms，
allocation slope 为 0，liveness cache hit 100%；负负载 on/off median 最低为 lua_calls 的 0.935 倍。
六 RID CI 只聚合每个 runner 的 decision，不把共享硬件绝对时间作为 build failure。

M14 将 on/off 顺序按进程交替，并把负负载 startup、automatic acceptance、guard failure 纳入门槛。
win-x64 五进程记录达到 8.670 倍 interpreter、115.995 倍 disabled、3.538 ms compile p95；四项负负载
warm median 为 0.993/0.982/1.007/0.987，startup median 均不低于 0.984，且 accepted、managed install
与 guard failure 均为 0。真实六 RID aggregate 仍是默认 rollout 的必要条件。

2026-07-16 的 M19 跨运行时门禁使用相同 Lua 源码、六轮平衡顺序和逐轮配对 ratio。
managed warmup 后在计时区外显式完成 CLR GC reset，避免将上一 runtime 的遗留 heap 状态带入
下一样本。win-x64 的九 engine × 八 workload × 六轮共 432 个结果全部正确；Auto/Tier 2
相对 MoonSharp 的 geomean 为 1.655/1.691 倍，逐 workload 最低为 `string_build` 的
1.233/1.233 倍，其 CI95 下界为 1.126/1.145。六 RID 聚合对缺失、重复、schema 不兼容、
incomplete 或任一 workload gate 失败均 fail closed；原生 Lua 始终保持报告中的 `1.000x` 基准。
真实 hosted run `29459923109` 的六个 RID 与 aggregate 全部通过：96 个 Auto/Tier 2 门禁无失败，
跨 48 个 measurement 的 MoonSharp-relative geomean 为 1.980/1.979 倍。

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
