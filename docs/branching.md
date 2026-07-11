# Branch management

Lunil uses a protected, linear `main` branch. Work is performed on short-lived
branches and merged by pull request only after all required checks succeed.

## Branch names

- `feature/<topic>` for new capabilities and breaking changes;
- `fix/<topic>` for bug fixes;
- `docs/<topic>` for documentation-only work;
- `release/<version>` for release preparation when a stabilization branch is needed.

Branches should be rebased on `main` before review. Pull requests are squash-merged,
must resolve review conversations, and must pass the Windows, Linux, macOS, formatting,
and package checks. Force pushes and direct pushes to `main` are disabled.

## Releases

Release tags are created from `main` only. A tag must use `v<SemVer>` (for example,
`v0.5.0-alpha.1`), match `VersionPrefix` plus the optional `VersionSuffix` in
`Directory.Build.props`, and have a matching full-version changelog such as
`changelogs/0.5.0-alpha.1.md`. The release workflow rejects inconsistent versions
before building or publishing anything.

The complete version and prerelease promotion policy is documented in
[`versioning.md`](versioning.md).
