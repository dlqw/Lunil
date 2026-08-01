# Native FFI 参考

[English](ffi-reference.pub.md)

Native C ABI FFI 能力为 opt-in 且默认关闭。本参考列出 Lua `ffi` 模块、签名语法、错误类别、
选项与平台 loader 行为。配置步骤见 [如何通过 FFI 调用原生代码](ffi.zh-CN.pub.md)。

## 全局 `ffi` 模块

当 `LuaStandardLibraryOptions.Ffi.Enabled` 为 `true` 时，由 `LuaStandardLibrary.InstallFfi`
安装。`InstallAll` 默认不安装。

| 成员 | 含义 |
| --- | --- |
| `ffi.enabled` | 模块安装后恒为 `true`。 |
| `ffi.version` | `"0.15"`，已安装模块表面版本。 |

## 函数

### `ffi.load(libraryName)`
加载白名单内的 native library 并返回 library userdata。
- 拒绝 `AllowedLibraryNames` 之外的名字，报 `LibraryNotAllowed`；拒绝 NUL 字符与 `..` 路径段。
- 通过配置的 `ILuaFfiLibraryLoader` 加载；加载失败报 `LibraryLoadFailed`，可用时包含
  native 错误详情。
- 重复加载返回独立 userdata；任一 lease 存在期间 native handle 保持存活
  （见 [生命周期](#生命周期与所有权)）。

### `ffi.bind(library, symbolName, signature[, convention])`
为白名单内的 `libraryName!symbolName` 返回可调用闭包。
- `signature` 使用紧凑声明形式，见 [签名](#签名)。
- `convention` 可选：`"default"`/`"platform"`、`"cdecl"` 或 `"stdcall"`；默认使用
  `LuaFfiOptions.DefaultCallingConvention`。
- 有注册绑定时，请求签名必须与注册签名完全一致，否则 `InvalidSignature`。
- 无注册绑定时，运行时动态代码可用则使用动态签名适配，否则 `DynamicCodeUnavailable`。
- 其库关闭后，绑定函数以 `LibraryClosed` 失败。

### `ffi.close(library)`
显式关闭 library userdata。同一 userdata 幂等；之后调用其绑定函数以 `LibraryClosed` 失败。

### `ffi.alloc(size)`
创建 `size` 字节（1 到 `MaximumBufferBytes`）的零初始化 native buffer，返回 buffer userdata；
超出 buffer 或分配预算分别报 `RangeExceeded` 或 `AllocationLimitExceeded`。

### `ffi.free(buffer)`
释放 native buffer。幂等；释放后访问报 `BufferClosed`。

### `ffi.read(buffer, offset, type)` / `ffi.write(buffer, offset, type, value)`
在 `offset` 处读写一个类型化值。越界访问报 `RangeExceeded`。`cstring` 读取在第一个 NUL
字节停止，并受 `MaximumStringBytes` 限制。

## 签名

紧凑 C ABI 声明：`returnType(paramType1, paramType2, ...)`。参数类型不能为 `void`；
varargs 与平台歧义的 `long`/`unsigned long` 被拒绝。

| 规范形式 | 接受的别名 | .NET 等价 | 大小 |
| --- | --- | --- | --- |
| `void` | — | `void` | 仅返回 |
| `bool` | `_bool`, `bool8` | `byte` | 1 |
| `i8` | `signed char`, `int8`, `int8_t` | `sbyte` | 1 |
| `u8` | `unsigned char`, `uint8`, `uint8_t` | `byte` | 1 |
| `i16` | `short`, `int16`, `int16_t` | `short` | 2 |
| `u16` | `unsigned short`, `uint16`, `uint16_t` | `ushort` | 2 |
| `i32` | `int`, `int32`, `int32_t` | `int` | 4 |
| `u32` | `unsigned`, `unsigned int`, `uint32`, `uint32_t` | `uint` | 4 |
| `i64` | `long long`, `int64`, `int64_t` | `long` | 8 |
| `u64` | `unsigned long long`, `uint64`, `uint64_t` | `ulong` | 8 |
| `isize` | `intptr_t`, `intptr` | `IntPtr` | pointer-sized |
| `usize` | `uintptr_t`, `uintptr`, `size_t` | `UIntPtr` | pointer-sized |
| `f32` | `float` | `float` | 4 |
| `f64` | `double` | `double` | 8 |
| `cstring` | `char*`, `const char*`, `utf8`, `utf8string` | UTF-8 字符串 | 终止符 |
| `pointer` | `void*`, `ptr`, `pointer` | 原始指针 | pointer-sized |

Lua 值严格转换：整数参数要求精确 Lua 整数，数字参数要求 Lua number，`cstring` 参数要求
Lua 字符串，`pointer` 参数接受 `nil`、buffer 或指针。超出 Lua 整数范围的无符号结果报
`RangeExceeded`。

## 错误类别

Lua 侧失败以 `ffi {Code}: {message}` 形式呈现，携带稳定 `LuaFfiErrorCode`。

| 代码 | 含义 |
| --- | --- |
| `Disabled` | 该 Lua state 未启用 FFI。 |
| `InvalidName` | 名字为空或含 NUL。 |
| `LibraryNotAllowed` | library 不在 `AllowedLibraryNames` 中或含 `..`。 |
| `LibraryLoadFailed` | native loader 失败；可用时包含 native 详情。 |
| `LibraryClosed` | 关闭后使用 library 或绑定函数。 |
| `SymbolNotAllowed` | `library!symbol` 不在白名单。 |
| `SymbolNotFound` | 已加载库中未找到导出。 |
| `InvalidSignature` | 签名格式错误或与注册绑定不一致。 |
| `UnsupportedSignature` | 签名无法适配为调用。 |
| `InvalidArgument` | Lua 值无法表示 native 参数。 |
| `RangeExceeded` | 值或 buffer 访问超出支持范围。 |
| `NativeInvocationFailed` | native 调用本身抛错。 |
| `BufferClosed` | 释放后使用 buffer。 |
| `AllocationLimitExceeded` | native 分配预算耗尽。 |
| `ResourceLimitExceeded` | 达到打开库数量上限。 |
| `DynamicCodeUnavailable` | 需要动态适配但不可用。 |
| `BindingConflict` | 同一 library/symbol 的重复注册。 |

## 选项与上限

`LuaFfiOptions` 默认值；非法组合在构造期被拒绝。

| 选项 | 默认 | 说明 |
| --- | --- | --- |
| `Enabled` | `false` | FFI 默认关闭。 |
| `AllowedLibraryNames` | 空 | 启用时至少需要一个精确名字。 |
| `AllowedSymbolNames` | 空 | `library!symbol` 条目；注册绑定同样计入。 |
| `BindingRegistry` | `null` | 精确 AOT/trimmed 绑定。 |
| `LibraryLoader` | 系统 loader | .NET 10 用 `NativeLibrary`；其他平台用 P/Invoke。 |
| `MaximumOpenLibraries` | `32` | 1–4096。 |
| `MaximumSignatureLength` | `256` | 8–65536。 |
| `MaximumArgumentCount` | `8` | 0–32。 |
| `MaximumStringBytes` | `1 MiB` | 1–1 GiB。 |
| `MaximumBufferBytes` | `16 MiB` | 1–1 GiB。 |
| `MaximumAllocationBytes` | `16 MiB` | 1–2^62。 |
| `DefaultCallingConvention` | `PlatformDefault` | `ffi.bind` 省略 convention 时使用。 |

## 平台 loader 行为

- **.NET 10 目标**使用 `NativeLibrary.Load`/`GetExport`/`Free`。
- **其他目标**使用平台 P/Invoke：Windows 为
  `LoadLibraryW`/`GetProcAddress`/`FreeLibrary`，Linux 与 macOS 为
  `dlopen`/`dlsym`/`dlclose`。Linux 加载从 `libdl.so.2` 回退到 `libdl.so` 以支持 musl 系
  发行版（如 Alpine Linux）。
- 失败详情：Windows 报告 `GetLastWin32Error` 消息；Linux 与 macOS 报告 `dlerror`。
- `package.loadlib` 仍为显式不支持的 Lua C module 诊断；FFI 是独立的、受白名单约束的
  能力，不加载 Lua C module。

## 生命周期与所有权

- Library userdata 与绑定闭包各自持有 native handle 的独立 lease。最后一个 lease 释放或
  库被显式关闭时，handle 被卸载。
- `__gc` 与 `__close` metamethod 释放原生资源；finalizer 路径不会逃逸到 Lua 收集器。
- 指针结果为空时返回 `nil`；非空指针成为 pointer userdata，其所属库关闭后拒绝访问。
