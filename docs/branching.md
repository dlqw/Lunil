# Using Lunil from Git branches

This short Git tutorial describes how an application can evaluate Lunil source from a branch without confusing source identity and package identity.

## Pin an exact source identity

Use a commit SHA when a build must be reproducible. A branch name is a moving reference and is suitable for exploration, not for recording a dependency input. Keep the selected SHA with the application's own dependency metadata.

```text
git clone https://github.com/dlqw/Lunil.git
cd Lunil
git checkout <commit-sha>
```

## Separate application work

Keep application integration changes on an application branch. Update the Lunil source identity in one focused change so a regression can be compared with the prior identity. Do not infer package compatibility solely from a branch name; inspect the package version and public API documentation.

## Verify the input

Record the repository URL, commit SHA, .NET SDK, and package or project-reference mode used by the application. Build the application from a clean checkout to confirm that its selected source identity is sufficient.

## Prefer released packages for distribution

For a distributed application, reference an immutable published package version. Source references are most useful when evaluating an unreleased source snapshot or debugging a dependency interaction.
