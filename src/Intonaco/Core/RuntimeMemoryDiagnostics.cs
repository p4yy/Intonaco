using System;
using System.Diagnostics;

namespace Intonaco.Core
{
    /// <summary>
    /// Low-overhead diagnostic sampling: compares process committed memory
    /// with .NET's live heap, so native decoder allocations are not mistaken
    /// for managed object leaks. Never forces a full collection and never
    /// changes the memory reported by Task Manager.
    /// </summary>
    internal static class RuntimeMemoryDiagnostics
    {
        private static readonly long SampleIntervalTicks =
            (long)(Stopwatch.Frequency * 45.0);
        private static long _lastSampleTimestamp;

        internal static void LogIfDue(
            string backend, bool livePreview, int videoWidth, int videoHeight,
            long helperWorkingSetBytes = 0)
        {
            long now = Stopwatch.GetTimestamp();
            if (_lastSampleTimestamp != 0 &&
                now - _lastSampleTimestamp < SampleIntervalTicks)
                return;

            _lastSampleTimestamp = now;
            try
            {
                using Process process = Process.GetCurrentProcess();
                process.Refresh();
                long managedBytes = GC.GetTotalMemory(forceFullCollection: false);
                long workingBytes = process.WorkingSet64;
                long privateBytes = process.PrivateMemorySize64;
                double mib = 1024.0 * 1024.0;
                DebugLog.Write(
                    "Memory sample: backend=" + backend +
                    " video=" + videoWidth + "x" + videoHeight +
                    " preview=" + livePreview +
                    " appWorkingSetMiB=" + (workingBytes / mib).ToString("F1") +
                    " appPrivateMiB=" + (privateBytes / mib).ToString("F1") +
                    " managedLiveMiB=" + (managedBytes / mib).ToString("F1") +
                    " helperWorkingSetMiB=" + (helperWorkingSetBytes / mib).ToString("F1"));
            }
            catch (Exception ex)
            {
                DebugLog.Write("Memory sample unavailable: " + ex.Message);
            }
        }
    }
}
