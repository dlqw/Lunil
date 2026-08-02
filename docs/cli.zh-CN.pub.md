# Lunil 命令行参考

[English](cli.pub.md)

`Lunil.Cli` 通过 `lunil` .NET tool 与可执行文件公开 Lunil 的 compiler、workspace、host 和
binary-chunk 契约。本页是信息型参考；需要查找命令、选项、默认值或退出码时可直接定位对应小节。

签名更新 bundle 可通过 `lunil patch pack`、`verify`、`inspect`、`diff` 和 `dry-run` 使用。
验证操作接受一组 `--public-key`/`--key-id`，或使用带 key 轮换与撤销窗口的版本化
`--trust-store`。信任与资源边界见[签名 Patch Bundle 参考](signed-patch-bundles.zh-CN.pub.md)。
`patch inspect` 会报告签名的 `updateIntent`、canonical `requiredCapabilities` 和精确匹配的
`requiredTargetLabels` claim。

## 命令

```text
lunil run <input|-> [options] [-- script-args...]
lunil check <input...> [options]
lunil build <input> --output <path> [--target chunk] [options]
lunil patch <pack|verify|inspect|dry-run|diff> ... [options]
lunil dump <input> [--kind <kind>] [--format text|json] [options]
```

- `run` 在执行前分析 source workspace，也接受所选语言版本对应的已验证 PUC Lua chunk。
  `--` 后的参数会成为 main chunk 的 vararg 与 `arg[1..n]`；`arg[0]` 是输入标识。
- `check` 接受一个或多个 source root，并生成确定性的跨 module 诊断。Binary chunk 会经过结构
  验证，但没有 source annotation/type view。
- `build --target chunk` 为所选版本写出 PUC Lua chunk，默认版本为 Lua 5.4。Workspace 会为每个
  已解析 module 写出一个 `.luac`；`--strip-debug` 删除 chunk debug data。
- `dump` 支持 `summary`、`syntax`、`annotations`、`analysis`、`ir` 和 `chunk`，格式可以是
  text 或 `lunil.dump.v1` JSON。`--output -` 或省略输出路径时写入 stdout。
- `--lua-version 5.1|5.2|5.3|5.4|5.5` 选择语言与 chunk 契约。不支持的 identity 会产生诊断，
  不会回退到 Lua 5.4。

`--output` 适用于 `build`、`dump` 和 `patch pack`，但语义由 command 决定：`build` 与
`patch pack` 要求 filesystem path，只有 `dump` 接受表示 stdout 的 `-`。`--target`、
`--strip-debug` 仅适用于 `build`；`--kind`、`--format` 仅适用于 `dump`。当前唯一
build target 是 `chunk`。旧 AOT target 输入会以 phase `removed-feature`、诊断 `LUNIL0006`
和退出码 `2` 失败。

## 选项

| 选项 | 值/默认值 | 适用范围 | 说明 |
| --- | --- | --- | --- |
| `-h`、`--help` | Flag | 全部 | 显示全局帮助和 command-specific 补充。 |
| `--version` | Flag | 全部 | 输出 Lunil 版本。 |
| `--config` | Path | 全部 | 读取指定 `lunil.json`；不能与 `--no-config` 组合。 |
| `--no-config` | Flag | 全部 | 关闭 `lunil.json` 自动发现。 |
| `--diagnostic-format` | `text`（默认）、`json` | 全部 | 选择 stderr 诊断序列化格式。 |
| `--module-root` | 可重复 path | Source command | 添加 module resolver 与 sandbox root。 |
| `--path-pattern` | `?.lua`、`?/init.lua` | Source command | 添加 Lua `?` path pattern。 |
| `--module-name` | Name | `run`、单 root `check`、`build`、`dump` | 覆盖 root logical module name。 |
| `--profile` | `trusted`（默认）、`sandbox`、`deterministic` | 全部 | 选择 host capability profile；`restricted` 是 `sandbox` 的别名。 |
| `--trusted` | Flag | 全部 | 选择 trusted profile。 |
| `--sandbox` | Flag | 全部 | 选择限制在 root 内的只读 profile。 |
| `--deterministic` | Flag | 全部 | 选择带确定性时间与 hash 的 sandbox capability。 |
| `--lua-version` | `5.4`（默认）；`5.1`–`5.5` | Source/chunk command | 选择语言与 chunk 契约。 |
| `--execution` | `auto`（默认）、`interpreter`、`jit` | 全部 | 选择 `run` 使用的执行 backend。 |
| `--warnings-as-errors` | Flag | Source command | 将 analysis warning 提升为 error。 |
| `--no-warnings-as-errors` | Flag | Source command | 覆盖已启用的配置或环境设置。 |
| `--suppress` | 可重复的码 | `check`、`build`、`dump` | 抑制一个 analysis diagnostic 码（例如 `LUA6022`）。 |
| `-o`、`--output` | Path | `build` | 写入 chunk 文件；path 表示目录时，每个 module 写入一个 `.luac`；stdout 列出每个构件 path。 |
| `-o`、`--output` | Path 或 `-` | `dump` | 写入 dump 文件；省略该选项或使用 `-` 时将 dump payload 写入 stdout。 |
| `-o`、`--output` | Path | `patch pack` | 将签名 bundle 写入该 path；stdout 输出 bundle path。 |
| `--target` | `chunk`（默认） | `build` | 选择 build target。 |
| `--strip-debug` | `false`（默认） | `build` | 删除 chunk debug data。 |
| `--kind` | `summary`（默认）、`syntax`、`annotations`、`analysis`、`ir`、`chunk` | `dump` | 选择 dump view。 |
| `--format` | `text`（默认）、`json` | `dump` | 选择 dump serialization。 |
| `--key-id` | Identifier | `patch` | 选择 signing 或 verification key identity。 |
| `--private-key` | PEM path | `patch pack` | 读取 ECDSA P-256 private key。 |
| `--public-key` | PEM path | Patch verification action | 读取一个 ECDSA P-256 public key。 |
| `--trust-store` | JSON path | Patch verification action | 使用版本化 multi-key store 代替 `--public-key`/`--key-id`。 |
| `--maximum-input-bytes` | `67108864` | 全部 | 限制每个输入与解析到的 module。 |
| `--maximum-instructions` | `100000000` | `run` | 限制每次执行的 VM instruction。 |
| `--maximum-stack-slots` | `1000000` | `run` | 限制 VM stack slot。 |
| `--maximum-call-depth` | `20000` | `run` | 限制 Lua call depth。 |
| `--maximum-heap-bytes` | `268435456` | `run` | 限制 logical Lua heap byte。 |

