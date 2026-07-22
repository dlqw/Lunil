# Runtime continuation ABI

[English](runtime-continuation-abi.md)

Runtime continuation ABI 将所有可恢复执行边界表示为显式、可遍历的 tagged continuation。Lua 的 yield 仍是带 `MayYield` effect 的 `Call` 结果；continuation 用来保存恢复所需状态，而非增加另一套 Lua 调用语义。

## Lua continuation

`LuaContinuationKind` 覆盖普通 Lua call、tail call、protected call/error handler、return/`__close`、coroutine yield/resume 注入、native callback 和 native yield。continuation 只保存：

- continuation id、结果数量和其他整数状态；
- Lua 栈中的 base/count/expected-results 窗口；
- 经所属 `LuaHeap` 验证并由逻辑 GC 遍历的 `LuaValue` slot；
- result transform、protected boundary 和 close-handler tag。

VM 不将 CLR delegate closure 用作 continuation。resumable native descriptor 使用静态方法组入口；Lua 捕获值存入 `LuaNativeClosure` 的 owner-aware slot，使逻辑 GC 可以遍历它们。

## Native step

`LuaNativeFunctionStepBody` 接收 `LuaNativeCallContext`、整数 continuation id 和当前输入值窗口，并只返回以下结果：

- `Completed`：native 调用完成，values 是调用结果；
- `CallLua`：调用指定 Lua callable，Lua 返回后以 continuation id 再次进入同一 descriptor；
- `Yielded`：向 resumer 返回 values，下次 resume 时以 continuation id 再次进入 descriptor。

Descriptor 是可跨 state 共享的无 owner 代码描述。`LuaNativeClosure` 是逻辑堆对象，保存 descriptor、显式捕获 slot、write barrier 和 GC traversal。每次调用的 continuation state 位于调用 frame 或 root continuation，因此同一 closure 可以重入，且 Lua 对象不会隐藏在 CLR 闭包中。

## Coroutine 与 scheduler

Coroutine 具有 `New`、`Suspended`、`Running`、`Normal`、`Dead`、`Error` 生命周期状态。`Start`、`Resume`、`Close` 使用显式 activation stack；嵌套 `coroutine.resume` 切换 activation，而不递归调用 CLR `Execute`。每个 activation 使用独立指令预算。第一次 resume 值作为 entry 参数，后续 resume 值注入 yield/native-yield 的结果窗口。

active resumer/resumee、entry、suspended frame、yield/resume window、root/frame continuation、terminal error 和 native closure capture 都是逻辑 GC 根。内部 coroutine 切换复用 activation 与值窗口；只有宿主边界物化 `ImmutableArray` 结果。

## Close 与错误

未受保护的 coroutine 错误进入 `Error`，同时保留 frame、open upvalue、continuation 和 to-be-closed slot。`coroutine.close` 负责展开 errored coroutine；suspended coroutine 以 nil error 展开。显式 close/finalizer 是不可 yield 边界，普通 return 的 Lua `__close` 可以 yield。closer error 替换当前错误，但剩余 closer 仍按逆序执行；最后关闭 upvalue 并清除 frame、continuation 和栈根。
