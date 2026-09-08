using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CodeCompass.Core.Diagnostics;

/// <summary>
/// Keeps this process running at full speed regardless of how it was launched. When a console app is
/// attached to the legacy Windows console host (conhost.exe), Windows may place it under EcoQoS / power
/// throttling - parking it on the CPU's efficiency (E) cores at reduced clock. On a hybrid CPU that can
/// make an identical run ~2x slower from a plain console vs Windows Terminal, with cores never fully
/// pegged. This explicitly opts the process OUT of execution-speed throttling so heavy parallel indexing
/// gets the performance cores at full clock no matter the launching console.
/// </summary>
public static class ProcessPerformance
{
    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_POWER_THROTTLING_STATE
    {
        public uint Version;
        public uint ControlMask;
        public uint StateMask;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetProcessInformation(IntPtr hProcess, int ProcessInformationClass,
        ref PROCESS_POWER_THROTTLING_STATE ProcessInformation, int ProcessInformationSize);

    private const int ProcessPowerThrottling = 4; // ProcessPowerThrottling from PROCESS_INFORMATION_CLASS
    private const uint PROCESS_POWER_THROTTLING_CURRENT_VERSION = 1;
    private const uint PROCESS_POWER_THROTTLING_EXECUTION_SPEED = 0x1;

    /// <summary>
    /// Disables OS execution-speed (EcoQoS) throttling for the current process, and optionally nudges its
    /// priority up. Best-effort: any failure (older/other OS, insufficient rights) is swallowed - it only
    /// ever affects speed, never correctness. Call once at the very start of Main.
    /// </summary>
    /// <param name="raisePriority">
    /// Raise the process priority to AboveNormal. Wanted for short-lived batch runs (CLI index, bench);
    /// pass false for the long-lived MCP server so it doesn't outrank the editor/agent it shares the box with.
    /// </param>
    public static void RequestFullSpeed(bool raisePriority = true)
    {
        // ControlMask = EXECUTION_SPEED ("I manage this policy"); StateMask = 0 ("throttling OFF").
        try
        {
            var state = new PROCESS_POWER_THROTTLING_STATE
            {
                Version = PROCESS_POWER_THROTTLING_CURRENT_VERSION,
                ControlMask = PROCESS_POWER_THROTTLING_EXECUTION_SPEED,
                StateMask = 0,
            };
            SetProcessInformation(Process.GetCurrentProcess().Handle, ProcessPowerThrottling,
                ref state, Marshal.SizeOf(state));
        }
        catch { /* older/non-Windows runtimes won't have this - ignore */ }

        if (!raisePriority) return;

        // Nudge the scheduler toward foreground compute. AboveNormal is enough; High/RealTime risk
        // starving the UI.
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; }
        catch { /* priority hint is not worth failing the run over */ }
    }
}
