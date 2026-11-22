# Download Bazaar

Status: DRAFT (2026-11-22). **There is no public release yet.** No signed installer exists, and no release has been published. The links below are where releases will appear. Before you install, read the [tester guide](guides/tester-onboarding.md).

## Get a release

All releases are on the GitHub Releases page:

**[github.com/codex-alchemist-dev/Bazaar/releases](https://github.com/codex-alchemist-dev/Bazaar/releases)**

If the page shows no releases, that is the current state. Releases are first created as drafts and checked by a person before they are published.

## What each package is

| Package | For | Where it comes from | Status |
|---|---|---|---|
| Server installer (MSI) | Windows. Runs the Bazaar server that does all the work. | `packaging/windows-msi` | Builds in CI. Not signed. |
| Windows client (MSIX, Chocolatey, Scoop, winget) | Windows. The app you use to talk to the server. | `packaging/windows-msix`, `packaging/chocolatey`, `packaging/scoop`, `packaging/winget` | Builds in CI. Not signed. |
| Linux client (AppImage, Debian, RPM, Flatpak, Snap) | Linux desktop. | `packaging/linux-*` | Builds in CI. Not signed. |
| Android client | Android phones and tablets. | `packaging/android` | Planned as a Google Play closed test. Not published. |

Nothing here installs a database for you. Bazaar includes its own.

## Check your download

Each release has a `SHA256SUMS` file. It lists a checksum for every file in the release. Check the file you downloaded against it before you run it.

On Linux or macOS:

```
sha256sum --check --ignore-missing SHA256SUMS
```

On Windows (PowerShell), compare the value shown by this command with the matching line in `SHA256SUMS`:

```
Get-FileHash .\the-file-you-downloaded.msi -Algorithm SHA256
```

If the checksum does not match, do not run the file. Report it (see the tester guide).

## Which version is which

- Versions look like `0.1.1`. While Bazaar is 0.x it is in **Beta**. Anything can change between versions.
- A version shown in the app may also carry the build commit, like `0.1.1+a1b2c3d`. The part after `+` only names the build.
- Release tags look like `v0.1.1`. A tag with `-beta.N` or `-rc.N` is a pre-release.
- The operator chooses the channel for each install: LTSC, Stable, Beta, Alpha or Latest-Commit. The full rules are in [VERSIONING.md](VERSIONING.md).

## Related

- [Tester onboarding](guides/tester-onboarding.md): what works today and what does not.
- [Release process](release.md): how a release is cut (for maintainers).
- [Roadmap](ROADMAP.md): what is planned and in what order.
