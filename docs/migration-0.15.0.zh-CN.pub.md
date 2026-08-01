# 从 Lunil 0.14 迁移到 0.15

[English](migration-0.15.0.pub.md)

Lunil 0.15 保持 0.14 的 compiler、runtime、hosting、analysis 与 engine 入口源码兼容。本版本
新增 opt-in native C ABI FFI 能力；所有默认行为不变，未配置 FFI 的 host 不受任何影响。

## 1. 更新包与工具

将全部 Lunil 包引用作为一个兼容线更新：

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.15.0" />
<PackageReference Include="Lunil.Hosting" Version="0.15.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.15.0
```

## 2. 理解新的 opt-in FFI 能力

`LuaStandardLibraryOptions.Ffi` 是新的默认关闭选项。启用它需要精确的 library 与 symbol
白名单；AOT 或 trimmed host 还需要精确 registry 绑定。见
[如何通过 FFI 调用原生代码](ffi.zh-CN.pub.md) 与 [FFI reference](ffi-reference.zh-CN.pub.md)。

`InstallAll` 不安装 `ffi` 模块。显式调用 `LuaStandardLibrary.InstallFfi(state, options)` 的
host 才会安装；只有选项启用时全局 `ffi` 表才会出现。

## 3. 兼容性检查清单

- 除非 host 显式授予 native loading，否则保持 `LuaStandardLibraryOptions.Ffi` 关闭；默认
  受限行为不变。
- `InstallPackage(LuaState)` 仍然可用；新的可选 options 重载不改变既有调用点。
- 既有标准库选项、package searcher 与 `package.loadlib` 不支持诊断不变。
- Lua 5.1–5.5 契约、canonical IR、可移植解释器与 .NET 10 JIT 不变。
- `0.15.0` 公共 API 基线在 `Lunil.StandardLibrary` 下新增 FFI 契约类型；没有移除任何既有
  公共成员。
