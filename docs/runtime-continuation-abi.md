# Runtime continuation ABI（0.3.0）

## 目标

0.3.0 将所有可恢复执行边界统一为显式、可遍历的 tagged continuation。canonical IR
保持版本 2；Lua 的 yield 仍是带 `MayYield` effect 的 `Call` 结果，不增加专用指令。

> 0.4.0 因增加 PUC debug provenance 将 canonical 格式升级为 v3；本文件冻结的
> call/yield/close opcode 与 continuation 约定未改变。

## Lua continuation

`LuaContinuationKind` 覆盖普通 Lua call、tail call、protected call/error handler、
return/`__close`、coroutine yield/resume 注入以及 native callback/native yield。状态只能包含：

- continuation id、结果数量和其他整数状态；
- Lua 栈中的 base/count/expected-results 窗口；
- 经所属 `LuaHeap` 验证并由逻辑 GC 遍历的 `LuaValue` slots；
- 结果 transform、protected boundary 和 close-handler tag。

VM 不把 CLR delegate closure 当作 continuation。resumable native descriptor 必须使用静态
方法组入口（不能使用编译器生成 target 的 lambda）；Lua 捕获值必须放入 `LuaNativeClosure`
的 owner-aware slots。

## Native step

`LuaNativeFunctionStepBody` 接收 `LuaNativeCallContext`、整数 continuation id 和当前输入值窗口，
只返回以下三类结果：

- `Completed`：native 调用完成，values 是调用结果；
- `CallLua`：调用指定 Lua callable，Lua 返回后以 continuation id 再次进入同一 descriptor；
- `Yielded`：向 resumer 返回 values，下次 resume 时以 continuation id 再次进入 descriptor。

descriptor 是可跨 state 共享的无 owner 代码描述；`LuaNativeClosure` 是逻辑堆对象，拥有
descriptor、显式捕获 slots、barrier 和 GC traversal。每次调用的 continuation state 保存在
调用 frame/root continuation 中，因此同一个 closure 可重入，且不会把 Lua 对象藏在 CLR 闭包中。

## Thread 与 scheduler

内部状态为 `New`、`Suspended`、`Running`、`Normal`、`Dead`、`Error`。`Start`、`Resume`、
`Close` 使用显式 activation stack；nested `coroutine.resume` 只切换 activation，不递归调用
CLR `Execute`。每个 activation 有独立指令预算。首次 resume 值作为 entry 参数，后续 resume
值注入 yield/native-yield 的结果窗口。

active resumer/resumee、entry、suspended frames、yield/resume windows、root/frame continuation、
terminal error 和 native closure captures 都属于逻辑 GC 根图。steady-state 内部 coroutine
切换复用 activation 与值窗口；只有宿主边界会物化 `ImmutableArray` 结果。

## Close 与错误协程

未受保护的 coroutine 错误将状态置为 `Error`，但保留 frame、open upvalue、continuation 和
to-be-closed slots。`coroutine.close` 才以原始错误展开 errored coroutine；suspended coroutine
以 nil error 展开。显式 close/finalizer 是不可 yield 边界，普通 return 中的 Lua `__close`
可以 yield。closer 错误替换当前错误，但剩余 closer 仍按逆序执行；最终关闭 upvalue，并清除
frame、continuation 和栈根。
