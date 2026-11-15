using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Alexbed.HyperCopy.Core;

public enum StorageKind
{
    Unknown,
    SolidState,
    Rotational,
    /// <summary>USB stick / SD card / external USB enclosure.</summary>
    Usb,
    Network,
}

/// <summary>What HyperCopy learned about the volume behind a path.</summary>
public sealed record DriveDescriptor(
    string VolumeRoot,
    StorageKind Kind,
    string? FileSystem,
    int? PhysicalDiskNumber,
    long FreeBytes)
{
    public bool IsFat32 => string.Equals(FileSystem, "FAT32", StringComparison.OrdinalIgnoreCase)
                           || string.Equals(FileSystem, "FAT", StringComparison.OrdinalIgnoreCase);

    public string KindLabel => Kind switch
    {
        StorageKind.SolidState => "SSD",
        StorageKind.Rotational => "HDD",
        StorageKind.Usb => "USB",
        StorageKind.Network => "Network",
        _ => "Disk",
    };
}

/// <summary>Concrete I/O parameters the engine uses for a job.</summary>
public sealed record TuningProfile(
    int SmallFileThreads,
    int LargeFileThreads,
    int BufferSize,
    int BufferCount,
    bool UnbufferedRead,
    bool UnbufferedWrite,
    bool Serial,
    string Description,
    int WriteDepth = 2);

