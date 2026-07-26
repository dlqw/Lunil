# 迁移到 Lunil 0.10.0

[English](migration-0.10.0.pub.md)

Lunil 0.10 在 parsing、compilation、binary chunk、hosting 和 runtime state 中显式标识 Lua
语言版本。未选择版本时默认使用 Lua 5.4。Module 与创建其 closure 的 `LuaState` 必须使用相同版本：

```csharp
using Lunil.Compiler;
using Lunil.Core;
using Lunil.Runtime;

var version = LuaLanguageVersion.Lua54;
var compilation = new LuaCompiler(new LuaCompilerOptions
{
    LanguageVersion = version,
}).CompileUtf8(source, "@module.lua");
var state = new LuaState(new LuaStateOptions { LanguageVersion = version });
var closure = state.CreateMainClosure(compilation.Module!);
```

`LuaHostOptions.LanguageVersion`、compiler/parser/binder option 和
`LuaStateOptions.LanguageVersion` 使用同一个 identity。CLI 通过 `--lua-version` 和
`LUNIL_LUA_VERSION` 公开此设置：

```text
lunil run module.lua --lua-version 5.1
lunil build module.lua --target chunk --lua-version 5.5
LUNIL_LUA_VERSION=5.3 lunil check module.lua
```

支持值为 `5.1`、`5.2`、`5.3`、`5.4` 和 `5.5`。Lunil 会拒绝不可用的 adapter，绝不会替换为
其他版本。每个版本都有自己的 PUC binary-chunk reader/writer，因此 chunk 不能跨版本使用。

## 1. 配置 Lua 5.1 function environment

Lua 5.1 profile 提供 `getfenv`、`setfenv` 和 `module`。Closure 的 legacy environment 控制其
global 读写，包括从 Lua 5.1 chunk 导入的 closure。改变 environment 会改变该 closure 后续的
global lookup，但不会改变 module 或 state 的语言版本。

需要这些函数的代码必须选择 Lua 5.1。Lua 5.2+ state 不会根据源码自动推断旧语义。

## 2. 让 `require` 使用 state 版本

一个 `LuaState` 只有一个语言契约。`require` 加载的 module 使用该 state 的 `LanguageVersion`
编译；module 文件不会隐式选择其他版本。必须在不同 Lua 契约下运行的 module 应使用不同 state：

```csharp
var state51 = new LuaState(new LuaStateOptions
{
    LanguageVersion = LuaLanguageVersion.Lua51,
});
var state54 = new LuaState(new LuaStateOptions
{
    LanguageVersion = LuaLanguageVersion.Lua54,
});
```

`LuaState.CreateMainClosure` 会拒绝语言版本与 state 不一致的 canonical module。

## 3. 替换已移除的 Lua AOT API

Persisted/static Lua AOT 产品、disk cache、生成的 static registry 和 `Lunil.Build` package 已
不可用。通过 `LuaHost`、`LuaInterpreter` 或 `LuaJitExecutor` 执行 runtime-compiled source；
需要分发预编译输入时使用经过验证的可移植 PUC chunk。已移除的 API 名称与 CLI 输入见
[0.8.0 迁移指南](migration-0.8.0.zh-CN.pub.md)。
