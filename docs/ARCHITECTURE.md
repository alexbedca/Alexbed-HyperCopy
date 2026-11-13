# Architecture

## Layout

```
Alexbed.HyperCopy.slnx
├─ src/Alexbed.HyperCopy.Core     The engine. No UI code, fully unit-tested.
│   ├─ CopyEngine.cs              scan → plan → execute; small-file pool, large-file pool, moves
│   ├─ FastFileIo.cs              PipelinedFileCopier, aligned buffers, unbuffered open/read, hashing
│   ├─ DriveProbe.cs              SSD/HDD/USB/network detection, volume and disk identity, tuning table
│   ├─ CopyOptions.cs             mode, conflict policy, verify, profile, overrides
│   ├─ CopyPlan.cs                plan/result records
│   ├─ CopyProgress.cs            lock-free counters polled by the UI, pause gate
│   └─ Format.cs                  human-readable bytes / speed / durations
├─ src/Alexbed.HyperCopy.App      WPF window (MainWindow + MainViewModel), theme in App.xaml
├─ src/Alexbed.HyperCopy.Cli      hypercopy.exe console front end
├─ tests/Alexbed.HyperCopy.Tests  xUnit tests
└─ tools/                         make_logo.py (logo/ICO), benchmark.ps1
```

## The job pipeline

1. **Scan** (`CopyEngine.Scan`). Sources are walked with `FileSystemEnumerable`, which reads size,
   attributes and timestamps from the directory listing itself (no per-file stat calls). The result is a
   `CopyPlan`: files, folders (including empty ones), totals and warnings. Junction/symlink folders are
   skipped with a warning to avoid loops. The destination drive is probed, and FAT32 4 GB limits and free
   space are checked so the UI can warn *before* anything is written.
2. **Moves on the same volume** are tried first as a single `Directory.Move`/`File.Move` per selected
   item (instant). If the destination already exists, the engine merges file by file, still with
   renames only.
3. **Folders** are created up front.
4. **Small files** (< 4 MB) go to a parallel pool and are copied with CopyFileEx. Their creation time is
   restored afterwards, because CopyFileEx keeps only the modified time.
5. **Large files** go to a second pool. Each file uses a `PipelinedFileCopier`:

```
            ┌────────── free buffers ◄──────────┐
 source ──► reader thread ──► full buffers ──► writer thread(s) ──► destination
  (unbuffered, sequential,       (bounded           (unbuffered, positional,
   XXH3 hashed in order)          queue)             1 = HDD/USB, 2 = SSD)
```

   - Buffers are 4 KB-aligned native memory, as `FILE_FLAG_NO_BUFFERING` requires.
   - The destination is pre-allocated to the final size. The last block is padded to a sector multiple
     and the file is truncated to its exact length at the end.
   - If a file system refuses unbuffered handles, the copier falls back to buffered I/O silently.
6. **Verify** (optional) re-reads the destination unbuffered, so the check reads the disk rather than
   RAM, and compares the XXH3 with the hash taken while reading the source.
7. **Finish:** folder timestamps and attributes are applied deepest first. For a move, emptied source
   folders are removed.

On HDD/USB profiles, phases 4 and 5 run one after the other so the disk head isn't shared. On SSDs they
run at the same time.

## Tuning table (`DriveProbe.ResolveTuning`)

| Situation | Small-file threads | Large-file streams | Buffer | Writes in flight |
|---|---:|---:|---:|---:|
| SSD / NVMe | 2 × cores (4–16) | 2 | 16 MB × 4 | 2 |
| HDD / USB | 2 | 1 | 32 MB × 4 | 1 |
| Same physical HDD for source and destination | 1 | 1 | 64 MB × 2 | 1 |
| Network | 8 | 2 | 8 MB × 4 (buffered writes) | 2 |

Drive type comes from `IOCTL_STORAGE_QUERY_PROPERTY` (`StorageDeviceSeekPenaltyProperty`, and
`StorageDeviceProperty.BusType` for USB/SD). "Same physical disk" comes from
`IOCTL_STORAGE_GET_DEVICE_NUMBER`. No admin rights are needed.

## Safety model

- **No partial files.** If a file attempt fails or is cancelled, the destination file is deleted. It is
  only marked "created" once its handle is open, so a locked *source* can never delete an existing
  destination file.
- **Retries:** transient I/O errors are retried twice with a back-off. Missing files and path errors
  are not retried.
- **Disk full** stops the whole job with a clear message instead of failing every remaining file.
- **Move = copy → (verify) → delete source,** one file at a time. A skipped or failed file keeps its
  source.
- **Destination inside source** is rejected at scan time.
- **Read-only, hidden and system** destination files are handled when replacing.
- Long paths (> 260 characters) work: the app manifest is `longPathAware`, and .NET adds the `\\?\`
  prefix where needed.

## UI

`MainViewModel` owns the job. A 250 ms `DispatcherTimer` polls `CopyProgress` (plain `Interlocked`
counters), so the copy threads never wait on the UI thread. Speed is a 6-second sliding window; ETA is
based on the work rate, including verification. The graph is sampled every 0.5 s. During a job, the
window calls `SetThreadExecutionState` so the PC doesn't sleep.

The test hook `HYPERCOPY_SNAPSHOT=<png>` (plus optional `HYPERCOPY_AUTOSTART=1` and
`HYPERCOPY_SNAPSHOT_DELAY=<ms>`) renders the window to a PNG and exits. It's used to produce the
README screenshots.
