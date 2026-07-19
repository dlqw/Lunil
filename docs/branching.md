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

## Review and test exemptions

The repository keeps automatic code-owner review focused on product and release risk. The
CODEOWNERS file covers source, tests, benchmarks, scripts, workflows, build/version inputs,
public API baselines, and release metadata. Community Markdown, changelogs, generated
performance charts, and benchmark result reports do not generate a code-owner request.

CI still runs the public-repository, community-documentation, and generated-chart checks for
those documentation-only changes, but skips the six-platform restore/build/test, publish, package,
and backend-evidence steps. Any source, test, benchmark harness, script, workflow, version,
API-baseline, or release-configuration change makes the run full again. Manual dispatches and
release-tag workflows always run their complete required gates; an exemption cannot weaken a
release.

## Releases

Release tags are created from `main` only. A tag must use `v<SemVer>` (for example,
`v0.5.0-alpha.1`), match `VersionPrefix` plus the optional `VersionSuffix` in
`Directory.Build.props`, and have a matching full-version changelog such as
`changelogs/0.5.0-alpha.1.md`. The release workflow rejects inconsistent versions
before building or publishing anything.

The complete version and prerelease promotion policy is documented in
[`versioning.md`](versioning.md).
