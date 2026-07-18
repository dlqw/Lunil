# Versioned API and package compatibility

Lunil keeps compatibility data by pre-1.0 minor line. Historical data is immutable, and the active
Beta line uses exact public-API and package gates so additions, removals, signature changes, or
asset changes cannot enter accidentally.

## Compatibility lines

### Frozen `0.7.0`

`0.7.0-beta.1` froze the feature, public API, assembly, and NuGet package scope for the complete
`0.7` line. The declarations, manifest, and package assets under [`api/0.7.0/`](../api/0.7.0/)
must continue to match the stable `v0.7.0` tag; later feature work never rewrites them.

### Active `0.8.0`

[`api/0.8.0/`](../api/0.8.0/) freezes the public API, assembly, and package scope accepted at
`0.8.0-beta.1`. Its exact gates reject additions, removals, signature or nullability changes,
dependency edges, package identities, and package-asset changes throughout Beta, RC, and stable
`0.8.x` maintenance unless a deliberate backward-compatible fix requires a reviewed baseline
update. Breaking work waits for `0.9.0`.

The frozen `0.8` contract intentionally widens the advanced
`LuaCompiledExit.InstructionsConsumed` code-generation ABI property from `int` to `long`. The
scheduler's 64-bit activation budget and remaining JIT emitters prevent one compiled entry from
overflowing after more than `Int32.MaxValue` canonical instructions. See
[ADR 0013](adr/0013-64-bit-instruction-accounting.md).

`0.8.0-alpha.12` also removes the Lua persisted/static AOT API, disk-cache API, `Lunil.Build`
assembly/package, and metadata emitter surface. This is an intentional breaking change relative to
stable `0.7.0`; .NET NativeAOT/trimming compatibility remains supported. See
[ADR 0018](adr/0018-remove-lua-aot.md).

`0.8.0-alpha.9` intentionally adds the public immutable `LuaFunctionVersion` and
`LuaFunctionIdentity` inspection surface, structured reload function-migration results, the
`IsBackendGenerationCurrent` code-generation safepoint, and conservative
`LuaJitProfileRemapper` result/status types. These additions are versioned Alpha contracts backed
by ADR 0017; they do not modify the frozen `0.7.0` declarations.

The validation scripts derive the active compatibility line from `Directory.Build.props`; with an
active `0.8.0-beta.1` version they read and update only `api/0.8.0/`.

## Public API baseline

The active `manifest.json` pins 13 shipped assemblies and the SHA-256 of a generated C# declaration
baseline for each one. The repository-local .NET tool manifest pins
`Meziantou.Framework.PublicApiGenerator.Tool` 2.0.2. The gate includes public and protected types,
members, generic constraints, parameter names and defaults, enum values, and nullable annotations.

```powershell
dotnet tool restore
./scripts/Test-PublicApiBaselines.ps1 -Configuration Release
```

Only an intentional, backward-compatible and version-reviewed API decision may update the active
declarations:

```powershell
./scripts/Update-PublicApiBaselines.ps1 -Configuration Release
git diff -- api/0.8.0
git diff --exit-code v0.7.0 -- api/0.7.0
```

Beta and RC updates are limited by the promotion policy in [versioning](versioning.md); new API and
backend features wait for `0.9.0`.

## Package baseline

The active `packages.json` records all 13 NuGet packages independently of the prerelease suffix. It
pins:

- package identity, author, license, repository, readme, description, tags, and package type;
- exact target-framework dependency groups and same-version Lunil dependency edges;
- normal package asset paths, symbol-package asset paths, and the .NET tool layout;
- the required one-to-one set of 13 `.nupkg` and 13 `.snupkg` outputs.

Every packable project also enables the SDK package validator with strict compatible-TFM and
compatible-framework checks. The compatibility script restores a clean project against the local
package directory only, references all 12 library packages, executes the public `LuaHost`
path, installs `Lunil.Cli`, verifies its exact version, and executes a source file.

```powershell
$version = ./scripts/Get-LunilVersion.ps1
./scripts/New-NuGetPackages.ps1 -Version $version
./scripts/Test-PackageCompatibility.ps1 `
  -Version $version -PackageDirectory artifacts/packages -NoPack
```

The update command is reserved for a reviewed package-boundary change:

```powershell
./scripts/Update-PackageBaseline.ps1 -Version $version
git diff -- api/0.8.0/packages.json
git diff --exit-code v0.7.0 -- api/0.7.0/packages.json
```

Normal CI and tag-triggered release workflows enforce the active gates. Stable patch fixes update
the accepted baseline only when a deliberate compatible surface or asset change requires it;
breaking changes wait for the next pre-1.0 minor compatibility line.