## 输入与 module

`-` 从 stdin 读取一个 UTF-8 source document；binary chunk 只能从文件读取。`--module-name`
覆盖 root logical name。`--module-root` 与 `--path-pattern` 可重复；pattern 默认为 `?.lua` 和
`?/init.lua`。静态 direct-global literal `require` 与 API compilation 使用同一个 workspace
module graph。

`run` 的程序输出写入 stdout。`build` 和 `patch pack` 将已写入的 filesystem path 输出到
stdout；`dump` 仅在省略 `--output` 或将其设为 `-` 时把 payload 写入 stdout。诊断写入
stderr。`--maximum-input-bytes` 限制每个输入和解析到的 module。

## 配置

优先级从低到高为：

```text
built-in defaults < lunil.json < LUNIL_* environment < CLI/response-file arguments
```

CLI 会在当前目录查找 `lunil.json`。使用 `--config <path>` 指定文件，或使用 `--no-config`
关闭自动发现。未知 property、无效类型、超限文件以及同时使用这两个选项都会产生 usage error。

```json
{
  "profile": "deterministic",
  "luaVersion": "5.4",
  "execution": "auto",
  "diagnosticFormat": "json",
  "buildTarget": "chunk",
  "dumpKind": "analysis",
  "dumpFormat": "json",
  "moduleRoots": ["src", "vendor"],
  "pathPatterns": ["?.lua", "?/init.lua"],
  "warningsAsErrors": true,
  "stripDebug": false,
  "maximumInputBytes": 67108864,
  "maximumInstructions": 100000000,
  "maximumStackSlots": 1000000,
  "maximumCallDepth": 20000,
  "maximumHeapBytes": 268435456
}
```

相对 `moduleRoots` 从配置文件所在目录解析。等价环境变量为 `LUNIL_PROFILE`、
`LUNIL_LUA_VERSION`、`LUNIL_EXECUTION`、`LUNIL_DIAGNOSTIC_FORMAT`、`LUNIL_BUILD_TARGET`、
`LUNIL_DUMP_KIND`、`LUNIL_DUMP_FORMAT`、`LUNIL_MODULE_ROOTS`、`LUNIL_PATH_PATTERNS`、
`LUNIL_WARNINGS_AS_ERRORS`、`LUNIL_STRIP_DEBUG`、`LUNIL_MAXIMUM_INPUT_BYTES`、
`LUNIL_MAXIMUM_INSTRUCTIONS`、`LUNIL_MAXIMUM_STACK_SLOTS`、`LUNIL_MAXIMUM_CALL_DEPTH` 和
`LUNIL_MAXIMUM_HEAP_BYTES`。

## Response file

以 `@` 开头的参数会展开一个 UTF-8 response file。嵌套 response-file 路径相对于包含它的文件。
支持引号、受支持的反斜杠转义、空行和 `#` 注释；`@@value` 生成字面量 `@value`。展开过程会拒绝
cycle，并限制文件大小、嵌套深度和参数数量。

## 执行 profile 与预算

`--execution auto|interpreter|jit` 选择 `run` backend。`auto` 在受支持时使用动态代码 backend，
否则使用参考解释器。`interpreter` 确定性关闭 JIT。`jit` 要求支持动态代码；JIT 无法编译的函数
仍会回退解释器。`build`、`check` 与 `dump` 会验证此选项，但不执行 Lua。

- `--trusted` 使用普通 host capability。
- `--sandbox` 提供限制在 root 内的只读 filesystem；root 外路径、写入、临时文件以及
  symlink/reparse-point 穿越都会被拒绝。
- `--deterministic` 在 sandbox capability 上增加确定性的时间与 hash 行为。

所有 profile 都执行 instruction、stack-slot、call-depth、logical-heap 和 input-byte 预算。

## 诊断与退出码

`--diagnostic-format text|json` 选择 text 或稳定的 `lunil.diagnostics.v1` envelope。
`--warnings-as-errors` 将 analysis warning 提升为 error。Ctrl+C 请求协作式取消。

| 代码 | 含义 |
| ---: | --- |
| `0` | 成功；可能仍输出 warning |
| `1` | Source、workspace、chunk 或 analysis error |
| `2` | Usage 或 configuration error |
| `3` | 输入或输出获取失败 |
| `4` | Lua 执行错误或 runtime budget 失败 |
| `5` | 构件 build 或 host 失败 |
| `130` | 取消 |
