# Cutting a release

The release workflow (`.github/workflows/release.yml`) publishes installer packages that another workflow built. It creates a **draft** GitHub release. Nothing is published automatically, and a person publishes the draft. The version scheme and channels are in `docs/VERSIONING.md`.

## Before you start

- The commit to release is on `main` (squash-merged, CI green).
- `VersionPrefix` in `Directory.Build.props` already holds the version you are tagging.
- The installer workflow has finished successfully for that exact commit, and its run produced `installer-*` artifacts. Note its run id (the number in the run URL).

## Steps

1. Create an annotated tag on the `main` commit and push it:

   ```
   git tag -a v0.2.0 <commit> -m "Bazaar v0.2.0"
   git push origin v0.2.0
   ```

2. The push starts the Release workflow. If exactly one successful run for that commit has `installer-*` artifacts, the workflow uses it. If there are several, or none, the run fails with a message. Use step 3 instead.
3. To choose the source run yourself, start the workflow by hand from the Actions tab (workflow_dispatch) with:
   - `tag`: the tag, for example `v0.2.0`. It must already exist on origin.
   - `source_run_id`: the numeric run id of the installer run.
4. The **verify** job runs first (see the checks below). If it passes, the **publish** job waits at the `release` environment approval gate. An owner or reviewer approves it in the Actions tab.
5. After approval, the job creates a draft release for the tag and uploads the installers and `SHA256SUMS`.
6. Open the draft on GitHub, check the file list and `SHA256SUMS`, edit the notes if needed, and publish it. Do not mark it as latest by hand unless the channel calls for it.

## Checks the workflow performs

verify:
- The tag is a SemVer release tag (`v<MAJOR>.<MINOR>.<PATCH>` with an optional pre-release suffix). Any other form is refused.
- The tag exists and its commit is reachable from `origin/main` (`git merge-base --is-ancestor`). A tag on any other commit is refused.
- The source run is completed, successful, and was built from the tag's commit.
- The download has at least one file. Every file is a regular file (no directories or symlinks) and ends in `.msi`, `.exe`, `.zip`, `.tar.gz` or `.nupkg` (case-insensitive). Anything else stops the release.
- `SHA256SUMS` is written over all files and checked with `sha256sum --check --strict`.

publish:
- The `release` environment approval (repository setting, required reviewers).
- `SHA256SUMS` is checked again after download.
- The release is created with `--draft`, `--verify-tag` (the tag must exist) and `--latest=false`. It is a prerelease only when the tag has a suffix such as `-alpha.1`, `-beta.2` or `-rc.1`. This matches the channel rules in `docs/VERSIONING.md` section 4.

## Notes

- Tags are immutable. If a release is wrong, delete the draft, fix the cause, and cut a new PATCH or pre-release tag. Do not move a tag.
- A second run for the same tag fails if a release already exists for it. Delete the old draft first.
- Tag format: the workflow accepts SemVer tags such as `v0.2.0`. `docs/VERSIONING.md` section 1 describes a zero-padded display form (`v00.02.00`) for tags. That is an open question for the owner; until it is settled, this workflow rejects the padded form.
- The source installer run is not built here. Its workflow is separate and must produce `installer-*` artifacts.
- The `release` environment needs required reviewers set in repository settings. Without them the approval gate does nothing.
