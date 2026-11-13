# Changelog

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
