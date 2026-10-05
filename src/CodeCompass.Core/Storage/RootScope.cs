using System.Linq;

namespace CodeCompass.Core.Storage;

/// <summary>
/// Resolves a user-supplied focus token (a repo folder name, a path fragment, or a full absolute path)
/// against the known root set (the primary project root plus its linked roots). Pure and case-insensitive
/// so the MCP tool stays thin and the matching is unit-testable in isolation.
///
/// Matching precedence, first non-empty wins:
///   1. exact absolute-path equality (when the token is itself a rooted path),
///   2. basename equality (token == a root's folder name - the common "focus repoA" case),
///   3. case-insensitive substring of the full path.
/// Substring is deliberately last and deliberately allowed to match MORE than one root: a shared-parent
/// token (e.g. "big") selects every root under it, which is exactly how a caller asks to "search both".
/// All returned paths are normalized (full path, no trailing separator) so callers can compare by value.
/// </summary>
public static class RootScope
{
    /// <summary>The normalized roots matched by <paramref name="token"/> (empty if none match).</summary>
    public static IReadOnlyList<string> Match(IReadOnlyList<string> roots, string token)
    {
        token = (token ?? "").Trim();
        if (token.Length == 0 || roots.Count == 0) return Array.Empty<string>();
        var norm = roots.Select(Normalize).ToList();

        if (Path.IsPathRooted(token))
        {
            var nt = Normalize(token);
            var exact = norm.Where(r => string.Equals(r, nt, StringComparison.OrdinalIgnoreCase)).ToList();
            if (exact.Count > 0) return exact;
        }

        var byBase = norm.Where(r => string.Equals(Path.GetFileName(r), token, StringComparison.OrdinalIgnoreCase)).ToList();
        if (byBase.Count > 0) return byBase;

        return norm.Where(r => r.Contains(token, StringComparison.OrdinalIgnoreCase)).ToList();
    }

    /// <summary>Normalize a root for value comparison: absolute, no trailing separator.</summary>
    public static string Normalize(string p) => PathSafety.NormalizeDir(p);
}
