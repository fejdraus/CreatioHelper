# Security Policy

## Supported versions

Security fixes are applied to the latest release. Please make sure you are running the most recent version before reporting a problem.

Releases are published per component:

- Desktop — `desktop-v*`
- CLI — `cli-v*`
- Agent — `agent-v*`

## Reporting a vulnerability

**Please do not report security issues through public GitHub issues or discussions.**

Instead, use GitHub's private reporting:

1. Open the [Security advisories](https://github.com/fejdraus/CreatioHelper/security/advisories) page.
2. Click **Report a vulnerability**.

If private reporting is unavailable to you, email **fejdraus@gmail.com** with the subject line `CreatioHelper security`.

Please include:

- A description of the issue and why you consider it a security problem
- Steps to reproduce, or a proof of concept
- Affected component (Desktop, CLI, Agent) and version
- Any suggested mitigation you are aware of

## What to expect

- An acknowledgement of your report within a few days.
- An assessment of the issue and, if confirmed, a fix in an upcoming release.
- Credit in the release notes, unless you prefer to stay anonymous.

## Scope notes

CreatioHelper handles credentials for Creatio databases, Redis, SSH/SFTP targets and the agent API. Reports involving how these are stored, transmitted or logged are especially welcome.

Configuration files that the user themselves places on disk (for example `settings.json` containing their own connection strings) are not considered a vulnerability in the tool by default — but weaknesses in how the application protects, transmits or exposes that data are.
