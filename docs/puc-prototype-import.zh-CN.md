# PUC Lua 5.4 prototype 导入

[English](puc-prototype-import.md)

输入：PUC Lua 5.4 binary chunk。输出：canonical IR。

## 导入路径

```text
binary bytes
  → Lua54ChunkReader（格式、宽度、预算）
  → Lua54ChunkVerifier（执行级结构与控制流）
  → Lua54PrototypeConverter（两遍 PC 映射）
  → LuaIrVerifier
  → LuaState 主闭包/upvalue 物化
  → LuaInterpreter
```

Prototype 使用父函数先于子函数的 preorder 连续编号。转换器先固定整棵树的 function id，再逐函数生成指令和 raw-PC → canonical-PC 映射，最后修补控制流边与 debug-local 范围。因此一个扩展成多条 canonical 指令的 PUC opcode 仍有稳定的 continuation PC。

## 指令语义

- register、constant、upvalue、closure、table 和 unary 指令直接映射，或使用位于 PUC `MaximumStackSize` 之后的三个保留 scratch register 展开。
- `MMBIN`、`MMBINI`、`MMBINK` 与前置算术/fallback 合成一个可恢复 `Binary`；负 shift immediate 会选择 `__shl`/`__shr` 并调整 operand 顺序。
- 比较、`TEST`、`TESTSET` 与紧随的 `JMP` 合成显式条件边；`LFALSESKIP` 保留跳过 `LOADTRUE` 的控制流。
- `LOADKX`、`NEWTABLE` 和扩展 `SETLIST` 消费 `EXTRAARG`；table 容量提示受逻辑字节配额约束。
- `CALL`、`TAILCALL`、`RETURN`、`VARARG` 和 `SETLIST` 将 PUC 的零编码开放窗口转换为 canonical `-1`，不创建中间 CLR array。
- `TFORPREP` 标记 closing value，`TFORCALL` 使用 PUC 的 `A+4` 调用窗口，`TFORLOOP` 显式测试首结果并更新 control variable。
- `FORPREP`/`FORLOOP` 支持 PUC integer counter 和 float limit 两种形式；integer counter 以 `ulong` 解释其 64 位 payload，覆盖完整 `long.MinValue` 到 `long.MaxValue` 范围。
- `TBC`/`CLOSE`、普通 return 和 tail call 进入统一的可恢复 close/continuation ABI。

## 不可信输入不变量

chunk verifier 在转换前拒绝：

- 越界 register/range、constant、upvalue、prototype 或跳转；
- 错配或可被外部控制流进入的 `MMBIN*`、`EXTRAARG`、test companion `JMP`；
- 未紧邻 top producer 的开放 call/return/set-list 窗口；
- 错配的 numeric/generic-for target 或 iterator window；
- 不一致的 `<close>` 标记顺序或控制流合流状态；
- 非法 line delta/absolute marker、local range 或 debug table 数量；
- 会溢出 runtime index/capacity 的扩展参数。

转换结果会再次通过独立 canonical verifier。执行入口只创建当前 `LuaState` owner 的 closure、upvalue、string 和 table，不接受跨 state 引用。
