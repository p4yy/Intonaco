using System;
using System.Diagnostics;
using System.Globalization;

namespace Intonaco.Core
{
    /// <summary>
    /// Process footprint sampled on demand, never on a rendering callback.
    /// </summary>
    internal sealed class PerformanceSnapshot
    {
        public long WorkingSetBytes { get; init; }
        public long PrivateBytes { get; init; }
        public long ManagedBytes { get; init; }
        public int ThreadCount { get; init; }
        public int HandleCount { get; init; }
        public TimeSpan TotalProcessorTime { get; init; }

        public static PerformanceSnapshot Capture()
        {
            using var process = Process.GetCurrentProcess();
            process.Refresh();
            return new PerformanceSnapshot
            {
                WorkingSetBytes = process.WorkingSet64,
                PrivateBytes = process.PrivateMemorySize64,
                ManagedBytes = GC.GetTotalMemory(forceFullCollection: false),
                ThreadCount = process.Threads.Count,
                HandleCount = process.HandleCount,
                TotalProcessorTime = process.TotalProcessorTime
            };
        }

        public override string ToString()
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"working-set={WorkingSetBytes / 1048576.0:F1} MB, " +
                $"private={PrivateBytes / 1048576.0:F1} MB, " +
                $"managed={ManagedBytes / 1048576.0:F1} MB, " +
                $"threads={ThreadCount}, handles={HandleCount}, " +
                $"cpu-total={TotalProcessorTime.TotalSeconds:F2}s");
        }
    }

    /// <summary>Diagnostics remain optional and never cause GC.</summary>
    internal static class PerformanceMonitor
    {
        public static void Log(string reason)
        {
            try
            {
                DebugLog.Write($"Performance[{reason}]: {PerformanceSnapshot.Capture()}");
            }
            catch
            {
                // Diagnostics cannot interrupt playback.
            }
        }
    }
}
