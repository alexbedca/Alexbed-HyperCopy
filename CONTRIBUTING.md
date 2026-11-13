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
