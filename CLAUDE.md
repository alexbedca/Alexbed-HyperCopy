# Alexbed HyperCopy: project notes for Claude

## What this project is
A free, portable Windows 10/11 app that copies and moves files (up to terabytes) as fast as the drives allow:
choose sources and a destination folder or USB drive, pick **Copy** or **Move**, and watch live speed/ETA. It's one
of Alexander's "Alexbed" apps, built from scratch with Claude in Nov 2026.

- **Status:** v1.0.4 released 2026-11-15 (badge overlap fix; all 22 release checks incl. USB passed); v1.0.3 (same-folder warning, honest result screen, ~15% faster small files); v1.0.2 (uncompressed exes, faster start); v1.0.1 (checksum PASS/FAIL badge); v1.0.0 released 2026-11-13. Public repo https://github.com/alexbedca/Alexbed-HyperCopy
  (GPL-3.0-or-later), release with `AlexbedHyperCopy.exe` + `hypercopy.exe` (CLI) + `SHA256SUMS.txt`, CI green,
  social preview set.
- **Next (v1.1):** close the small-file gap with Robocopy `/MT` on SSD (~2× slower today). See `ROADMAP.md`.
- **Read first:** `README.md` (user view), `docs/ARCHITECTURE.md` (engine), `docs/benchmark-results.md` (measured facts),
  `docs/RESEARCH.md` (competitors and lessons).

## Stack and layout
C# / .NET 10 (`net10.0-windows`), WPF UI, xUnit tests, System.IO.Hashing (XXH3) as the only package.
```
src/Alexbed.HyperCopy.Core   engine: CopyEngine, PipelinedFileCopier (FastFileIo.cs), DriveProbe, options/progress
src/Alexbed.HyperCopy.App    WPF window: MainWindow.xaml(.cs), MainViewModel.cs, theme in App.xaml
src/Alexbed.HyperCopy.Cli    hypercopy.exe
tests/Alexbed.HyperCopy.Tests  52 tests
tools/                       benchmark.ps1, make_logo.py, make_social_preview.py
build.ps1                    tests + portable exes + checksums (also used by CI)
```

## Tools for this project
**Project commands**
- `dotnet test -c Release`: 52 tests, ~20 s. Every engine change needs a test.
- `powershell -ExecutionPolicy Bypass -File build.ps1` → `dist\` (self-contained single-file exes, ~141 MB + ~70 MB).
- `dotnet run --project src/Alexbed.HyperCopy.Cli -- copy <src>... <dst> [--verify --profile ssd|hdd --write-depth N --buffer-mb N --threads N]`.
- Snapshot hook: `HYPERCOPY_SNAPSHOT=<png>` (+ `HYPERCOPY_AUTOSTART=1`, `HYPERCOPY_VERIFY=1`, `HYPERCOPY_SNAPSHOT_DELAY=<ms>`) renders
  the window to PNG and exits. Used for all screenshots and for UI checks without driving the desktop.
- `python -I tools/make_logo.py <dir>` regenerates logo PNG/ICO. `tools/make_social_preview.py` regenerates the 1280×640 card.

**Project skills** (in `.claude/skills/`)
- `hypercopy-release`: version bump → build → screenshots → GitHub release → verification.
- `hypercopy-benchmark`: fair benchmarking rules, drive notes, known results, screenshot recipe.

**Account tools this project uses** (full list in `~/.claude/CLAUDE.md`)
- `git push` via Git Credential Manager (the `gh` token lacks `workflow` scope; don't use `gh repo create --push`).
- `gh` for releases, CI (`gh run watch`), repo settings and topics.
- Claude in Chrome for the GitHub social-preview upload (web-only).
- Personal skill `alexbed-github-publish` for repo-wide conventions.

## Release rule (from Alexander, 2026-11-15)
**Test every function and every UI state on the built exe before any release**, and show him the results table.
v1.0.2 shipped a bug only the real app showed (folder copied into its own parent: 74 failed files after 56 s of
retries, a ticked "Checksums" badge, "213 MB verified"). Use `tools/release-check.ps1` (real drives, every option)
plus the snapshot-hook UI states listed in the `hypercopy-release` skill and CONTRIBUTING.md. Untested drives
(e.g. USB when no stick is plugged in) must be named as NOT TESTED in the release notes.
**Every release also regenerates all 4 README screenshots and the social card** (they show the version number),
checks them, and uploads the card in repo Settings, before publishing. 1.0.3 first shipped with old images.

## Decisions backed by measurements (don't undo without re-measuring)
- Small files (< 4 MB) use CopyFileEx (`File.Copy`) + restore creation time: 4–8× faster than handle-based copying.
- Large files: unbuffered, sector-aligned pipeline; HDD/USB = 1 writer (concurrent writes cost 12–35%), SSD = 2.
- Single-file exes are **not compressed**: compression cost ~0.45 s per app start (1.73 → 1.27 s) and ~56 ms per
  CLI start. An empty WPF window takes ~0.88 s on this PC, so ~0.35 s is HyperCopy's own window.
- `InvariantGlobalization` crashes WPF bindings at startup; it's only set on the CLI.
- Tests must not block on `.Result` (xUnit1031); use `async Task` + `await`.

## Gotchas
- PowerShell 7: `clean` is reserved, and `R` is an alias for Invoke-History. Don't use them as function names.
- A guard blocks deleting folders directly under a drive root (`E:\x`); use nested test paths (`E:\hc-bench\x`).
- `git commit -F -` with a PowerShell here-string failed; write the message to a file first.
- Benchmark data (`C:\hc-bench`, ~42 GB) was deleted on 2026-11-14. `tools/benchmark.ps1` recreates it when needed;
  delete it again afterwards.
- The ALMP and Bulk Rename social-card working files moved out of this folder on 2026-11-14 to
  `D:\Home\Desktop\app_dev\social-cards` (they aren't HyperCopy files).
