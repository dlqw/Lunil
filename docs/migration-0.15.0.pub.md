# Migrate from Lunil 0.14 to 0.15

[简体中文](migration-0.15.0.zh-CN.pub.md)

Lunil 0.15 keeps the 0.14 compiler, runtime, hosting, analysis, and engine entry points source
compatible. The release adds an opt-in native C ABI FFI surface; every default behavior stays
unchanged, and hosts that do not configure FFI see no difference.

## 1. Update packages and tools

Update all Lunil package references as one compatibility line:

```xml
<PackageReference Include="Lunil.StandardLibrary" Version="0.15.0" />
<PackageReference Include="Lunil.Hosting" Version="0.15.0" />
```

```bash
dotnet tool update --global Lunil.Cli --version 0.15.0
```

## 2. Understand the new opt-in FFI capability

`LuaStandardLibraryOptions.Ffi` is a new disabled-by-default option. Enabling it requires exact
library and symbol allowlists and, for AOT or trimmed hosts, exact registry bindings. See
[How to call native code through FFI](ffi.pub.md) and the [FFI reference](ffi-reference.pub.md).

`InstallAll` does not install the `ffi` module. Hosts that call
`LuaStandardLibrary.InstallFfi(state, options)` explicitly install it; the global `ffi` table
appears only when the option is enabled.

## 3. Compatibility checklist

- The default language contract is unchanged: Lua 5.4 remains the default language contract
  and PUC Lua 5.4.8 is its compatibility baseline; the wording is clarified, not a behavior change.
- Keep `LuaStandardLibraryOptions.Ffi` disabled unless the host explicitly grants native
  loading; the default restricted behavior is unchanged.
- `InstallPackage(LuaState)` remains available; the new optional-options overload does not
  change existing call sites.
- Existing standard-library options, package searchers, and the `package.loadlib` unsupported
  diagnostic are unchanged.
- Lua 5.1–5.5 contracts, the canonical IR, the portable interpreter, and the .NET 10 JIT are
  unchanged.
- Public API baselines for `0.15.0` add the FFI contract types under
  `Lunil.StandardLibrary`; no existing public member is removed.
