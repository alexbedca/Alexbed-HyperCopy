using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Alexbed.HyperCopy.Core;

/// <summary>
/// Small-file copy through the kernel's CopyFileEx that also keeps the source's creation time.
/// The creation time is stamped through CopyFileEx's own destination handle (from the progress
/// callback when the data stream starts), which saves the second open/close of every new file
/// that File.Copy + File.SetCreationTimeUtc needed (measured ~10–15% on 20,000 small files).
/// </summary>
internal static unsafe class SmallFileCopy
{
    private const uint CALLBACK_STREAM_SWITCH = 1;
    private const uint PROGRESS_CONTINUE = 0;
    private const uint COPY_FILE_FAIL_IF_EXISTS = 0x1;

    private struct StampState
    {
        public long CreationFileTime;
        public int Stamped;
    }

    /// <summary>
    /// Copies <paramref name="source"/> to <paramref name="dest"/> (data, attributes, modified time) and
    /// sets the creation time. Returns false when the callback could not stamp it, so the caller can
    /// fall back to setting it by path. Throws like File.Copy (HResult = HRESULT_FROM_WIN32(error)).
    /// </summary>
    public static bool Copy(string source, string dest, DateTime creationUtc, bool overwrite = true)
    {
        var state = new StampState { CreationFileTime = creationUtc.ToFileTimeUtc() };
        int cancel = 0;
        if (!CopyFileExW(source, dest, &OnProgress, &state, &cancel, overwrite ? 0 : COPY_FILE_FAIL_IF_EXISTS))
            throw ToException(Marshal.GetLastPInvokeError(), source);
        return state.Stamped != 0;
    }

    /// <summary>Maps a Win32 error to the exception File.Copy would throw. Always an IOException or
    /// UnauthorizedAccessException (never COMException), so disk-full and retry checks keep working.</summary>
    internal static Exception ToException(int error, string path)
    {
        string msg = $"{Marshal.GetPInvokeErrorMessage(error)} : '{path}'";
        int hr = unchecked((int)0x80070000 | error);
        return error switch
        {
            2 => new FileNotFoundException(msg, path) { HResult = hr },
            3 => new DirectoryNotFoundException(msg) { HResult = hr },
            5 => new UnauthorizedAccessException(msg) { HResult = hr },
            206 => new PathTooLongException(msg) { HResult = hr },
            _ => new IOException(msg, hr),
        };
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint OnProgress(long totalSize, long totalDone, long streamSize, long streamDone,
        uint streamNumber, uint reason, IntPtr hSource, IntPtr hDest, void* data)
    {
        // Stream 1 is the file's main data stream; its destination handle is open for writing.
        // CopyFileEx later sets only the modified time, so the creation time set here sticks.
        var s = (StampState*)data;
        if (reason == CALLBACK_STREAM_SWITCH && streamNumber == 1 && hDest != IntPtr.Zero && s->Stamped == 0 &&
            SetFileTime(hDest, &s->CreationFileTime, null, null))
            s->Stamped = 1;
        return PROGRESS_CONTINUE;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern bool CopyFileExW(string existing, string newFile,
        delegate* unmanaged[Stdcall]<long, long, long, long, uint, uint, IntPtr, IntPtr, void*, uint> progress,
        void* data, int* cancel, uint flags);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    private static extern bool SetFileTime(IntPtr h, long* creation, long* lastAccess, long* lastWrite);
}
