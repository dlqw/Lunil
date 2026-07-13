# Version management

Lunil follows SemVer 2.0.0. Package versions, assembly informational versions,
Git tags, changelog names, binary bundle names, and GitHub releases all derive from
one version declared in `Directory.Build.props`:

```xml
<VersionPrefix>0.7.0</VersionPrefix>
<VersionSuffix>alpha.2</VersionSuffix>
```

The resulting version is `0.7.0-alpha.2` and its tag is `v0.7.0-alpha.2`.
`VersionSuffix` is removed for a stable release.

The three numeric fields select the compatibility line; the optional suffix selects
the maturity channel of a build on that line. A backend passing its performance gates
does not by itself justify removing the suffix or promoting the entire product.

## Choosing `X.Y.Z`

- `X` is the stable compatibility generation. Before `1.0.0` it remains `0`. After
  `1.0.0`, increment it only for intentional breaking changes to the supported stable API.
- `Y` identifies the next pre-1.0 development milestone. After `1.0.0`, it identifies a
  backward-compatible feature release. Starting work on the next planned milestone after
  the `0.6.0` development line therefore uses `0.7.0-alpha.1`, not `0.6.1-alpha.1`.
  A prerelease line may be superseded without publishing its suffix-free version; that
  decision must be recorded explicitly and never mutates its published tags.
- `Z` identifies backward-compatible fixes and refinements to a stable release line. A fix
  for published `0.7.0` uses `0.7.1`; a fix while `0.7.0` is still in prerelease increments
  that prerelease channel instead, for example `0.7.0-alpha.2`.

Do not increment a numeric field merely because one feature branch was merged. Choose the
numeric line from the intended compatibility milestone, then choose the suffix from the
actual maturity and allowed-change policy.

| Development work | Version action |
| --- | --- |
| Add or redesign a planned `0.7.0` feature while scope is still open | Publish the next `0.7.0-alpha.N` |
| Freeze the complete `0.7.0` feature/API scope | Promote to `0.7.0-beta.1` |
| Fix compatibility, diagnostics, docs, or performance during beta | Publish the next `0.7.0-beta.N` |
| Declare the hardened beta ready for release-candidate validation | Promote to `0.7.0-rc.1` |
| Fix a release blocker during RC | Publish the next `0.7.0-rc.N` |
| Accept an RC without further code changes | Remove the suffix and publish `0.7.0` from the accepted commit |
| Fix a backward-compatible defect after stable `0.7.0` | Publish `0.7.1` |
| Begin the next feature/API milestone after `0.7.0` | Start `0.8.0-alpha.1` |

Documentation-only release preparation does not consume a new prerelease number when the
current number has not been published. Once `v0.7.0-alpha.2` exists, every code or release
metadata correction must use `0.7.0-alpha.3` or a later appropriate version.

## Compatibility while below 1.0

- Minor releases such as `0.7.0` may contain breaking public API changes.
- Patch releases such as `0.7.1` are backward-compatible fixes and refinements.
- Every intentional break must be identified in the matching changelog.
- `1.0.0` will declare the first supported stable public API contract.

## Prerelease channels

1. `alpha.N`: active feature and API development; incomplete behavior and breaking changes
   are expected. New planned features may enter.
2. `beta.N`: feature and public-API scope is frozen; compatibility, diagnostics,
   documentation, and performance are being hardened. New planned features wait for the
   next numeric milestone.
3. `rc.N`: release candidate; only release-blocking fixes are accepted. Any substantial
   feature or API redesign returns the milestone to an appropriate prerelease channel.
4. Stable: remove the suffix only after an accepted release candidate, producing `X.Y.Z`.
   A project must not jump directly from alpha or beta to a suffix-free release.

Promotion for this milestone is therefore:

```text
0.7.0-alpha.1 -> alpha.2 -> ... -> beta.1 -> ... -> rc.1 -> ... -> 0.7.0
```

Numbers increase monotonically within a channel and restart at `1` when the milestone
enters a new channel (`alpha.N` to `beta.1`, not `beta.N+1`). Published versions and tags
are immutable and are never deleted and reused. An unpublished version may be refined on
its release branch before tagging, but a fix after publication always receives a new
prerelease number or patch version. Build metadata (`+metadata`) is reserved for local/CI
provenance and is not used in release tags or package identities.

## Transition from `0.6.0-alpha.14` to `0.7.0-alpha.1`

The interpreter, qualified CoreCLR Tier 1/Tier 2 JIT, exact-numeric loop OSR, persisted
CIL AOT, build-time AOT, and NativeAOT integration have multi-platform CI and release-gate
evidence. This made `0.6.0-alpha.14` suitable for an alpha prerelease build.

The full `0.6.0` public API and feature scope, complete official Lua test-suite coverage,
and long-term compatibility/support contract were not frozen. The project therefore ended
the `0.6.0` line at the immutable `0.6.0-alpha.14` preview instead of publishing a
misleading beta, RC, or suffix-free `0.6.0`.

New compiler, analysis, hosting, and package-boundary work starts at
`0.7.0-alpha.1`. Its scope and promotion gates are defined in the
[`0.7.0` roadmap](roadmap-0.7.0.md).

## Current `0.7.0-alpha.2` decision

`0.7.0-alpha.1` established the public compiler and hosting boundaries. The current
`0.7.0-alpha.2` milestone adds the bounded LuaLS/legacy EmmyLua annotation syntax front end and
integrates immutable annotation results into `LuaCompiler` while keeping runtime IR annotation
free. Type/flow/module analysis, workspace and CLI scope are still open, so beta promotion is not
yet justified.

## Release procedure

1. Create a `release/<version>` branch when stabilization outside `main` is needed.
2. Set `VersionPrefix` and `VersionSuffix` and create `changelogs/<full-version>.md`.
3. Open a pull request; all protected CI checks must pass before squash merge.
4. From the accepted commit on `main`, create the immutable `v<full-version>` tag.
5. The release workflow validates all version sources, builds six RID bundles and
   symbol-enabled NuGet packages, publishes packages, and creates the GitHub release.
6. Versions containing a suffix are marked GitHub prereleases automatically; versions
   without a suffix are stable releases.
