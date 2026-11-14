---
name: hypercopy-benchmark
description: Measure Alexbed HyperCopy's speed fairly against Robocopy and Explorer's copy engine, test tuning changes, and capture app screenshots with the snapshot hook. Use when asked how fast HyperCopy is, to compare with other copiers, to tune the engine, or to update screenshots.
---

# Benchmarking and screenshots for HyperCopy

## Test machine (Alexander's PC)
| Drive | Hardware | Notes |
|---|---|---|
| C: | Crucial MX500 1 TB SATA SSD | Reliable. Best for SSD numbers. |
| E: | 240 GB DRAM-less SATA SSD | Drops to ~80 MB/s once its cache fills, and recovers after idle. Never trust a single run. |
| D:, G: | 7200 rpm HDDs | D: holds the repo. G: is the usual HDD target. |
| M: | Network drive | |
32 GB RAM, 4 logical cores. Test data: `C:\hc-bench\large\big-36.bin` (36 GB), `C:\hc-bench\src\video-6gb.bin`,
`C:\hc-bench\small` (20,000 files, 1–64 KB). Use nested destinations such as `E:\hc-bench\x`: a guard blocks deleting
folders directly under a drive root.

## Fairness rules (each one changed a result once)
1. **RAM cache:** a source file read recently is served from RAM. Use files larger than RAM, or exclude runs that
   are suspiciously fast (one CopyFileEx run "did" 391 MB/s on a 6 GB file because it was cached).
2. **Flush:** buffered tools (Robocopy, CopyFileEx) can finish before data reaches the disk. Time them *with*
   a flush: copy, then `[IO.File]::Open(dst,'Open','ReadWrite').Flush($true)`. HyperCopy's unbuffered writes are already on disk.
3. **Order:** a drive tired from the previous run penalises the next tool. Alternate the order and repeat each test ≥ 2×.
4. **Same build:** benchmark `src\Alexbed.HyperCopy.Cli\bin\Release\net10.0-windows\hypercopy.exe` from `dotnet build -c Release`.
   Don't rebuild while a benchmark is running.
5. Report losses as well as wins, with numbers, in `docs/benchmark-results.md` and the README table.

## How to run
- Full suite (~40 min with a 36 GB file): `pwsh tools/benchmark.ps1` → writes `docs/benchmark-results.md` (then add
  the methodology notes back in by hand).
- Quick A/B of one setting: CLI flags `--write-depth N`, `--buffer-mb N`, `--threads N`, `--profile ssd|hdd`, `--buffered`.
  The CLI prints scan time and tuning description.
- In PowerShell 7, don't name helper functions `Clean` (reserved word) or `R` (alias for Invoke-History).

## Known results (v1.0, keep unless re-measured)
- Large files: C:→C: 25.3 s vs CopyFileEx 29.7 s vs Robocopy /J 30.7 s (6 GB). HDD: parity with CopyFileEx+flush.
- Write depth: HDD 1 writer = 69 s, 2 = 79 s, 4 = 86 s, 8 = 107 s. SSD 2 writers slightly better than 1.
- Small files: CopyFileEx is 4–8× faster than handle-based copying. HyperCopy ~10.6 s vs Robocopy /MT:16 ~5 s (20k files).
  Thread count 8/16/32, dedicated threads, 4 processes, pwsh (MS-signed), folder ordering, and CopyFile2 without
  pre-allocation (much worse, 47 s) were all tried; none closed the gap.

## Screenshots (snapshot hook)
```powershell
$env:HYPERCOPY_SNAPSHOT = "docs\screenshots\copying.png"   # render window to PNG, then exit
$env:HYPERCOPY_AUTOSTART = "1"                             # start the job given on the command line
$env:HYPERCOPY_SNAPSHOT_DELAY = "12000"                    # ms before the capture
Start-Process dist\AlexbedHyperCopy.exe -ArgumentList 'C:\hc-bench\src\video-6gb.bin','--to','C:\hc-bench\shot' -Wait
```
The capture cancels any running job, so for a "complete" shot use a delay longer than the job. Remove the env vars
afterwards. Standard set: welcome (no args), setup (args, no autostart), copying (~12 s into the 6 GB file), complete
(small folder + 6 GB file, 45 s).
