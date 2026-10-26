using System.Diagnostics;

namespace CodeCompass.Core.SelfTest;

/// <summary>
/// Samples a child process's working set on a background thread: the peak over its life, the peak over a window (one
/// tool call), and a hard kill at a ceiling so a runaway can never take the machine down. Process isn't thread-safe,
/// so every read goes through one lock.
/// </summary>
public sealed class ProcessSampler : IDisposable
{
    private readonly Process _p;
    private readonly long _ceilingBytes;
    private readonly int _intervalMs;
    private readonly Thread _thread;
    private readonly object _gate = new();
    private volatile bool _stop;
    private long _peak, _windowPeak, _last;

    /// <param name="ceilingBytes">Kill the process (and its children) past this working set; 0 = never.</param>
    public ProcessSampler(Process p, long ceilingBytes, int intervalMs = 100)
    {
        _p = p;
        _ceilingBytes = ceilingBytes;
        _intervalMs = intervalMs;
        _thread = new Thread(Run) { IsBackground = true, Name = "selftest-sampler" };
        _thread.Start();
    }

    /// <summary>The default ceiling: 60% of the machine's memory, as probe-repo.ps1 uses.</summary>
    public static long DefaultCeilingBytes()
    {
        try { return (long)(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes * 0.6); } catch { return 0; }
    }

    public bool Killed { get; private set; }
    public double PeakMb => Interlocked.Read(ref _peak) / 1048576.0;
    public double LastMb => Interlocked.Read(ref _last) / 1048576.0;
    public double WindowPeakMb => Interlocked.Read(ref _windowPeak) / 1048576.0;

    /// <summary>Start a new window (a tool call): its peak starts at the working set now.</summary>
    public void StartWindow()
    {
        Interlocked.Exchange(ref _windowPeak, 0);
        Sample();
    }

    /// <summary>Take one sample now (a call shorter than the interval still gets its end measured).</summary>
    public void Sample()
    {
        long ws;
        lock (_gate)
        {
            try
            {
                if (_p.HasExited) return;
                _p.Refresh();
                ws = _p.WorkingSet64;
            }
            catch { return; }
        }
        Interlocked.Exchange(ref _last, ws);
        Max(ref _peak, ws);
        Max(ref _windowPeak, ws);
        if (_ceilingBytes > 0 && ws > _ceilingBytes && !Killed)
        {
            Killed = true;
            try { _p.Kill(entireProcessTree: true); } catch { }
        }
    }

    private static void Max(ref long field, long value)
    {
        long cur;
        while (value > (cur = Interlocked.Read(ref field)) && Interlocked.CompareExchange(ref field, value, cur) != cur) { }
    }

    private void Run()
    {
        while (!_stop)
        {
            Sample();
            bool exited;
            try { exited = _p.HasExited; } catch { exited = true; }
            if (exited || Killed) return;
            Thread.Sleep(_intervalMs);
        }
    }

    public void Dispose()
    {
        _stop = true;
        _thread.Join(2000);
    }
}
