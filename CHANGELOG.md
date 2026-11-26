# Changelog

## 1.0.5 (2026-11-25)

Faster to use: no more freezes when opening the app or loading lots of files.

- **No freeze after opening.** The window used to appear and then stop responding for about 0.8 s while Windows
  started its typing/keyboard-language service. That service now starts only if you click into the Destination box.
  Measured on a Windows 11 PC: window ready in 1.4 s (1.36–1.45 s), where before it was 1.3–2.6 s plus the freeze.
- **Dropping thousands of files no longer locks the window.** 20,000 files: 11.5 s frozen before, now 1.45 s with
  the window responsive. 5,000 files: 2.3 s frozen before, now 0.4 s.
- **Starting a copy of 20,000 separately added files** no longer freezes the window for 3.5 s (lists now only draw
  the rows you can see).
- **Closing the window during a copy** now waits until the half-copied file is removed, as the dialog promises.
- New pre-release checks `tools/ui-speed-check.ps1` (start-up and freezes) and `tools/ui-states.ps1` (every screen);
  53 automated tests.

## 1.0.4 (2026-11-15)

- Fixed: during a copy with **Verify checksums** on, the checksum label ("Checksums · checked after each file")
  covered the "Elapsed" timer. It now reads "Checksums · pending" until the first file is checked.
- Screenshots and the social preview now show the current version.
- Tested on a USB stick (exFAT) this time: all 22 release checks passed, and HyperCopy was the fastest of the
  tools measured on it (see [benchmark-results.md](docs/benchmark-results.md#usb-stick-v104)).

## 1.0.3 (2026-11-15)

Fixes a bug reported on 1.0.2, plus a small speed-up.

- **Fixed: copying a folder into the folder it is already in.** Example: source `H:\Apps\My App`, destination
  `H:\Apps`. Every file would be copied onto itself. 1.0.2 started anyway, retried each file for about a minute,
  failed them all and then showed a misleading result. Now the window warns you as soon as you pick such a
  destination, and **Start** stays disabled until you choose another one. The command line refuses it at once.
- **Fixed: the result screen now only shows what really happened.**
  - "Checksums · not run" (orange) when no file could be copied. 1.0.2 showed a tick there.
  - "Checksums · nothing to check" now has an info icon instead of a tick (for example, when every file was skipped).
  - Skipped or failed files no longer count as "verified" data.
  - The DATA tile shows what was actually written, and SPEED shows "—" when nothing was written (was "0 B/s").
  - "Nothing was copied · N errors" (red) when every file failed.
  - A missing source or an unreadable folder now gives "Finished with N problems" instead of "Copy complete",
    and the command line returns exit code 1 (was 0).
  - The command line prints "Checksums: NOT RUN" instead of "PASS" when no file was copied.
  - The speed profile says "Same USB drive" instead of "Same HDD" on USB drives.
- **Faster with many small files:** about 15% fewer seconds on 20,000 small files. Each new file is now
  opened once instead of twice. This was measured on a busy PC, so treat it as approximate. Robocopy `/MT` is still faster
  here (see [ROADMAP.md](ROADMAP.md)).
- New pre-release check (`tools/release-check.ps1`) that tests every function of the real exe on real drives;
  52 automated tests (was 38).

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
