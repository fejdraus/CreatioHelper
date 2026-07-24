# Contributing to CreatioHelper

Thanks for your interest in the project. Bug reports, feature requests and pull requests are all welcome.

## Ways to help

- **Report a bug** — open an issue using the *Bug report* template. Include your Creatio version, OS and CreatioHelper version; most deployment problems depend on them.
- **Request a feature** — open an issue using the *Feature request* template and describe the workflow you want automated.
- **Ask a question or share how you use the tool** — use [Discussions](https://github.com/fejdraus/CreatioHelper/discussions).
- **Send a pull request** — see below.

## Building the project

Requirements:

- [.NET SDK 10.0](https://dotnet.microsoft.com/download) or later
- Windows is required for the IIS-related features; the rest builds and runs on Linux and macOS

```bash
git clone https://github.com/fejdraus/CreatioHelper.git
cd CreatioHelper
dotnet build
dotnet test
```

Running a specific project:

```bash
dotnet run --project src/CreatioHelper.Desktop      # Desktop application (Avalonia)
dotnet run --project src/CreatioHelper.Cli -- --help # CLI
dotnet run --project src/CreatioHelper.Agent        # Agent service
```

## Repository layout

| Path | Purpose |
|---|---|
| `src/CreatioHelper.Desktop` | Avalonia desktop application |
| `src/CreatioHelper.Cli` | Headless command-line interface |
| `src/CreatioHelper.Agent` | Agent service (HTTP API) |
| `src/CreatioHelper.WebUI` | Web interface for the agent |
| `src/CreatioHelper.Application` | Use cases and orchestration |
| `src/CreatioHelper.Domain` | Entities and value objects |
| `src/CreatioHelper.Infrastructure` | WorkspaceConsole, IIS, Redis, sync implementations |
| `src/CreatioHelper.Shared` | Cross-cutting helpers and constants |
| `src/CreatioHelper.Contracts` | Shared DTOs between agent and clients |
| `tests/` | Unit tests (xUnit) |

Desktop, CLI and Agent are versioned and released independently.

## Coding conventions

- All repository content — code, comments, commit messages, documentation and release notes — is written in **English**.
- The codebase is kept **free of comments**; prefer clear names and small methods over explanatory comments.
- Always use braces `{ }` in `if` statements, including single-line bodies.
- Match the style of the surrounding code.

## Commit messages

The project follows [Conventional Commits](https://www.conventionalcommits.org/):

```
feat(compile): add Fast Compile mode gated on Creatio 8.0.10
fix(cli): support --key=value and stop losing option values
docs: document the restore command in README
refactor(core): resolve the Creatio site layout in one place
```

Common types: `feat`, `fix`, `docs`, `refactor`, `perf`, `build`, `ci`, `chore`.

## Pull requests

1. Fork the repository and create a branch from `main`.
2. Make your change, keeping it focused on a single concern.
3. Make sure `dotnet build` produces no errors or warnings and `dotnet test` passes.
4. Add or update tests when you change behaviour.
5. Open the pull request and describe what changes and why.

Changes that touch deployment behaviour (compilation chains, package installation, IIS handling) should ideally be verified against a real Creatio instance, since a green build does not prove correct behaviour there. Mention in the pull request what you tested and how.

## Reporting security issues

Please do not open a public issue for security problems. See [SECURITY.md](./SECURITY.md).

## License

By contributing you agree that your contributions are licensed under the [GNU GPL v3.0](./LICENSE), the same license as the project.
