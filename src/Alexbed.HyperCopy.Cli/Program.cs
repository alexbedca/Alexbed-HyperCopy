using Alexbed.HyperCopy.Core;

// hypercopy copy|move <source>... <destination> [--verify] [--conflict skip-identical|overwrite|skip|keep-both]
//                                               [--profile auto|ssd|hdd] [--threads N] [--buffer-mb N] [--buffered]
if (args.Length < 3 || args[0] is not ("copy" or "move"))
{
    Console.WriteLine("""
        Alexbed HyperCopy CLI

          hypercopy copy <source>... <destination-folder> [options]
          hypercopy move <source>... <destination-folder> [options]

        Options:
          --verify                 re-read and checksum (XXH3) every copied file
          --conflict <policy>      skip-identical (default) | overwrite | skip | keep-both
          --profile <p>            auto (default) | ssd | hdd
          --threads <n>            override parallelism
          --buffer-mb <n>          override large-file buffer size
          --buffered               disable unbuffered I/O
          --quiet                  no live progress
        """);
    return 2;
}

var options = new CopyOptions { Mode = args[0] == "move" ? TransferMode.Move : TransferMode.Copy };
var positional = new List<string>();
bool quiet = false;
for (int i = 1; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--verify": options.Verify = true; break;
        case "--buffered": options.ForceBufferedIo = true; break;
        case "--quiet": quiet = true; break;
        case "--threads": options.ThreadsOverride = int.Parse(args[++i]); break;
        case "--write-depth": options.WriteDepthOverride = int.Parse(args[++i]); break;
        case "--buffer-mb": options.BufferSizeOverride = int.Parse(args[++i]) * 1024 * 1024; break;
        case "--profile":
            options.Profile = args[++i] switch { "ssd" => PerformanceProfile.SolidState, "hdd" => PerformanceProfile.Rotational, _ => PerformanceProfile.Auto };
            break;
        case "--conflict":
            options.Conflict = args[++i] switch
            {
                "overwrite" => ConflictPolicy.Overwrite,
                "skip" => ConflictPolicy.Skip,
                "keep-both" => ConflictPolicy.KeepBoth,
                _ => ConflictPolicy.SkipIdentical,
            };
            break;
        default: positional.Add(args[i]); break;
    }
}

string destination = positional[^1];
var sources = positional[..^1];
var engine = new CopyEngine(options);
using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };

CopyPlan plan;
var scanClock = System.Diagnostics.Stopwatch.StartNew();
try
{
    plan = engine.Scan(sources, destination, cts.Token);
}
catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException)
{
    Console.Error.WriteLine("Error: " + ex.Message);
    return 1;
}

Console.WriteLine($"{Format.Count(plan.Files.Count)} files, {Format.Bytes(plan.TotalBytes)} (scanned in {scanClock.Elapsed.TotalSeconds:0.00}s)");
Console.WriteLine($"Source: {plan.SourceDrive?.VolumeRoot} [{plan.SourceDrive?.KindLabel}]  →  Destination: {plan.DestinationDrive?.VolumeRoot} [{plan.DestinationDrive?.KindLabel}, {plan.DestinationDrive?.FileSystem}]");
Console.WriteLine($"Tuning: {plan.Tuning!.Description}");
foreach (var w in plan.Warnings) Console.WriteLine("Warning: " + w);

var job = engine.ExecuteAsync(plan, cts.Token);
if (!quiet)
{
    while (!job.Wait(500))
        Console.Write($"\r{engine.Progress.Fraction:P1}  {Format.Bytes(engine.Progress.TransferredBytes)}  files {engine.Progress.CompletedFiles}/{plan.Files.Count}     ");
    Console.WriteLine();
}
var r = job.Result;
Console.WriteLine($"Done in {r.Elapsed.TotalSeconds:0.00}s — copied {r.FilesCopied}, skipped {r.FilesSkipped}, failed {r.FilesFailed}, " +
                  $"{Format.Bytes(r.BytesTransferred)} @ {Format.Speed(r.AverageBytesPerSecond)}");
if (r.Verified)
    Console.WriteLine(r.ChecksumsFailed > 0
        ? $"Checksums: FAIL — {r.ChecksumsFailed} file(s) did not match the source ({r.ChecksumsPassed} passed)"
        : $"Checksums: PASS — {r.ChecksumsPassed} file(s) read back and matched" + (r.FilesSkipped > 0 ? $" ({r.FilesSkipped} skipped, not checked)" : ""));
if (r.Cancelled) Console.WriteLine("Cancelled.");
if (r.FatalError is not null) Console.WriteLine("Fatal: " + r.FatalError);
foreach (var e in r.Errors.Take(50)) Console.WriteLine($"  ! {e.Path}: {e.Message}");
return r.Success ? 0 : 1;
