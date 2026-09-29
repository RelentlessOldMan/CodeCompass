namespace CodeCompass.Core.Diagnostics;

/// <summary>
/// Opt-in, field-only tracing for the find_references path, gated by <c>CODECOMPASS_DEBUG_REFS</c>.
/// OFF by default (zero cost - the flag is read ONCE at process start, so set it before launching the
/// server/CLI). When ON, the refs path writes to the common <c>codecompass.log</c>:
/// <list type="bullet">
///   <item>a one-line semantic-vs-lexical merge summary per query (from both the MCP tool and CLI
///   <c>refs</c>): candidate/parsed TUs, memory-stop, unresolved-include count, incomplete verdict, and
///   the C#/C++/lexical hit counts that made up the answer; and</item>
///   <item>each lexical candidate whose NETWORK scan came back EMPTY or hit a read error - a file the
///   trigram index named as containing the symbol, yet the read returned nothing. That is the signature
///   of a hit dropped on a UNC read, which is what the deterministic UNC refs-count gap looks like and
///   which <c>CODECOMPASS_FORCE_NETWORK=1</c> on local disk cannot reproduce.</item>
/// </list>
/// Built to diagnose that gap on the real share; harmless to leave shipped because it is inert unless
/// the env var is set. Written at Warn so it appears at the default log level and lands in the shared
/// log a field agent will look at, while still being suppressed entirely when logging is turned off.
/// </summary>
public static class RefsDebug
{
    // Read once: a field agent sets CODECOMPASS_DEBUG_REFS before launching, and this is checked
    // per-candidate in the scan hot path, so re-reading the environment each time would be wasteful.
    private static readonly bool _on = Compute();

    /// <summary>True when <c>CODECOMPASS_DEBUG_REFS</c> was set to a truthy value (1/true/on) at startup.</summary>
    public static bool On => _on;

    private static bool Compute()
    {
        var v = Environment.GetEnvironmentVariable("CODECOMPASS_DEBUG_REFS");
        return v is "1"
            || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase)
            || string.Equals(v, "on", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Emit one refs-debug line to the common log. No-op unless <see cref="On"/>.</summary>
    public static void Log(string message)
    {
        if (!_on) return;
        Diagnostics.Log.Global.Warn("[refs-debug] " + message);
    }
}
