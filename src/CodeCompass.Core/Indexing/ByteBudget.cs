namespace CodeCompass.Core.Indexing;

/// <summary>
/// A counting gate measured in bytes, used to bound how much file content is read into memory at
/// once during a parallel build. A worker acquires a file's size before reading it and releases
/// after processing, so the total in-flight bytes stay near the budget no matter how many cores
/// are running - preventing the "N cores each load a 1 GB file simultaneously" blow-up when the
/// file cap is large. A file larger than the whole budget reserves all of it and runs alone
/// (so progress is always possible; it never deadlocks).
///
/// FIFO: requests are served in arrival order. Without that, a request for (nearly) the whole budget waits for the
/// budget to drain completely while a stream of small requests keeps winning the race for whatever frees up - it can
/// starve for most of a build (and the stall watchdog then blames that healthy file).
/// </summary>
public sealed class ByteBudget
{
    private readonly long _budget;
    private long _available;
    private readonly object _gate = new();
    private long _nextTicket; // next ticket to hand out
    private long _serving;    // the ticket now allowed to take bytes (head of the line)

    public ByteBudget(long budgetBytes)
    {
        _budget = Math.Max(1, budgetBytes);
        _available = _budget;
    }

    public void Acquire(long bytes)
    {
        long need = Reserve(bytes);
        lock (_gate)
        {
            long ticket = _nextTicket++;
            while (ticket != _serving || _available < need) Monitor.Wait(_gate);
            _available -= need;
            _serving++;
            Monitor.PulseAll(_gate); // the next in line may already fit
        }
    }

    /// <summary>Bytes currently available (diagnostics / tests).</summary>
    public long Available { get { lock (_gate) return _available; } }

    public void Release(long bytes)
    {
        long need = Reserve(bytes);
        lock (_gate)
        {
            _available += need;
            Monitor.PulseAll(_gate);
        }
    }

    // Clamp to the budget so an over-budget file reserves the whole thing (runs solo) rather than
    // requesting more than exists (which could never be satisfied -> deadlock).
    private long Reserve(long bytes) => Math.Min(Math.Max(bytes, 0), _budget);
}
