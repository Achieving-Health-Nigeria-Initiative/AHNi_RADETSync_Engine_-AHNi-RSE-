using System.Diagnostics;
using System.Runtime;
using Microsoft.Extensions.Logging;

namespace AHNiRSE.Infrastructure.Memory;

/// <summary>
/// Forces a full compacting GC collection and trims the OS working set.
/// Called once after every report cycle — never during a cycle.
///
/// Why this is correct here (not premature optimisation):
///   The script cycle is a known, bounded bulk-allocation burst.
///   DataTable + ClosedXML put hundreds of MB on the LOH.
///   LOH is never compacted by the background GC — only an explicit
///   GCSettings.LargeObjectHeapCompactionMode = CompactOnce
///   followed by GC.Collect triggers the compaction.
///   Without this, working set grows monotonically for the lifetime of the service.
/// </summary>
public static class MemoryPressureHelper
{
    public static void ReleaseAfterCycle(ILogger logger)
    {
        // false = do not force a collection just to get the number
        var before = GC.GetTotalMemory(false);

        // Tell the GC to compact the LOH on the NEXT collection only
        GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;

        // Full blocking collection — gen0 + gen1 + gen2 + LOH compaction
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();

        // Second pass — finalizer queue may have freed more roots
        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

        var after = GC.GetTotalMemory(false);

        logger.LogInformation(
            "[Memory] GC cycle complete — managed heap {Before:N0} → {After:N0} bytes " +
            "(freed {Freed:N0} bytes, {FreedMb:F1} MB)",
            before, after, before - after, (before - after) / 1_048_576.0);

        // Trim the OS working set (Windows only).
        // This releases pages the OS can give to other processes.
        // Non-fatal on failure — the GC collect already did the real work.
        TrimWorkingSet(logger);
    }

    private static void TrimWorkingSet(ILogger logger)
    {
        if (!OperatingSystem.IsWindows()) return;

        try
        {
            // SetProcessWorkingSetSize(-1, -1) = "trim to minimum now"
            // Same call IIS application pools make on idle timeout.
            var process = Process.GetCurrentProcess();
            NativeMemory.TrimWorkingSet(process.Handle);

            process.Refresh();
            logger.LogInformation(
                "[Memory] Working set after trim: {WorkingSetMb:F1} MB",
                process.WorkingSet64 / 1_048_576.0);
        }
        catch (Exception ex)
        {
            logger.LogDebug("[Memory] Working set trim skipped: {Message}", ex.Message);
        }
    }
}
