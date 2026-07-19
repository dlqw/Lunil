# PUC Lua 5.4 prototype 导入契约

状态：0.4.0 已实现

输入版本：PUC Lua 5.4

输出版本：canonical IR v3

## 流水线

```text
binary bytes
  → Lua54ChunkReader（格式、宽度、预算）
  → Lua54ChunkVerifier（执行级结构与控制流）
  → Lua54PrototypeConverter（两遍 PC 映射）
  → LuaIrVerifier
  → LuaState 主闭包/upvalue 物化
  → LuaInterpreter
```

prototype 采用父函数先于子函数的 preorder 连续编号。转换先固定整棵树的函数 id，再逐函数生成指令和 raw-PC → canonical-PC 映射，最后修补所有控制流边与 debug-local 范围。因此扩展成多条 canonical 指令的 PUC opcode 仍有稳定 continuation PC。

## 指令语义

- register、constant、upvalue、closure、table 与 unary 指令直接映射或用三个保留 scratch register 展开；scratch 位于 PUC `MaximumStackSize` 之后。
- `MMBIN`、`MMBINI`、`MMBINK` 不形成独立 canonical 指令。前置算术和 fallback 合成单个可恢复 `Binary`，包括负 shift immediate 对 `__shl`/`__shr` 的事件切换及 operand flip。
- 比较/`TEST`/`TESTSET` 与紧随的 `JMP` 合成显式条件边；`LFALSESKIP` 保留跳过 `LOADTRUE` 的控制流。
- `LOADKX`、`NEWTABLE` 和扩展 `SETLIST` 消费 `EXTRAARG`；table 容量提示传入 heap 分配并受逻辑字节配额约束。
- `CALL`、`TAILCALL`、`RETURN`、`VARARG` 与 `SETLIST` 把 PUC 的零编码开放窗口转换成 canonical `-1`，不物化中间 CLR 数组。
- `TFORPREP` 标记 closing value，`TFORCALL` 使用 PUC 的 `A+4` 调用窗口，`TFORLOOP` 显式测试首结果并更新 control variable。
- `FORPREP`/`FORLOOP` 使用 PUC integer counter/float limit 双模式；integer counter 以 `ulong` 解释其 64 位 payload，支持完整 `long.MinValue` 到 `long.MaxValue` 范围。
- `TBC`/`CLOSE`、普通 return 和 tail call 均进入 0.3.0 冻结的可恢复 close/continuation ABI。

## 不可信输入不变量

chunk verifier 在转换前拒绝：

- 越界 register/range、constant、upvalue、prototype 与跳转；
- 错配或可被外部控制流进入的 `MMBIN*`、`EXTRAARG`、test companion `JMP`；
- 没有紧邻 top producer 的开放 call/return/set-list 窗口；
- 错配的 numeric/generic-for target 和 iterator window；
- 不一致的 `<close>` 标记顺序或控制流合流状态；
- 非法 line delta/absolute marker、local range 与 debug table 数量；
- 会溢出 runtime index/capacity 的扩展参数。

转换结果随后再次通过独立 canonical verifier。执行入口只创建当前 `LuaState` owner 的 closure、upvalue、string 与 table，不接受跨 state 引用。
