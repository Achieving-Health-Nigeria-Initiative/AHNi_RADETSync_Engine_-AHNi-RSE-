using System.Runtime.InteropServices;

namespace AHNiRSE.Infrastructure.Memory;

/// <summary>
/// P/Invoke wrapper for Win32 memory management calls.
/// Only used on Windows — all entry points are guarded by OperatingSystem.IsWindows().
/// </summary>
internal static class NativeMemory
{
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessWorkingSetSize(
        nint hProcess,
        nint dwMinimumWorkingSetSize,
        nint dwMaximumWorkingSetSize);

    /// <summary>
    /// Trims the process working set to its minimum immediately.
    /// Equivalent to what IIS application pools do on idle timeout.
    /// Only call after a GC.Collect — trimming before collection is pointless.
    /// </summary>
    internal static void TrimWorkingSet(nint processHandle)
    {
        if (!OperatingSystem.IsWindows()) return;
        SetProcessWorkingSetSize(processHandle, -1, -1);
    }
}
