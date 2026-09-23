using System.Linq;

namespace CodeCompass.Core.Storage;

/// <summary>
/// The single source of truth for "is this relative path safe to act on inside the repo?" - used both
/// as a defence against a corrupt/tampered on-disk cache (a stored doc path must not escape the repo)
/// and as an input filter for change events / targeted updates (a path outside the repo, the repo root
/// itself, or a rooted/escaping path must be ignored). Consolidated here so every caller applies the
/// same rule rather than hand-rolling subtly different <c>StartsWith("..")</c> checks.
/// </summary>
public static class PathSafety
{
    /// <summary>
    /// A path that must NOT be treated as an in-repo file: empty, the repo root itself (<c>"."</c>),
    /// rooted (absolute, or a different drive), or containing a <c>".."</c> segment (an escape). The
    /// segment split is why this is stricter-but-safer than <c>rel.StartsWith("..")</c>, which would
    /// also (wrongly) reject a real file literally named <c>"..foo"</c>.
    /// </summary>
    public static bool IsOutsideRepo(string rel) =>
        rel.Length == 0 || rel == "." || Path.IsPathRooted(rel) ||
        rel.Split('/', '\\').Any(p => p == "..");

    /// <summary>A relative, in-repo path: the negation of <see cref="IsOutsideRepo"/>.</summary>
    public static bool IsInsideRepo(string rel) => !IsOutsideRepo(rel);

    /// <summary>A bare filename (no directory separators, not rooted) - resolves only inside its dir.</summary>
    public static bool IsBareFileName(string name) =>
        name.Length > 0 && name == Path.GetFileName(name);

    /// <summary>Is <paramref name="child"/> the same directory as, or nested inside, <paramref name="parent"/>?
    /// Both are absolutized and compared case-insensitively (Windows). Used to reject linking a directory
    /// already covered by another root, and (federation) to confirm a result path belongs to a known root.</summary>
    public static bool IsUnderOrEqual(string child, string parent)
    {
        var c = Normalize(child);
        var p = Normalize(parent);
        if (string.Equals(c, p, StringComparison.OrdinalIgnoreCase)) return true;
        var rel = Path.GetRelativePath(p, c);
        return rel != "." && !rel.StartsWith("..", StringComparison.Ordinal) && !Path.IsPathRooted(rel);
    }

    private static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p));
}
