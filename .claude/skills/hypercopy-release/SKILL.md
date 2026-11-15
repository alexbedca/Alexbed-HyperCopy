---
name: hypercopy-release
description: Release a new version of Alexbed HyperCopy - version bump, changelog, tests, portable exe build, screenshots, GitHub release and verification. Use when asked to release, ship, publish or tag a new HyperCopy version.
---

# Releasing Alexbed HyperCopy

Repo: https://github.com/alexbedca/Alexbed-HyperCopy (branch `main`). For general publishing conventions shared by all
Alexbed apps, see the personal `alexbed-github-publish` skill. This file covers only what is specific to HyperCopy.
Confirm with Alexander before pushing, tagging or publishing the release.

## 1. Prepare
1. `git status`: the working tree must be clean except for the intended changes. `git pull`.
2. Pick the version (semver). Update:
   - `Directory.Build.props` → `<Version>X.Y.Z</Version>` (the window title chip reads it automatically).
   - `README.md` → the `**Version:** X.Y.Z` line, and the benchmark table if numbers changed.
   - `CHANGELOG.md` → a new `## X.Y.Z (YYYY-MM-DD)` section at the top.
   - `ROADMAP.md` → move finished items out.
3. If the engine changed speed, re-run the `hypercopy-benchmark` skill and update `docs/benchmark-results.md` first.

## 2. Build and test EVERY function (mandatory: no release without it)
Alexander's rule after the v1.0.2 bug (a folder copied into its own parent failed every file, retried for a
minute and showed a tick on the checksum badge): **test every function and every UI state on the built exe before
any release, and show him the results.** Unit tests passing is not enough.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
pwsh tools/release-check.ps1 -Ssd C:\hc-bench\rc -Ssd2 E:\hc-bench\rc -Hdd G:\hc-bench\rc -Usb H:\hc-bench\rc
```
- `build.ps1` runs all unit tests (must be 0 failed) and writes `dist\AlexbedHyperCopy.exe` (~141 MB, uncompressed for
  fast start), `dist\hypercopy.exe` (~70 MB) and `dist\SHA256SUMS.txt`.
- `release-check.ps1` drives the real `hypercopy.exe` through every option on real drives, checks every byte,
  timestamp and attribute, and must end with **0 failed**. Leave out `-Usb` if no stick is plugged in; it then
  reports USB as NOT TESTED, and the release notes must say so. (Use folders below a drive root: a guard blocks
  deleting `X:\folder`.)
- **UI states:** use the snapshot hook to capture every state and look at each image: welcome, destination
  warning (source folder → its own parent), copying in progress, finished + Checksums PASS, re-run all skipped
  ("nothing to check" with an info icon, not a tick), some files locked (PASS for the rest + Problems list),
  every file locked ("Nothing was copied", "Checksums · not run"), move on the same drive. Hooks:
  `HYPERCOPY_SNAPSHOT=<png>`, `HYPERCOPY_SNAPSHOT_DELAY=<ms>`, `HYPERCOPY_AUTOSTART=1`, `HYPERCOPY_VERIFY=1`,
  `HYPERCOPY_MODE=Move`, `HYPERCOPY_CONFLICT=Overwrite|Skip|KeepBoth|SkipIdentical`, args `<src>... --to <dest>`.
  Lock a file with `[IO.File]::Open(path,'Open','Read','None')` to test failures. Every number must be true.
- Start-up time: median of 5 starts, compared with `docs/benchmark-results.md`.
- Report to Alexander: a table of every check with PASS/FAIL/NOT TESTED, before asking to publish.

## 3. Screenshots and social card (EVERY release, not only when the UI changed)
Every screenshot shows the version chip (`v1.0.x`) in the title bar, so they go stale with **every** release.
Alexander had to ask after 1.0.3 shipped with 1.0.0/1.0.2 images. Do this **before** pushing and publishing:
regenerate all four `docs/screenshots/{welcome,setup,copying,complete}.png` from the new `dist\` exe, look at each
one (correct version, no overlapping or cut-off text, every number true), then rebuild and upload the social card.
Demo data: `C:\Users\Public\hc-demo` ("Photos 2026" folder of ~400 files plus a 6 GB "Holiday 2026.mp4"),
copying shot ~12 s into the 6 GB file with Verify on, complete shot = Photos 2026 with Verify on (60 s delay).
Snapshot details are in the `hypercopy-benchmark` skill. Then the social card:
`python -I tools/make_social_preview.py assets/logo.png docs/screenshots/complete.png docs/social-preview.png`
(since 1.0.2 the card uses the verified "complete" shot with the Checksums PASS badge).
Look at every image. Upload a new social card in repo Settings → Social preview (Chrome; there's no API).

## 4. Publish
1. Commit with a message file: `git commit -F <file>`, ending with the Co-Authored-By line.
2. `git push` (plain git. Git Credential Manager has the workflow scope; gh doesn't).
3. Write release notes: screenshot (link it by commit, not `main`:
   `https://raw.githubusercontent.com/alexbedca/Alexbed-HyperCopy/<commit>/docs/screenshots/complete.png`,
   so older releases keep their own image when screenshots are replaced), download table (AlexbedHyperCopy.exe / hypercopy.exe / SHA256SUMS.txt),
   highlights, honest known limitations. Then:
   ```powershell
   gh release create vX.Y.Z dist\AlexbedHyperCopy.exe dist\hypercopy.exe dist\SHA256SUMS.txt `
     --repo alexbedca/Alexbed-HyperCopy --target main --title "Alexbed HyperCopy X.Y.Z" --notes-file <notes> --latest
   ```

## 5. Verify
- Checklist before saying "released": tests 0 failed · release-check 0 failed · UI states checked ·
  **4 screenshots + social card regenerated with the new version and uploaded** · release notes name anything
  NOT TESTED · download hash matches · CI green.
- `gh release download vX.Y.Z -p AlexbedHyperCopy.exe -D <scratch>` and compare its SHA-256 with `SHA256SUMS.txt`.
- `gh run list --limit 1`, then `gh run watch <id> --exit-status`: CI must be green. Fix any annotations in a follow-up.
- Open the release page and the README to check that images render.
