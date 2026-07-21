# Version management

Lunil follows [Semantic Versioning 2.0.0](https://semver.org/). Package versions, assembly
informational versions, Git tags, changelog names, bundles, and GitHub releases derive from
`Directory.Build.props`:

```xml
<VersionPrefix>0.10.0</VersionPrefix>
```

This example produces `0.10.0`. Stable tags `v0.8.0`, `v0.9.0`, and `v0.10.0` remain immutable.

## Compatibility lines

- Before `1.0.0`, a new minor version may intentionally change public API or behavior. Every
  breaking change must be documented in the matching changelog and migration guide.
- Patch releases contain backward-compatible fixes and refinements for an existing stable line.
- Published versions, tags, packages, and release assets are immutable and are never reused.
- Build metadata is reserved for local or continuous-integration provenance and is not part of
  release tags or package identities.

| Line | Status | Change policy |
| --- | --- | --- |
| `0.8.x` | Maintained | Backward-compatible fixes only |
| `0.9.x` | Maintained | Backward-compatible fixes only |
| `0.10.x` | Stable | Backward-compatible fixes only |
| `0.10.0-alpha.N` | Published development history | Immutable prerelease evidence |
| `0.10.0-beta.N` | Unused channel | No release published |
| `0.10.0-rc.N` | Published release-candidate history | Immutable prerelease evidence |

## Prerelease channels

1. `alpha.N` permits active feature and API development.
2. `beta.N` freezes feature and public API scope. New features move to the next milestone.
3. `rc.N` accepts release blockers only. A substantial redesign returns to an appropriate
   prerelease channel.
4. Stable removes the suffix only after an accepted release candidate. A milestone does not jump
   directly from Alpha or Beta to stable.

Numbers increase monotonically within a channel and restart at `1` when entering the next channel:

```text
0.9.0-alpha.1 -> ... -> 0.9.0
0.10.0-alpha.1 -> ... -> 0.10.0
```

## Versioned assets

Every version keeps these assets consistent:

1. `VersionPrefix` and `VersionSuffix` in `Directory.Build.props`;
2. `changelogs/<full-version>.md` containing community-facing release notes;
3. the active `api/<minor>/` public API and package baselines;
4. runtime ABI, profile, telemetry, and artifact schema versions when applicable;
5. benchmark data and charts for performance-sensitive releases;
6. the immutable `v<full-version>` tag, packages, bundles, and GitHub release.

## Release procedure

1. Prepare stabilization on `release/<version>` when needed.
2. Set the version and add the matching changelog.
3. Open a pull request and pass all protected checks.
4. Merge the reviewed source into `main`.
5. Create the immutable `v<full-version>` tag from the accepted commit.
6. The release workflow validates version consistency, builds six RID bundles and symbol-enabled
   packages, publishes the packages, and creates the GitHub release.

Versions with a suffix are published as prereleases. Versions without a suffix are stable releases.
