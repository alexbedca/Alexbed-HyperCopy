# Roadmap

## v1.1: small files
- Close the gap with Robocopy `/MT` on thousands of tiny files on SSDs (currently about 2× slower; see
  [benchmark-results.md](docs/benchmark-results.md)). Leads: batched directory creation, different
  CopyFile2 flags per file size, profiling filter-driver overhead.
- Async overlapped reads for NVMe sources (more reads in flight).

## v1.2: convenience
- Job queue: several jobs run one after another, per physical disk.
- Remember the last destinations and options (stored next to the exe; portable).
- Optional Explorer right-click "Copy with HyperCopy" that can be turned off again (per user, no admin).
- Light theme and high-contrast support.

## Later
- Network share (SMB) tuning and resume of partially copied large files.
- ReFS / Dev Drive block cloning (instant copies on the same volume).
- Code-signed releases.
- Translations.

Ideas and votes are welcome in [Issues](../../issues).
