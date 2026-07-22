# Version and compatibility identifiers

Lunil uses [Semantic Versioning 2.0.0](https://semver.org/) identifiers for packages and public compatibility communication. A version has the form `MAJOR.MINOR.PATCH` with an optional prerelease suffix, for example `0.11.0-alpha.1`.

## Reading a version

Before `1.0.0`, a minor version can introduce a breaking public API or behavioral change. Patch versions describe backward-compatible corrections within an existing line. A prerelease suffix identifies a prerelease package; applications should test it against their own compatibility requirements before adopting it.

A published package version and tag are immutable identities. Do not assume that two artifacts with different versions have interchangeable public APIs, runtime ABI formats, profile formats, or language-adapter behavior.

## Related compatibility identities

Package version is only one boundary. Lunil also validates:

- selected `LuaLanguageVersion` for source, chunk, state, and library behavior;
- canonical IR identity for compiled modules;
- runtime ABI identity for generated artifacts;
- module/function content identity and generation for caches and direct calls.

These identifiers prevent a host from loading a module, profile, or generated entry under a contract that it was not created for.

## Upgrading an application

Read the changelog and applicable migration guide for the exact package version, then build and test the application with its chosen Lua versions and host configuration. For applications that persist chunks, profiles, or module metadata, include their compatibility identities in the application's own storage format and reject incompatible data explicitly.
