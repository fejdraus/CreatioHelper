## What this changes

<!-- Describe the change in a sentence or two. -->

## Why

<!-- The problem being solved, or a link to the related issue (e.g. Closes #12). -->

## Component

<!-- Mark what this touches. -->

- [ ] Desktop
- [ ] CLI
- [ ] Agent
- [ ] Shared code (Application / Domain / Infrastructure / Shared)
- [ ] Documentation only

## How it was tested

<!--
Describe what you actually ran. A green build is not proof for deployment behaviour —
if this touches compilation, package installation, IIS or Redis handling, please say
which Creatio version and environment you verified it on.
-->

## Checklist

- [ ] `dotnet build` completes with no errors and no warnings
- [ ] `dotnet test` passes
- [ ] Tests added or updated for behaviour changes
- [ ] Documentation updated if the change is user-visible (README / CHANGELOG)
- [ ] Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/)
