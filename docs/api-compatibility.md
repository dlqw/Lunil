# API and package compatibility

This guide describes how Lunil keeps public APIs and NuGet packages compatible across pre-1.0 minor lines.

## Compatibility baselines

Each supported minor line has a declaration and package baseline under [`api/`](../api/). A baseline records the public/protected .NET surface, nullable annotations, generic constraints, package metadata, dependency groups, and package assets for that line. Stable baselines are immutable: a patch must retain source and binary compatibility with the baseline for its line.

The repository contains baselines for `0.7.0`, `0.8.0`, `0.9.0`, `0.10.0`, and `0.11.0`. They are independent compatibility contracts, so a change in one line does not rewrite an older line.

## Public contracts by line

- `0.8` uses 64-bit `LuaCompiledExit.InstructionsConsumed`, allowing a compiled entry to account for more than `Int32.MaxValue` canonical instructions. It does not provide Lua persisted/static AOT APIs or the `Lunil.Build` package; .NET NativeAOT and trimming remain supported deployment modes.
- `0.10` exposes `LuaLanguageVersion`, `LuaChunkFormat`, `LuaVersionProfileAttribute`, `LuaVersionFeatures`, and `LuaVersionFeatureTable`. Compiler, runtime, hosting, workspace, canonical-module, and closure options carry `LanguageVersion` where callers can observe the language boundary. The CLI accepts `--lua-version`, `languageVersion`, and `LUNIL_LUA_VERSION`.
- `0.11` adds the opt-in CLR bridge in `Lunil.Hosting`. Its allowlists, capabilities, type descriptions, object wrappers, and error codes are public API; see [CLR interoperation](clr-interop.md).

## Verifying a consumer package

A package consumer can restore Lunil from a package directory, compile against the desired public surface, and execute a `LuaHost` program. The repository provides the following reproducible commands for maintainers and package consumers:

```powershell
dotnet tool restore
./scripts/Test-PublicApiBaselines.ps1 -Configuration Release

$version = ./scripts/Get-LunilVersion.ps1
./scripts/New-NuGetPackages.ps1 -Version $version
./scripts/Test-PackageCompatibility.ps1 `
  -Version $version -PackageDirectory artifacts/packages -NoPack
```

Use the matching baseline directory when evaluating a compatibility claim. A change that adds, removes, or changes a publicly callable contract belongs to the compatibility line whose package declares it.
