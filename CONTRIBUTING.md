# Contributing to Alexbed HyperCopy

Thank you for helping! HyperCopy is a small, free project: a file copier that is fast, safe and simple.

## Ways to help
- **Report a bug.** Open an issue and say what you did, what you expected and what happened. A screenshot
  and the *Copy list* output of the Problems panel help a lot.
- **Share benchmark numbers** from your hardware (NVMe, USB 3 sticks, NAS, SD cards).
  `tools/benchmark.ps1` runs the standard set.
- **Suggest an idea.** Open an issue and explain the problem you want solved.
- **Send code** (see below).

## Sending code
1. Fork the repository and create a branch.
2. Install the [.NET 10 SDK](https://dotnet.microsoft.com/download) on Windows.
3. Keep changes small and focused. For big changes, please open an issue first.
4. Run `dotnet test`. Every change to the engine needs a test in `tests/Alexbed.HyperCopy.Tests`.
5. For speed changes, measure before and after with `tools/benchmark.ps1` (or the CLI) on real drives,
   and include the numbers in the pull request. Watch for source files served from the RAM cache, and for
   buffered tools that finish before data reaches the disk.
6. Open a pull request and describe what you changed and why.

## Before every release: test every function (mandatory)
Unit tests alone are not enough. v1.0.2 shipped with a bug that only showed up in the real app: copying a folder
into its own parent ran for a minute, failed every file and still showed a misleading checksum badge. So **no
release goes out until all of these pass on the built exes in `dist\`**:

1. `dotnet test -c Release`: 0 failed.
2. `pwsh tools/release-check.ps1 -Ssd C:\hc-bench\rc -Ssd2 E:\hc-bench\rc -Hdd G:\hc-bench\rc [-Usb H:\hc-bench\rc]`:
   0 failed. It runs the real `hypercopy.exe` through every option (copy and move, all four "if a file exists"
   choices, verify, forced profiles, buffered, locked files, missing source, a folder into its own parent,
   destination inside source) on real drives, and checks every byte, timestamp and attribute.
   Any drive it could not test is listed as **NOT TESTED**; say so in the release notes.
3. **Look at the app window in every state** with the snapshot hook (`HYPERCOPY_SNAPSHOT`, plus
   `HYPERCOPY_AUTOSTART`, `HYPERCOPY_VERIFY=1`, `HYPERCOPY_MODE=Move`, `HYPERCOPY_CONFLICT=Overwrite|Skip|KeepBoth`):
   welcome, a destination warning, copying in progress, finished with PASS, re-run with everything skipped,
   some files failed, every file failed, and a move. Every number and badge on screen must be true:
   no tick when nothing was checked, no "verified" bytes that were never read back, and no "complete" when
   something went wrong.
4. Start-up time: open the exe 5 times and compare the median with `docs/benchmark-results.md`.

## Design rules
- **Data safety beats speed.** Never leave a partial file, never delete a source that wasn't copied.
- Simple and readable for non-technical people.
- No installer, no registry writes, no internet access, ads, tracking or accounts.
- The window must never freeze: I/O belongs off the UI thread.
- Keep logic in `Alexbed.HyperCopy.Core` (no UI) so it can be tested.

## Updating screenshots
`$env:HYPERCOPY_SNAPSHOT="docs\screenshots\name.png"` (optionally with `HYPERCOPY_AUTOSTART=1` and
`HYPERCOPY_SNAPSHOT_DELAY=<ms>`) makes the app render its window to a PNG and exit.

## Licence
By contributing, you agree that your work is released under the project licence,
**GPL-3.0-or-later** (see [LICENSE](LICENSE)).
