# winget manifests

Manifests for publishing CreatioHelper Desktop to the [Windows Package Manager community repository](https://github.com/microsoft/winget-pkgs).

The manifests live in [`manifests/`](./manifests). They are submitted to `winget-pkgs` under:

```
manifests/f/fejdraus/CreatioHelper/<version>/
```

Keep the YAML files in their own folder — `winget validate` parses every file in the folder it is given, so a README next to them fails validation.

Package identifier: **`fejdraus.CreatioHelper`** — permanent, do not change it. Command alias after install: `creatio-helper`.

## Updating for a new release

Three values change on every release: `PackageVersion`, `InstallerUrl`, `InstallerSha256` (plus `ReleaseDate` and `ReleaseNotesUrl`).

Get the SHA256 without downloading the 120+ MB archive — GitHub publishes the digest:

```bash
gh api repos/fejdraus/CreatioHelper/releases/tags/desktop-v<version> \
  --jq '.assets[] | select(.name|test("win-x64")) | {name, digest}'
```

Then update:

| File | Fields |
|---|---|
| `fejdraus.CreatioHelper.yaml` | `PackageVersion` |
| `fejdraus.CreatioHelper.installer.yaml` | `PackageVersion`, `ReleaseDate`, `InstallerUrl`, `InstallerSha256` |
| `fejdraus.CreatioHelper.locale.en-US.yaml` | `PackageVersion`, `ReleaseNotesUrl` |

## Validating

```powershell
winget validate --manifest .\winget\manifests
```

Optional local install test (installs the app on your machine):

```powershell
winget install --manifest .\winget\manifests
```

## Submitting

1. Fork [`microsoft/winget-pkgs`](https://github.com/microsoft/winget-pkgs).
2. Copy the three YAML files to `manifests/f/fejdraus/CreatioHelper/<version>/`.
3. Commit on a branch and open a pull request.
4. The automated validation pipeline installs the package in a sandbox and reports back on the pull request.

[`wingetcreate`](https://github.com/microsoft/winget-create) can automate steps 1-3, including submission from CI after a release.

## Notes

The Desktop archive is a single-file self-contained publish, so the archive contains exactly one entry — `CreatioHelper.exe` at the root. That is why the manifest uses `InstallerType: zip` with `NestedInstallerType: portable`.

Only the Windows Desktop build is published to winget. The CLI and Agent are distributed through GitHub Releases only.