/// <summary>
/// Asks Windows what kind of storage sits behind a path: SSD vs HDD (seek penalty),
/// USB bus, network, physical disk number. Never throws; unknowns are reported as such.
/// </summary>
public static class DriveProbe
{
    public static DriveDescriptor Describe(string path)
    {
        string full = Path.GetFullPath(path);
        string volumeRoot = GetVolumeRoot(full);

        if (full.StartsWith(@"\\", StringComparison.Ordinal) && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
            return new DriveDescriptor(volumeRoot, StorageKind.Network, null, null, GetFreeBytes(volumeRoot));

        string? fs = null;
        DriveType driveType = DriveType.Unknown;
        try
        {
            var di = new DriveInfo(volumeRoot);
            driveType = di.DriveType;
            if (di.IsReady) fs = di.DriveFormat;
        }
        catch { /* mounted folders etc. */ }

        if (driveType == DriveType.Network)
            return new DriveDescriptor(volumeRoot, StorageKind.Network, fs, null, GetFreeBytes(volumeRoot));

        StorageKind kind = driveType == DriveType.Removable ? StorageKind.Usb : StorageKind.Unknown;
        int? diskNumber = null;

        string? volumeDevice = GetVolumeDevicePath(volumeRoot);
        if (volumeDevice is not null)
        {
            using SafeFileHandle h = Native.CreateFileW(volumeDevice, 0,
                FileShare.ReadWrite | FileShare.Delete, IntPtr.Zero, FileMode.Open, 0, IntPtr.Zero);
            if (!h.IsInvalid)
            {
                diskNumber = QueryDiskNumber(h);
                if (kind == StorageKind.Unknown)
                {
                    if (QueryBusType(h) is 7 /* BusTypeUsb */ or 12 /* BusTypeSd */ or 13 /* BusTypeMmc */)
                        kind = StorageKind.Usb;
                    else if (QuerySeekPenalty(h) is bool seek)
                        kind = seek ? StorageKind.Rotational : StorageKind.SolidState;
                }
            }
        }

        return new DriveDescriptor(volumeRoot, kind, fs, diskNumber, GetFreeBytes(volumeRoot));
    }

    /// <summary>True when both paths live on the same volume, so a move is a metadata rename.</summary>
    public static bool IsSameVolume(string a, string b) =>
        string.Equals(GetVolumeRoot(Path.GetFullPath(a)), GetVolumeRoot(Path.GetFullPath(b)),
            StringComparison.OrdinalIgnoreCase);

    public static TuningProfile ResolveTuning(DriveDescriptor? source, DriveDescriptor? dest, CopyOptions options)
    {
        const int MB = 1024 * 1024;
        bool network = source?.Kind == StorageKind.Network || dest?.Kind == StorageKind.Network;

        bool slow = options.Profile switch
        {
            PerformanceProfile.SolidState => false,
            PerformanceProfile.Rotational => true,
            _ => IsSlow(source) || IsSlow(dest),
        };

        bool samePhysicalDisk = source?.PhysicalDiskNumber is int s && dest?.PhysicalDiskNumber is int d && s == d;

        TuningProfile t;
        if (network && options.Profile == PerformanceProfile.Auto)
        {
            t = new TuningProfile(8, 2, 8 * MB, 4, source?.Kind != StorageKind.Network, false, false,
                "Network · parallel streams");
        }
        else if (slow)
        {
            t = new TuningProfile(samePhysicalDisk ? 1 : 2, 1, (samePhysicalDisk ? 64 : 32) * MB, samePhysicalDisk ? 2 : 4,
                true, true, true,
                samePhysicalDisk
                    ? $"Same {(dest?.Kind == StorageKind.Usb ? "USB drive" : dest?.Kind == StorageKind.Rotational ? "HDD" : "drive")} · 1 stream · 64 MB blocks"
                    : "HDD/USB · sequential · 32 MB unbuffered",
                WriteDepth: 1); // measured: concurrent writes make HDD heads seek (-12..-35%)
        }
        else
        {
            int cores = Environment.ProcessorCount;
            t = new TuningProfile(Math.Clamp(cores * 2, 4, 16), 2, 16 * MB, 4, true, true, false,
                "SSD · parallel · 16 MB unbuffered");
        }

        if (options.ForceBufferedIo)
            t = t with { UnbufferedRead = false, UnbufferedWrite = false };
        if (options.BufferSizeOverride is int bs and > 0)
            t = t with { BufferSize = AlignUp(bs, 64 * 1024) };
        if (options.WriteDepthOverride is int wd and > 0)
            t = t with { WriteDepth = wd };
        if (options.ThreadsOverride is int th and > 0)
            t = t with { SmallFileThreads = th, LargeFileThreads = Math.Max(1, Math.Min(th, 4)), Serial = th == 1 };
        return t;
    }

    private static bool IsSlow(DriveDescriptor? d) =>
        d is not null && d.Kind is StorageKind.Rotational or StorageKind.Usb or StorageKind.Unknown;

    internal static int AlignUp(int value, int alignment) => (value + alignment - 1) / alignment * alignment;

    public static string GetVolumeRoot(string fullPath)
    {
        var sb = new char[1024];
        if (Native.GetVolumePathNameW(fullPath, sb, sb.Length))
        {
            string root = new string(sb).TrimEnd('\0');
            if (root.Length > 0) return root;
        }
        return Path.GetPathRoot(fullPath) ?? fullPath;
    }

    private static long GetFreeBytes(string volumeRoot)
    {
        return Native.GetDiskFreeSpaceExW(volumeRoot, out ulong freeToCaller, out _, out _)
            ? (long)Math.Min(freeToCaller, long.MaxValue)
            : -1;
    }

    private static string? GetVolumeDevicePath(string volumeRoot)
    {
        string root = volumeRoot.EndsWith('\\') ? volumeRoot : volumeRoot + "\\";
        var buf = new char[64];
        if (!Native.GetVolumeNameForVolumeMountPointW(root, buf, buf.Length))
            return null;
        string name = new string(buf).TrimEnd('\0'); // \\?\Volume{guid}\
        return name.TrimEnd('\\');
    }

    private static unsafe int? QueryDiskNumber(SafeFileHandle h)
    {
        // STORAGE_DEVICE_NUMBER { DWORD DeviceType; DWORD DeviceNumber; DWORD PartitionNumber; }
        uint* buf = stackalloc uint[3];
        return Native.DeviceIoControl(h, Native.IOCTL_STORAGE_GET_DEVICE_NUMBER, null, 0, buf, 12, out _, IntPtr.Zero)
            ? (int)buf[1]
            : null;
    }

    private static unsafe bool? QuerySeekPenalty(SafeFileHandle h)
    {
        // STORAGE_PROPERTY_QUERY { PropertyId = StorageDeviceSeekPenaltyProperty (7), QueryType = 0 }
        uint* query = stackalloc uint[3];
        query[0] = 7; query[1] = 0; query[2] = 0;
        // DEVICE_SEEK_PENALTY_DESCRIPTOR { DWORD Version; DWORD Size; BOOLEAN IncursSeekPenalty; }
        byte* outBuf = stackalloc byte[12];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, query, 12, outBuf, 12, out uint ret, IntPtr.Zero) || ret < 9)
            return null;
        return outBuf[8] != 0;
    }

    private static unsafe int? QueryBusType(SafeFileHandle h)
    {
        // StorageDeviceProperty (0) → STORAGE_DEVICE_DESCRIPTOR; BusType lives at byte offset 28.
        uint* query = stackalloc uint[3];
        query[0] = 0; query[1] = 0; query[2] = 0;
        byte* outBuf = stackalloc byte[512];
        if (!Native.DeviceIoControl(h, Native.IOCTL_STORAGE_QUERY_PROPERTY, query, 12, outBuf, 512, out uint ret, IntPtr.Zero) || ret < 32)
            return null;
        return *(int*)(outBuf + 28);
    }

    private static class Native
    {
        public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
        public const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern SafeFileHandle CreateFileW(string name, uint access, FileShare share, IntPtr security,
            FileMode mode, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        public static extern unsafe bool DeviceIoControl(SafeFileHandle h, uint code, void* inBuf, uint inSize,
            void* outBuf, uint outSize, out uint returned, IntPtr overlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumePathNameW(string fileName, [Out] char[] volumePathName, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, int bufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern bool GetDiskFreeSpaceExW(string directory, out ulong freeToCaller, out ulong total, out ulong totalFree);
    }
}
