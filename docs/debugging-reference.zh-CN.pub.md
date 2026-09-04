# 调试参考

[English](debugging-reference.pub.md)

Lunil 调试适配器协议（DAP）集成参考：协议面、支持的请求与事件、执行模型，以及用于暴露调试
管道的宿主侧 API。

## 能力

| 领域 | 支持 |
| --- | --- |
| Launch | 在参考解释器下运行 `.lua` 文件并挂接调试会话。 |
| Attach | 连接游戏循环宿主的命名管道调试端点并中继协议。 |
| 断点 | 执行前设置的行断点；`setBreakpoints` 整体替换断点集。启动源码会做断点验证：命中可执行行的断点在请求行报告 `verified`，其余行向前吸附到最近可执行行并在响应中回填实际行号，无法映射的行报告 `verified: false` 并附 message；无法验证的源码保持请求行回显。 |
| 单步 | `stepIn`、`next`（跳过）、`stepOut`；不暴露步进粒度。 |
| 暂停 | 异步 `pause` 在下一个指令检查点挂起回合。 |
| 栈 | `stackTrace` 上报暂停线程的 Lua 帧与源码行。 |
| 作用域与变量 | 每帧的局部变量与上值；值为只读格式化。表值可展开，数组条目在前、哈希条目在后，支持 `start`/`count` 分页（单次响应最多 100 条），resume 后变量引用失效；Upvalues 作用域只列出上值。 |
| 线程 | attach 模式每个活动游戏循环操作一个 DAP 线程；launch 会话上报主线程。 |
| 事件 | `initialized`、`stopped`（breakpoint / step / pause 原因）、`terminated`、`output`。 |

## 限制

- **仅解释器后端。** CIL JIT 后端不分发调试 hook；`StartDebugServer` 与调试恢复对 JIT 宿主
  给出明确错误。
- **不支持表达式求值。** v1 未实现 `evaluate` 请求。
- **不支持条件断点、命中计数或日志点。** 断点只是行集合。
- **同一时间一个管道客户端。** 宿主服务一个 DAP 连接；断开后接受下一个连接，直到 server
  被释放。
- **同一时间一次暂停。** 每个宿主状态挂接一个调试会话；暂停回合中的协程一起挂起并按链恢复。

## 协议面

adapter 通过 stdio 使用 `Content-Length` 帧格式提供 DAP。attach 模式在客户端与宿主管道之间
逐帧转发，由宿主提供协议服务。

| 请求 | 行为 |
| --- | --- |
| `initialize` | 报告 `supportsConfigurationDoneRequest`；发出 `initialized`。 |
| `launch` | 要求 `program`；执行推迟到 `configurationDone` 之后。 |
| `attach` | 对已挂接状态的宿主而言只是形式确认。 |
| `setBreakpoints` | 替换给定源路径的断点集。 |
| `configurationDone` | 断点配置完成后开始执行（launch）。 |
| `continue` / `next` / `stepIn` / `stepOut` | 以请求的步进模式在下一个宿主 tick 恢复暂停回合。 |
| `pause` | 请求在下一个指令检查点暂停。 |
| `stackTrace` / `scopes` / `variables` | 读取暂停线程的帧、局部变量与上值。 |
| `threads` | 列出活动游戏循环操作（attach）或主线程（launch）。 |
| `disconnect` | 结束会话；宿主 detach 并恢复任何暂停回合。 |

## 执行模型

- **Launch：** adapter 在专用执行线程上运行脚本；暂停时等待 continue/step 命令，然后通过
  解释器的调试暂停路径恢复。
- **Attach：** 游戏循环宿主是唯一执行驱动器。暂停的操作跨 tick 保持挂起；恢复命令排队并在
  下一个 tick 通过 `LuaHost.ResumeDebuggedThread` 应用，它重新激活根线程及其挂起的协程链
  （`LuaExecutor.ResumeDebugged`）。
- **暂停信号：** 调试暂停以 `LuaVmSignal.Paused` 从引擎上浮。游戏循环操作将其映射为挂起
  状态而不破坏 tick 契约。

## 宿主 API

| 成员 | 用途 |
| --- | --- |
| `LuaGameLoopHost.StartDebugServer(pipeName)` | 在宿主上启动命名管道 DAP 端点；要求解释器后端。 |
| `LuaGameLoopDebugServer` | 管道 server：`PipeName`、`IsConnected`、`PausedOperation`；`Dispose()` 停止。 |
| `LuaHost.ResumeDebuggedThread(thread)` | 通过解释器恢复调试器暂停的线程（拒绝 JIT 宿主）。 |
| `LuaExecutor.ResumeDebugged(state, thread)` | 引擎级恢复暂停回合，重新激活协程链。 |
| `LuaInterpreter.ResumeDebugged(state, thread)` | 同一恢复路径的解释器入口。 |
| `LuaDebugSession` | 宿主侧会话：`Attach`、`SetBreakpoints`、`RequestPause`、`Continue`、`StepInto/Over/Out`、`Detach`。 |
| `LuaDebugApi` | 协议处理器使用的帧、局部变量、上值与 hook 查询。 |

断点、暂停与单步应用于所挂接状态的每个线程（含协程）；暂停回合挂起整个调度链。

## 参见

- [调试指南](debugging.zh-CN.pub.md) — launch 与 attach 操作指南。
- [游戏循环宿主](game-engine-hosting.zh-CN.pub.md) — 宿主侧调度与操作生命周期。
- [迁移指南](migration-0.16.0.zh-CN.pub.md) — 0.16 新增的调试与类型检查 API。
