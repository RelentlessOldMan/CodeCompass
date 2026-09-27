using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace CodeCompass.Core.Diagnostics;

/// <summary>
/// Probes how much memory the machine can ACTUALLY commit right now - not how much RAM it has.
/// On Windows the binding limit for a large allocation is the system COMMIT limit (physical RAM +
/// page file); when other processes have charged most of it, a byte[] read of a big file throws
/// <see cref="OutOfMemoryException"/> even though physical RAM looks free. The indexer sizes its
/// read budget against this so it throttles under pressure instead of over-committing and dying.
/// Returns 0 when the figure can't be determined (caller then falls back to a RAM-fraction budget).
/// </summary>
public static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;   // total commit limit
        public ulong ullAvailPageFile;   // commit still available to charge
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [SupportedOSPlatform("windows")]
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX buffer);

    /// <summary>Bytes the process can still commit without hitting the OS limit, capped at available
    /// physical RAM so we don't lean on the page file (which would only trade an OOM for thrashing).
    /// 0 if unknown.</summary>
    public static long AvailableCommitBytes()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                var s = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref s))
                    return (long)Math.Min(s.ullAvailPageFile, s.ullAvailPhys);
            }
        }
        catch { /* P/Invoke unavailable - fall through */ }

        // Non-Windows / failure: approximate free physical from the GC's view of the machine.
        try
        {
            var gi = GC.GetGCMemoryInfo();
            long free = gi.TotalAvailableMemoryBytes - gi.MemoryLoadBytes;
            if (free > 0) return free;
        }
        catch { /* ignore */ }
        return 0;
    }
}
