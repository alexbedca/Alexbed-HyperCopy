# Changelog

## 1.0.2 (2026-11-14)

- **Opens faster:** the window appears in about 1.3 s instead of 1.7 s (median of 5 warm starts on a
  4-core PC with a SATA SSD). The exes are no longer compressed, so they are bigger: the app is 141 MB (was 64 MB)
  and `hypercopy.exe` 70 MB (was 36 MB, starts in 143 ms instead of 199 ms).

## 1.0.1 (2026-11-14)

- With **Verify checksums** on, the finished screen now shows a clear **Checksums PASS · N files** or
  **Checksum FAIL · N files** badge (hover it for details). No log file is written. The CLI prints the same verdict.

## 1.0.0 (2026-11-13)

First public release.

- Copy or move files, folders and whole drives, with drag & drop.
- Unbuffered, pipelined large-file engine with pre-allocation and in-flight XXH3 hashing.
- Automatic SSD / HDD / USB / network detection and tuning; manual SSD or HDD/USB profile.
- Small files copied in parallel through the Windows kernel copy engine.
- If a file exists: Skip identical (resumable), Replace, Skip, Keep both.
- Optional checksum verification that reads the copy back from the disk.
- Instant moves within a volume; copy → verify → delete for moves to another drive.
- Live percentage, speed, ETA, throughput graph, taskbar progress; pause, resume, cancel.
- Warnings for low free space and for 4 GB+ files going to FAT32.
- Keeps timestamps and attributes; long path support; keeps the PC awake during jobs.
- `hypercopy.exe` command-line version.
- 35 automated tests.
