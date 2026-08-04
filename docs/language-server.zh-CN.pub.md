# Lunil language server reference

[English](language-server.pub.md)

`lunil-language-server` 是面向 Lunil Lua parser、semantic analysis、workspace index 与外部 host
contract 的自包含 LSP 3.17 server。它只通过标准输入/输出上的 JSON-RPC 通信。

## 命令行

| 命令 | 结果 |
| --- | --- |
| `lunil-language-server --stdio` | 启动 server；`--stdio` 可省略。 |
| `lunil-language-server --version` | 输出 Lunil 产品版本并退出。 |

未识别参数会以状态 2 退出；若同时提供 `--version`，则优先输出版本并以状态 0 退出。Protocol log
必须写入标准错误；向标准输出写入非 LSP 文本会破坏连接。Request handler 意外失败时，server
返回简洁的 JSON-RPC internal error，并把 method、request ID 与完整 managed exception stack
写入标准错误。Notification handler 意外失败也会记录到标准错误，且不会停止连接。

## Document 与 workspace 模型

- Position 使用从零开始的 UTF-16 line/character offset。
- Text synchronization 为 incremental，并拒绝过期 document version。
- 已打开但未保存的 document 会覆盖对应磁盘文件。
- 初始化后仍可增删 workspace folder。
- 受监视 `.lua` 文件变化会使后台 compact index 失效。
- Workspace discovery 排除 `.git`、`.svn`、`bin`、`obj`、`node_modules`、`.vscode` 与 `.idea`。
- Compact summary/index 缓存在操作系统 local application-data 目录下；只为活跃 query
  materialize 完整 compiler model。

## 标准能力

Server 提供 diagnostic、completion（`.`、`:`、`@` trigger）、hover、signature help、definition、
declaration、type definition、implementation、reference、prepare rename/rename、document/workspace
symbol、完整与 delta semantic token、inlay hint、call hierarchy、folding range、selection range 与
quick-fix code action。

当事实足够精确时，navigation/reference 会覆盖 lexical name、table member、method、module export/
re-export、metatable-backed member、prototype method、closure upvalue、callback registration、
persistence schema 与外部宿主定义。动态 Lua operation 保持保守，只返回 candidate 或 unresolved
结果，不虚构 target。

## 配置

通过 `workspace/didChangeConfiguration` 发送顶层对象或 `settings.lunil` 对象：

```json
{
  "settings": {
    "lunil": {
      "hostContractPath": "/absolute/path/to/lunil-host-contract.json",
      "hostContractJson": ""
    }
  }
}
```

非空 `hostContractJson` 优先；path 由 server process 读取。修改任一值都会重建 workspace analysis
domain，并使旧结果失效。

## Lunil protocol extension

| Method | 类型 | 结果 |
| --- | --- | --- |
| `lunil/reindex` | Request | 重建当前 workspace index。 |
| `lunil/clearCache` | Request | 清除内存与 workspace index cache。 |
| `lunil/virtualHostDocument` | Request | 返回当前 host contract 的 Lua declaration 视图。 |
| `lunil/indexProgress` | Notification | 报告 phase、已完成/总 work item 与可选 module。 |

Client 声明支持时，server 也使用标准 work-done progress。Reference request 可通过标准
partial-result token 流式返回结果。

## Host-analysis contract

`LuaHostAnalysisContract` schema 1 描述注入的 global、module、function、overload、定义与实现位置、
side effect、callback lifetime 和 persistence operation。使用 `ToJson()` 生成 JSON，`ParseJson()`
解析，并用 `ToLuaStub()` 创建确定性的 LuaLS-compatible declaration。

Function effect 区分 global/table access、yield、throw、callback registration/unregistration，以及
persistence read/write/delete/clear。Persistence entry 携带 schema ID/version、value type、migration
function、key/value parameter position 和 missing read 是否返回 `nil`。

## 资源限制

JSON-RPC header 上限为 16 KiB，message 上限为 32 MiB。Workspace 默认允许 65,536 个 module、
1 GiB Lua source、1,048,576 条 dependency、4,096 个 pending work item、512 MiB memory cache 与
2 GiB disk cache。需要不同预算的 embedder 应直接使用 `LuaWorkspace`；standalone server 只暴露
以上稳定 editor 配置。
