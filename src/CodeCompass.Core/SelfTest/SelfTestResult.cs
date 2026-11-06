namespace CodeCompass.Core.SelfTest;

public enum StepOutcome { Pass, Fail, Warn, NotApplicable, Info }

/// <summary>One thing the self-test did and what it found. <see cref="Ms"/> and <see cref="PeakMb"/> are -1 when not
/// measured; <see cref="Answer"/> is the tool's answer (null when anonymized).</summary>
public sealed record SelfTestStep(string Feature, string What, StepOutcome Outcome, string Detail,
                                  long Ms = -1, double PeakMb = -1, string? Answer = null);

/// <summary>One find_references call of the deep sweep: the broadest names in the repo, the costliest lookups there are.</summary>
public sealed record RefsSweepRow(string Name, int Shown, bool Truncated, int Semantic, int Named, long Ms, double PeakMb,
                                  long RepeatMs, bool Deterministic, bool Ok, string Detail);

/// <summary>A place in the repo: a relative path and a 1-based line.</summary>
public sealed record SelfTestLocation(string Path, int Line);

/// <summary>A name the self-test looked up, where it's defined, and (for the searched names) every line that holds it
/// as a whole word - the expected answer, from the index, for a test that asks someone else the same question.</summary>
public sealed record SelfTestInput(string Name, string Kind, string Path, int Line, IReadOnlyList<SelfTestLocation>? Lines = null);

/// <summary>The inputs the self-test picked from the repo. Left out of an anonymized result.</summary>
public sealed record SelfTestInputs(IReadOnlyList<SelfTestInput> Definitions, IReadOnlyList<SelfTestInput> Searches,
                                    IReadOnlyList<SelfTestInput> Callers);

public sealed class SelfTestResult
{
    public SelfTestInputs? Inputs { get; set; }

    public string Version { get; init; } = "";
    public string RepoLabel { get; init; } = "";
    public bool Anonymized { get; init; }
    public DateTime StartedUtc { get; init; }
    public double Seconds { get; set; }

    /// <summary>Repo and machine facts, in display order.</summary>
    public List<(string Label, string Value)> Facts { get; } = new();
    public List<SelfTestStep> Steps { get; } = new();
    public List<RefsSweepRow> Sweep { get; } = new();

    /// <summary>The MCP server's working set: just after start, its peak over the whole session, and at the end.</summary>
    public double ServerStartMb { get; set; } = -1;
    public double ServerPeakMb { get; set; } = -1;
    public double ServerEndMb { get; set; } = -1;

    public int Failed => Steps.Count(s => s.Outcome == StepOutcome.Fail);
    public int Warned => Steps.Count(s => s.Outcome == StepOutcome.Warn);
    public int Passed => Steps.Count(s => s.Outcome == StepOutcome.Pass);
}
