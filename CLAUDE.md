# Alexbed HyperCopy: notes for Claude

Portable Windows file copier/mover. C# / .NET 10, WPF. Public repo: https://github.com/alexbedca/Alexbed-HyperCopy
(GPL-3.0-or-later). Design and engine details: `docs/ARCHITECTURE.md`. Measured results: `docs/benchmark-results.md`.

## Build, test, release
- Tests: `dotnet test -c Release` (35 tests, ~15 s). Every engine change needs a test.
- Release build: `powershell -ExecutionPolicy Bypass -File build.ps1` → `dist\AlexbedHyperCopy.exe`,
  `dist\hypercopy.exe`, `dist\SHA256SUMS.txt`. CI (`.github/workflows/build.yml`) runs the same script on push.
- Release: bump `<Version>` in `Directory.Build.props`, update `CHANGELOG.md` and the README "Version" line,
  run `build.ps1`, commit, `git push`, then
  `gh release create vX.Y.Z dist\* --title "Alexbed HyperCopy X.Y.Z" --notes-file <notes> --latest`.
  Download the published exe again and check its SHA-256 against `SHA256SUMS.txt`.
- Push with plain `git push` (Git Credential Manager has the `workflow` scope). The `gh` token lacks it, so
  `gh repo create --push` or anything through gh's git helper is rejected when workflow files change.

## Screenshots
`$env:HYPERCOPY_SNAPSHOT="<png>"` renders the window to a PNG and exits. Add `HYPERCOPY_AUTOSTART=1`
(start the job passed on the command line) and `HYPERCOPY_SNAPSHOT_DELAY=<ms>`. Pass sources and `--to <dest>`
as arguments. The snapshot cancels a running job, so make the delay long enough for a "complete" shot.
Save to `docs/screenshots/`. Regenerate the social card with `python -I tools/make_social_preview.py
assets/logo.png docs/screenshots/copying.png docs/social-preview.png` and upload it in repo Settings → Social preview
(there is no API for that step).

## Benchmarking: rules that bit us
- Drives here: C: MX500 SATA SSD (reliable), E: DRAM-less SSD (drops to ~80 MB/s once its cache fills;
  don't trust single runs), D:/G: HDDs, M: network.
- Use source files larger than RAM (32 GB), or note when a tool read from the RAM cache.
- Time buffered tools (Robocopy, CopyFileEx) **including a flush to disk**, otherwise they look faster.
- Alternate run order; a drive tired from the previous run penalises whoever goes next.
- Publish losses too (small files vs Robocopy /MT). Don't claim "fastest" without numbers.
- Test data lives in `C:\hc-bench` (+ `E:\hc-bench`, `G:\hc-bench`). Deleting drive-root folders is
  blocked by a guard, so use nested paths.

## Engine decisions backed by measurements (don't undo without re-measuring)
- Small files (< 4 MB) use CopyFileEx (`File.Copy`) + restore creation time: 4–8× faster than handle-based copying.
- HDD/USB uses WriteDepth 1. Concurrent writes cost 12–35% on HDD. SSD uses 2.
- `InvariantGlobalization` breaks WPF bindings (crash at startup); it's only set on the CLI.
- PowerShell 7 reserves `clean` (don't name a function `Clean`), and `R` is an alias for Invoke-History.

## Open work
v1.1 goal: close the small-file gap with Robocopy /MT on SSD (we're ~2× slower; see ROADMAP.md). Already
ruled out: thread-pool starvation, per-process limits, code signing, folder ordering, CopyFile2 flags.
