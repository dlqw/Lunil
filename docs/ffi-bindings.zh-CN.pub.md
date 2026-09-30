# 生成 FFI binding

[English](ffi-bindings.pub.md)

声明要绑定的原生签名，让 FFI binding generator 生成强类型 invoker，使被绑定的 symbol 在
NativeAOT 与 trimmed 运行时无需运行时 delegate 生成即可使用。本 how-to 覆盖声明请求、注册
生成的 provider 与阅读构建诊断。

## 前置条件

- 已[通过标准库选项授予 FFI 模块](ffi.zh-CN.pub.md)，且被绑定的 library 与 symbol 在 allowlist 内。
- 项目引用 `Lunil.StandardLibrary` 包；generator 以 analyzer 资产随包分发，无需额外引用。

## 1. 每个 symbol 声明一条请求

对每个 `(library, symbol)` 组合添加一条程序集级 attribute，签名与后续 `ffi.bind` 请求的完全
一致：

```csharp
using Lunil.StandardLibrary;

[assembly: LuaFfiGenerateBinding("gamecore", "score_add", "i32(i32, i32)")]
[assembly: LuaFfiGenerateBinding("gamecore", "describe", "cstring(i32)", "cdecl")]
```

签名使用 `ffi.bind` 的紧凑声明语法（`i32`、`f64`、`cstring`、`pointer` 等）。调用约定默认
`platform`，也可声明为 `cdecl` 或 `stdcall`。

## 2. 注册生成的 provider

```csharp
var registry = new LuaFfiBindingRegistry();
new Lunil.Generated.LuaFfiGeneratedBindings().RegisterBindings(registry);
```

像手写条目一样通过 `LuaFfiOptions.BindingRegistry` 传入 registry；[手写 registry 流程](ffi.zh-CN.pub.md)
及其 allowlist 规则保持不变。生成的 invoker 在绑定时从已加载的 library 解析 symbol 地址，
并通过编译期 native delegate 封送，因此每次调用都跳过运行时 delegate 生成与
`Delegate.DynamicInvoke`。

## 3. 阅读诊断

不支持的签名、未知的调用约定与重复的 `(library, symbol)` 请求会在构建期以 `LUNILFFI001`
和 `LUNILFFI002` 失败，而不会留到运行时。生成 binding 与后续 `ffi.bind` 调用之间的签名不匹配
仍与手写条目一样以 `InvalidSignature` 失败。

## 预期结果

被绑定的 symbol 在 NativeAOT、IL2CPP 与 trimmed 运行时上通过与手写 binding 相同的 registry
路径运行。动态签名路径、手写 invoker 与 buffer 所有权见[使用原生 FFI](ffi.zh-CN.pub.md)与
[FFI 参考](ffi-reference.zh-CN.pub.md)。
