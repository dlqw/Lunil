# 0.7.0 API and package compatibility

`0.7.0-beta.1` freezes the feature, public API, assembly, and NuGet package scope for the complete
`0.7.0` line. Beta, RC, and stable builds use the same versioned compatibility data under
`api/0.7.0/`.

## Public API baseline

[`api/0.7.0/manifest.json`](../api/0.7.0/manifest.json) pins 14 shipped assemblies and the SHA-256
of a generated C# declaration baseline for each one. The repository-local .NET tool manifest pins
`Meziantou.Framework.PublicApiGenerator.Tool` 2.0.2. The gate includes public and protected types,
members, generic constraints, parameter names and defaults, enum values, and nullable annotations.

The policy is an exact freeze: an addition is reviewed just as deliberately as a removal or
signature change. This is stricter than binary compatibility alone and prevents an accidental
implementation type from becoming supported API during stabilization.

```powershell
dotnet tool restore
./scripts/Test-PublicApiBaselines.ps1 -Configuration Release
```

Only an intentional, version-reviewed API decision may update the declarations:

```powershell
./scripts/Update-PublicApiBaselines.ps1 -Configuration Release
git diff -- api/0.7.0
```

Beta accepts compatibility fixes, but does not reopen planned feature scope. A required API change
must be documented in the matching changelog and must retain source and binary compatibility unless
it fixes a release blocker before `0.7.0` stable.

## Package baseline

[`api/0.7.0/packages.json`](../api/0.7.0/packages.json) freezes all 14 NuGet packages independent of
the active prerelease suffix. It records:

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
git diff -- api/0.7.0/packages.json
```

Normal CI and the tag-triggered release workflow both enforce these gates. A stable `0.7.1` fix may
add compatible API or assets only with a deliberate baseline update; breaking changes wait for the
next pre-1.0 minor compatibility line.
