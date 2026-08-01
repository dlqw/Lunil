# 如何通过 FFI 调用原生代码

[English](ffi.pub.md)

本指南为显式配置的 `LuaHost` 启用 opt-in native C ABI FFI 能力并从 Lua 使用它。该能力默认
关闭；未授权的 host 保持受限行为不变。

## 前置条件

- Lunil `0.15.0` 或更新版本，并引用 `Lunil.StandardLibrary` 包。
- 受信任的 host 决策：授予 native loading，并提供精确的 library 与 symbol 身份。
- AOT 或 trimmed 发布需要精确的 host 注册绑定（见 [AOT bindings](aot-bindings.zh-CN.pub.md)）。

## 1. 通过标准库选项授予 FFI

`LuaStandardLibraryOptions.Ffi` 默认关闭。启用时必须提供精确白名单，并可选择受 host
控制的 loader：

```csharp
using Lunil.StandardLibrary;

var options = new LuaStandardLibraryOptions
{
    Ffi = new LuaFfiOptions
    {
        Enabled = true,
        AllowedLibraryNames = ["gamecore"],
        AllowedSymbolNames = ["gamecore!score_add"],
        MaximumOpenLibraries = 8,
    },
};
```

library 白名单或 symbol 白名单为空时，配置在构造期即被拒绝。NUL 字符与路径穿越段在所有
入口都被拒绝；只有精确白名单条目可以被加载或绑定。

安装标准库时应用该选项：

```csharp
using Lunil.Runtime;
using Lunil.StandardLibrary;

var state = new LuaState();
LuaStandardLibrary.InstallBasic(state, options);
LuaStandardLibrary.InstallFfi(state, options);
```

安装后全局 `ffi` 表可用且 `ffi.enabled` 为 `true`；保持 `LuaFfiOptions.Enabled` 默认值的
host 永远不会看到该模块。

## 2. 加载库并绑定函数

`ffi.load` 返回 library userdata，`ffi.bind` 返回可调用闭包：

```lua
local lib = ffi.load('gamecore')
local add = ffi.bind(lib, 'score_add', 'i32(i32, i32)')
local value = add(20, 22)
ffi.close(lib)
```

签名使用紧凑 C ABI 声明形式 `returnType(param1, param2)`。支持的类型为定宽整数、
pointer-sized 整数、布尔、`float`/`double`、UTF-8 字符串（`cstring`）与原始指针；完整
别名表见 [FFI reference](ffi-reference.zh-CN.pub.md)。Variadic 声明与 `long` 等平台歧义类型不被
接受；pointer-sized 值请使用 `intptr_t`/`uintptr_t`/`size_t`。

动态签名适配需要运行时动态代码。AOT 或 trimmed host 应通过 registry 绑定精确 symbol
（见 [第 5 节](#5-为-aot-与-trimming-绑定精确-registry-条目)）。

## 3. 管理原生生命周期

Library userdata 通过 lease 保持其 native handle 存活。每次 `ffi.load` 结果与每个绑定函数
各持有一个 lease；最后一个 lease 释放或被显式关闭时，handle 才会被卸载：

```lua
ffi.close(lib)          -- 显式关闭；后续调用以 'LibraryClosed' 失败
lib = nil               -- 或释放引用，让垃圾回收器关闭它
```

`__gc` 与 `__close` metamethod 在收集或 to-be-closed 路径下释放原生资源。库关闭后调用其
绑定函数会以稳定的 `ffi LibraryClosed` 错误失败，而不会触碰 native 内存。
## 4. 与原生代码交换有界内存

`ffi.alloc` 创建零初始化的 native buffer，`ffi.read`/`ffi.write` 带边界检查访问：

```lua
local buffer = ffi.alloc(32)
ffi.write(buffer, 0, 'i32', 42)
ffi.write(buffer, 4, 'cstring', 'hello')
local number = ffi.read(buffer, 0, 'i32')      -- 42
local text = ffi.read(buffer, 4, 'cstring')    -- 'hello'
ffi.free(buffer)
```

每次 native 分配都计入配置的分配预算；超出以 `AllocationLimitExceeded` 失败。
`ffi.free` 幂等，释放后访问以 `BufferClosed` 失败。Buffer 可以作为 `pointer` 参数传给
native 函数。

## 5. 为 AOT 与 trimming 绑定精确 Registry 条目

无动态代码的 host 注册精确绑定并安装 registry-only loader：

```csharp
var registry = new LuaFfiBindingRegistry();
registry.Register("gamecore", "score_add", "i32(i32, i32)", AddNative);

var options = new LuaStandardLibraryOptions
{
    Ffi = new LuaFfiOptions
    {
        Enabled = true,
        AllowedLibraryNames = ["gamecore"],
        AllowedSymbolNames = ["gamecore!score_add"],
        BindingRegistry = registry,
        LibraryLoader = RegistryOnlyLoader.Instance,
    },
};
```

```csharp
private static object? AddNative(ReadOnlySpan<object?> arguments) =>
    checked((int)arguments[0]! + (int)arguments[1]!);
```

注册签名必须与 `ffi.bind` 请求的签名完全一致；不一致以 `InvalidSignature` 失败。运行时
无法提供动态代码时，registry 路径是唯一受支持的路由，任何动态解析尝试以
`DynamicCodeUnavailable` 失败。registry 选项同样通过 [第 1 节](#1-通过标准库选项授予-ffi)
的 `LuaStandardLibrary.InstallFfi(state, options)` 安装步骤应用。

## 6. 诊断失败

所有 Lua 侧失败都携带稳定错误类别，格式为 `ffi {Code}: {message}`——例如
`ffi LibraryNotAllowed: native library 'other' is not allowlisted.`。平台 loader 错误在可用时
包含 native 详情：Windows 报告 `GetLastWin32Error` 消息，Unix-like 报告 `dlerror`；Linux
加载从 `libdl.so.2` 回退到 `libdl.so` 以支持 musl 系发行版。完整错误码列表见
[FFI reference](ffi-reference.zh-CN.pub.md)。
