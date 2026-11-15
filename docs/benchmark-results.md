# Benchmark results — Alexbed HyperCopy v1.0

Test machine: Windows 11 Pro, 4 logical cores, 32 GB RAM.
Drives: **C:** Crucial MX500 1 TB SATA SSD (DRAM cache) · **E:** 240 GB DRAM-less SATA SSD ·
**G:** WD Blue 1 TB 7200 rpm HDD. Windows Defender real-time protection on.

Methodology notes:
- Test files are random (incompressible) data.
- "CopyFileEx" is the Windows copy engine behind `File.Copy` and Explorer.
- Buffered tools can report "done" while data is still in RAM. Where noted, their time **includes a
  flush to disk**, so every tool is measured to the point where the data is actually on the drive.
  HyperCopy's unbuffered writes are already on the drive when it reports completion.
- Runs where a tool read the source from the RAM cache (a warm-cache artefact) are excluded.

## Large files

| Test | HyperCopy | Robocopy `/J` | Robocopy | CopyFileEx (Explorer) |
|---|---:|---:|---:|---:|
| 6 GB, same SSD (C:→C:), steady state, 2 rounds | **25.3 – 25.9 s** (≈240 MB/s) | 30.7 – 31.3 s | – | 29.7 s (+flush) |
| 36 GB, SSD→SSD (C:→E:), larger than RAM | **459 s** | 501 s | 483 s | 491 s |
| 6 GB, SSD→HDD (C:→G:) | **69.4 s** | – | 66.8 s (no flush) | 69.7 s (+flush) |

- **SSD:** HyperCopy is 15–20% faster than Robocopy and Explorer.
- **36 GB to E:** every tool is capped at about 80 MB/s, the sustained write speed of a DRAM-less SSD
  once its SLC cache is full. HyperCopy is still first.
- **HDD:** on par with the Windows engine once both are measured to the point the data is on disk.

### Tuning finding: write queue depth

| Destination | 1 write in flight | 2 | 4 | 8 |
|---|---:|---:|---:|---:|
| HDD (G:) | **69.4 s** | 78.7 s | 86.2 s | 106.5 s |
| SSD (C:) | 25.9 s | **25.3 s** | – | – |

Concurrent writes make HDD heads seek, so the HDD/USB profile uses a single in-order writer and
the SSD profile uses two.

## Many small files (20,000 files, 1–64 KB, 632 MB)

| Test | HyperCopy v1.0 | HyperCopy (first build) | Robocopy `/MT:16` | Robocopy (1 thread) |
|---|---:|---:|---:|---:|
| SSD→SSD (C:→E:) | 10.6 – 11.7 s | 51 – 92 s | **4.7 – 5.2 s** | 17.6 – 19.2 s |
| SSD→HDD (C:→G:) | 30.7 s | 47.4 s | **28.3 – 28.8 s** | – |

What we found:
- The first build copied small files by opening handles itself. Switching to the kernel's CopyFileEx
  made it **4–8× faster**.
- Single-threaded, HyperCopy's small-file copy beats Robocopy's (15.3 s vs 17.6 s).
- Robocopy `/MT` scales better across threads (3.4× vs 1.5×). We ruled out thread-pool starvation,
  per-process limits, code signing, folder ordering and CopyFile2 flags. The remaining limit appears
  to be system-wide, likely antivirus scanning of newly created files, and needs admin-level tracing
  to pin down.
- **Open item for v1.1:** close the small-file gap to Robocopy `/MT` on SSDs.

## USB stick (v1.0.4)

Transcend JetFlash 16 GB, exFAT. Source: an app folder on a hard disk (74 files, 213 MB; 9 files of 4 MB+ hold
191 MB), read from the RAM cache by every tool. Removable drives use Windows' "quick removal" (no write cache), so
buffered tools also finish only when the data is on the stick.

| Tool | Time | Speed |
|---|---:|---:|
| **HyperCopy (auto: 1 stream)** | **33.7 – 36.3 s** | **≈6 MB/s** |
| Robocopy (1 thread) | 41.8 s | 5.1 MB/s |
| Windows copy engine (`Copy-Item`) | 49.1 s | 4.3 MB/s |
| HyperCopy forced to SSD profile (parallel) | 53.8 s | 4.0 MB/s |
| Robocopy `/MT:8` | 94.2 s | 2.3 MB/s |
| HyperCopy (auto) + verify | 42.4 s | 5.0 MB/s |

The stick itself is the limit (about 6 MB/s sustained writes). Parallel writes make cheap sticks much slower,
which is why HyperCopy uses one sequential stream for USB. One run each; single stick.

## Start-up time (v1.0.2)

Time from launching the exe until the window is visible and idle, median of 5 warm starts (4-core PC, SATA SSD):

| Build | Start-up | Exe size |
|---|---|---|
| v1.0.1, compressed single file | 1.73 s | 64 MB |
| **v1.0.2, uncompressed single file** | **1.27 s** | 141 MB |
| Uncompressed, folder of files (not single file) | 1.45 s | many files |
| Empty WPF window (lowest possible for WPF) | 0.88 s | 148 MB |

The CLI starts in 143 ms uncompressed versus 199 ms compressed. The first start after a reboot is slower for all builds.

## Reproduce

```
pwsh tools/benchmark.ps1 -SrcRoot C:\hc-bench -SsdDest E:\hc-bench -HddDest G:\hc-bench
```
