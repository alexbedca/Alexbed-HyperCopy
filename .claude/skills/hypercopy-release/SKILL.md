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

## 2. Build and test
```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```
It runs all tests (must be 0 failed), then writes `dist\AlexbedHyperCopy.exe` (~141 MB, self-contained, uncompressed for fast start),
`dist\hypercopy.exe` (~70 MB) and `dist\SHA256SUMS.txt`.

Smoke test the real exe from a separate folder (portable check): copy it to `%TEMP%\hc-portable`, run a job with
`HYPERCOPY_AUTOSTART=1` and a long `HYPERCOPY_SNAPSHOT_DELAY`, then hash-compare source and destination trees.

## 3. Screenshots (if the UI changed)
Use the snapshot hook (details in the `hypercopy-benchmark` skill) to regenerate `docs/screenshots/{welcome,setup,copying,complete}.png`,
then the social card:
`python -I tools/make_social_preview.py assets/logo.png docs/screenshots/complete.png docs/social-preview.png`
(since 1.0.2 the card uses the verified "complete" shot with the Checksums PASS badge).
Look at every image. Upload a new social card in repo Settings → Social preview (Chrome; there's no API).

## 4. Publish
1. Commit with a message file: `git commit -F <file>`, ending with the Co-Authored-By line.
2. `git push` (plain git. Git Credential Manager has the workflow scope; gh doesn't).
3. Write release notes: screenshot, download table (AlexbedHyperCopy.exe / hypercopy.exe / SHA256SUMS.txt),
   highlights, honest known limitations. Then:
   ```powershell
   gh release create vX.Y.Z dist\AlexbedHyperCopy.exe dist\hypercopy.exe dist\SHA256SUMS.txt `
     --repo alexbedca/Alexbed-HyperCopy --target main --title "Alexbed HyperCopy X.Y.Z" --notes-file <notes> --latest
   ```

## 5. Verify
- `gh release download vX.Y.Z -p AlexbedHyperCopy.exe -D <scratch>` and compare its SHA-256 with `SHA256SUMS.txt`.
- `gh run list --limit 1`, then `gh run watch <id> --exit-status`: CI must be green. Fix any annotations in a follow-up.
- Open the release page and the README to check that images render.
