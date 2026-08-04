# 使用调试适配器协议调试 Lua

[English](debugging.pub.md)

本指南使用 Lunil 的调试适配器协议（DAP）集成调试 Lua 脚本：从 VS Code 启动独立脚本，或
attach 到暴露调试管道的运行中游戏循环宿主。支持面与限制见
[调试参考](debugging-reference.zh-CN.pub.md)。

## 前置条件

- VS Code 1.96 及以上版本，已安装 Lunil 插件（见 [VS Code 指南](vscode.zh-CN.pub.md)）。
- 脚本必须在参考解释器后端上运行。CIL JIT 后端不分发调试 hook，因此调试会话要求解释器宿主
  （下面的 VS Code launch 与 attach 配置已选择解释器）。

## 1. 启动 Lua 脚本

创建 `.vscode/launch.json`，加入 `lunil` 类型的 launch 配置：

```json
{
  "version": "0.2.0",
  "configurations": [
    {
      "type": "lunil",
      "request": "launch",
      "name": "Debug Lua script",
      "program": "${workspaceFolder}/main.lua"
    }
  ]
}
```

选择该配置并按 **F5**。插件启动 `lunil-debug-adapter`，它在解释器下运行脚本并挂接调试会话。

停止时可执行：

- 在编辑器边栏设置与清除断点；
- 用暂停按钮暂停运行中的脚本；
- 单步 **进入**、**跳过**、**跳出** 函数；
- 在调试面板查看调用栈、局部变量与上值。

脚本未发生暂停并完成时，会把结果打印到调试控制台并结束会话。

## 2. Attach 到游戏循环宿主

[游戏循环宿主](game-engine-hosting.zh-CN.pub.md)可以暴露命名管道调试端点。宿主程序用如下代码
启动端点：

```csharp
using var debugServer = gameLoop.StartDebugServer("lunil-debug");
```

可移植宿主示例接受命令行参数 `--debug-pipe <name>`：

```bash
dotnet run --project samples/Lunil.Portable.Hosting -- --debug-pipe lunil-debug
```

然后在 VS Code 中用指向同一管道的 `attach` 配置连接：

```json
{
  "type": "lunil",
  "request": "attach",
  "name": "Attach to Lunil host",
  "debugPipe": "lunil-debug"
}
```

adapter 连接管道并在 VS Code 与宿主之间中继协议。宿主自身提供断点、暂停、单步与栈检查，
同时保持自己的 tick 循环作为唯一的执行驱动器：

- 断点在宿主 tick 脚本之前设置；
- 命中后挂起操作回合并上报 `stopped`；
- `continue`、`next`、`stepIn`、`stepOut` 在下一个 tick 恢复回合；
- 客户端断开时，宿主 detach 调试会话并恢复任何暂停的回合，游戏循环在无调试器的情况下继续
  运行。

## 3. 直接运行 adapter

adapter 是随 VSIX 一起发布的控制台程序（`server/<rid>/lunil-debug-adapter`）。它有两种模式：

```bash
# launch 模式：在 stdio 上提供 DAP 会话（VS Code launch 使用）
lunil-debug-adapter --stdio

# attach 模式：在 stdio 与宿主调试管道之间中继 DAP 会话（VS Code attach 使用）
lunil-debug-adapter --stdio --pipe <name>
```

attach 模式双向逐帧转发，保留请求序号，使响应与客户端待处理请求精确匹配。

## 参见

- [调试参考](debugging-reference.zh-CN.pub.md) — 能力、限制与宿主 API。
- [游戏循环宿主](game-engine-hosting.zh-CN.pub.md) — 宿主侧调度与 tick 契约。
- [VS Code 指南](vscode.zh-CN.pub.md) — 插件安装与 language server 配置。
