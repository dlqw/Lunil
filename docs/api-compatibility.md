# Versioned API and package compatibility

Lunil keeps compatibility data by pre-1.0 minor line. Historical data is immutable, while the
active Alpha line uses the same exact gates as a reviewed snapshot so API and package changes
cannot enter accidentally.

## Compatibility lines

### Frozen `0.7.0`

`0.7.0-beta.1` froze the feature, public API, assembly, and NuGet package scope for the complete
`0.7` line. The declarations, manifest, and package assets under [`api/0.7.0/`](../api/0.7.0/)
must continue to match the stable `v0.7.0` tag; later feature work never rewrites them.

### Active `0.8.0`

[`api/0.8.0/`](../api/0.8.0/) records the reviewed public API and package snapshot for
`0.8.0-alpha.1`. Alpha remains open to intentional, changelog-backed API changes, so this is not a
Beta freeze. The exact gate still rejects an unreviewed addition, removal, signature change,
dependency edge, or package asset. Entering `0.8.0-beta.1` will turn the accepted snapshot into the
frozen contract for the rest of the `0.8` line.

The validation scripts derive the active compatibility line from `Directory.Build.props`; with an
active `0.8.0-alpha.1` version they read and update only `api/0.8.0/`.

## Public API baseline

The active `manifest.json` pins 14 shipped assemblies and the SHA-256 of a generated C# declaration
baseline for each one. The repository-local .NET tool manifest pins
`Meziantou.Framework.PublicApiGenerator.Tool` 2.0.2. The gate includes public and protected types,
members, generic constraints, parameter names and defaults, enum values, and nullable annotations.

```powershell
dotnet tool restore
./scripts/Test-PublicApiBaselines.ps1 -Configuration Release
```

Only an intentional, version-reviewed API decision may update the active declarations:

```powershell
./scripts/Update-PublicApiBaselines.ps1 -Configuration Release
git diff -- api/0.8.0
git diff --exit-code v0.7.0 -- api/0.7.0
```

Alpha changes must be described in the matching changelog and retain correct runtime/package
behavior. Beta and RC follow the stricter promotion policy in [versioning](versioning.md).

## Package baseline

The active `packages.json` records all 14 NuGet packages independently of the prerelease suffix. It
pins:

- package identity, author, license, repository, readme, description, tags, and package type;
- exact target-framework dependency groups and same-version Lunil dependency edges;
- normal package asset paths, symbol-package asset paths, the MSBuild task layout, and the .NET tool
  layout;
- the required one-to-one set of 14 `.nupkg` and 14 `.snupkg` outputs.

Every packable project also enables the SDK package validator with strict compatible-TFM and
compatible-framework checks. The compatibility script restores a clean project against the local
package directory only, references all 13 library/MSBuild packages, executes the public `LuaHost`
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
