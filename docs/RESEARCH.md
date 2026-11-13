# Research and design plan

> The original plan for Alexbed HyperCopy: what the fastest copiers do, and what we set out to build.
> What actually shipped is in [ARCHITECTURE.md](ARCHITECTURE.md). Measured results are in [benchmark-results.md](benchmark-results.md).

## 1. What the fastest copiers in the world do (research summary)

| Tool | Key speed techniques | Weakness we avoid |
|---|---|---|
| **FastCopy** (Japan, the long-time speed champion) | Unbuffered I/O (bypasses the Windows cache), very large buffers, separate reader / writer threads, sorts work per physical disk | Dated UI, many cryptic options |
| **TeraCopy** | Async buffered pipeline, checksum verification, pause/resume, skip/retry on error | Closed source, verification is a second full pass |
| **Robocopy** (built into Windows) | `/MT` multithreading for many small files, restartable mode, `/J` unbuffered for big files | Command line only, `/MT` hurts on HDD/USB, no verify |
| **Ultracopier / ExtremeCopy / RichCopy** | Queues, parallelism, shell integration | Mixed reliability, little innovation in raw I/O |
| **Windows Explorer** | `CopyFile2` | Single threaded per job, slow on many small files, no verify |

**Conclusions → what HyperCopy does**

1. **Large files: unbuffered, sector-aligned, double-pipelined I/O.** A reader thread and a writer thread
   share a ring of large aligned buffers (8–16 MB). The disk never waits for the CPU and the OS file cache
   is never flooded (Explorer's cache flooding is why copies "stall" at 99% and the whole PC lags).
2. **Pre-allocation.** The destination file is pre-allocated to its final size → no fragmentation, fewer
   metadata updates, and a "disk full" error is detected *before* writing 500 GB, not after.
3. **Small files: parallel.** Per-file overhead (open/create/close/timestamps) dominates for small files,
   so many are copied at once.
4. **Device-aware concurrency.** HyperCopy asks Windows whether each drive has a *seek penalty*
   (HDD) or is *removable* (USB stick). Parallel writes are great on NVMe/SSD and terrible on HDDs/USB,
   so concurrency is tuned per source/destination pair automatically (overridable).
5. **Instant moves.** A move inside the same volume is a metadata rename — terabytes in milliseconds.
   Cross-volume moves copy → (optionally verify) → delete the source file only after success.
6. **Inline verification.** The source is hashed (XXH3-64, >10 GB/s per core) *while* it is being read,
   so verifying costs one read of the destination only — not two extra passes.
7. **Resilience.** Per-file retry, skip-on-error with a report, pause/resume, cancel, and a
   "skip identical" mode that makes interrupted terabyte jobs resumable.

## 2. Architecture

See [ARCHITECTURE.md](ARCHITECTURE.md).

## 3. Feature list (v1.0)

- Select sources: multiple files and/or folders, or drag & drop. Select destination folder (or USB drive).
- **Copy** or **Move** toggle.
- If a file exists: Overwrite · Skip · Skip if identical (size + timestamp; enables resume) · Keep both (rename).
- Verify after copy (XXH3 checksum).
- Performance profile: Auto · SSD/NVMe · HDD/USB.
- Live progress: % , bytes, files, current file, speed (MB/s), ETA, speed graph.
- Pause / Resume / Cancel. Error list with reasons at the end.
- Preserves timestamps and attributes; supports long paths (> 260 chars).

## 4. Quality plan

1. Unit tests on the engine (empty files, sector-boundary sizes, multi-GB-like sparse files, nested trees,
   unicode names, long paths, read-only files, conflicts, move same/cross volume, cancel).
2. Byte-for-byte verification of every test copy by independent hashing.
3. Benchmarks vs. Robocopy and Explorer-equivalent `File.Copy` on SSD→SSD, SSD→HDD, many-small-files.
4. Release build smoke test of the single-file EXE.

## 5. What we learned while building v1.0

- **Small files:** our own handle-based copy lost to the kernel's CopyFileEx by 3–5× per file, so v1.0 uses CopyFileEx for files under 4 MB.
- **Write depth:** more concurrent writes helped SSDs slightly but hurt HDDs by 12–35%, so HDD/USB uses one writer.
- **Benchmark fairness:** buffered tools can report "done" while data is still in RAM, and a source file already in the RAM cache inflates read speed. The published numbers account for both.

## 6. Roadmap

See [ROADMAP.md](../ROADMAP.md).

## Original roadmap ideas

- Explorer right-click integration (optional, portable-friendly registry toggle).
- Job queue (multiple jobs, run in order per physical disk).
- Network share (SMB) tuning, ReFS/Dev Drive block cloning via `CopyFile2`.
- Code-signing certificate, GitHub Actions CI building the EXE on every tag.
