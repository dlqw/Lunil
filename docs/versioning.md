# Version management

Lunil follows SemVer 2.0.0. Package versions, assembly informational versions,
Git tags, changelog names, binary bundle names, and GitHub releases all derive from
one version declared in `Directory.Build.props`:

```xml
<VersionPrefix>0.6.0</VersionPrefix>
<VersionSuffix>alpha.9</VersionSuffix>
```

The resulting version is `0.6.0-alpha.9` and its tag is `v0.6.0-alpha.9`.
`VersionSuffix` is removed for a stable release.

## Compatibility while below 1.0

- Minor releases such as `0.6.0` may contain breaking public API changes.
- Patch releases such as `0.6.1` are backward-compatible fixes and refinements.
- Every intentional break must be identified in the matching changelog.
- `1.0.0` will declare the first supported stable public API contract.

## Prerelease channels

1. `alpha.N`: active feature and API development; incomplete behavior is expected.
2. `beta.N`: feature scope is frozen; compatibility, diagnostics, and performance are
   being hardened.
3. `rc.N`: release candidate; only release-blocking fixes are accepted.
4. Stable: remove the suffix after an accepted release candidate, producing `X.Y.Z`.

Promotion for this milestone is therefore:

```text
0.6.0-alpha.1 -> alpha.2 -> alpha.3 -> alpha.4 -> alpha.5 -> ... -> beta.1 -> ... -> rc.1 -> ... -> 0.6.0
```

Numbers increase monotonically within a channel. Published versions and tags are
immutable and are never deleted and reused. A fix after publication always receives a
new prerelease number or patch version. Build metadata (`+metadata`) is reserved for
local/CI provenance and is not used in release tags or package identities.

## Release procedure

1. Create a `release/<version>` branch when stabilization outside `main` is needed.
2. Set `VersionPrefix` and `VersionSuffix` and create `changelogs/<full-version>.md`.
3. Open a pull request; all protected CI checks must pass before squash merge.
4. From the accepted commit on `main`, create the immutable `v<full-version>` tag.
5. The release workflow validates all version sources, builds six RID bundles and
   symbol-enabled NuGet packages, publishes packages, and creates the GitHub release.
6. Versions containing a suffix are marked GitHub prereleases automatically; versions
   without a suffix are stable releases.
