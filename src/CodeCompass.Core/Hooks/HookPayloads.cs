using System.Text.Json;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Storage;

namespace CodeCompass.Core.Hooks;

/// <summary>
/// JSON payloads emitted by the Claude Code hooks the plugin ships. Kept here (not in
/// the CLI's top-level program) so they're unit-testable.
/// </summary>
public static class HookPayloads
{
    // Only Grep (content search) is redirected: CodeCompass has no file-NAME search, so blocking Glob left the agent
    // a dead end it had to escape through a shell command. For the same reason only a Grep search_code can answer
    // the same way is redirected; everything else goes through to Grep (see ShouldRedirectGrep).
    private const string DenyReason =
        "CodeCompass is the indexed code-search tool for this workspace. Instead of Grep use: search_code " +
        "(literal text), find_definition, find_references (semantic for C#; by name elsewhere), find_callees, search_symbols - " +
        "precise file:line:col results for far fewer tokens. Use Read for a known file and Glob for file-NAME " +
        "patterns. Grep -i maps to search_code caseSensitive:false. Grep still runs for a regex, a pattern under 3 characters, " +
        "a subfolder or file, a path outside this project, or a glob/type/context/count option. (Disable with CODECOMPASS_ENFORCE=0.)";

    // Grep options search_code has an equivalent for (case, line numbers, result paging, the two plain output
    // modes). Any other option - glob, type, context lines, count, multiline, or one Grep adds later - means
    // search_code can't give the same answer, so the call goes to Grep.
    private static readonly HashSet<string> RedirectableOptions = new(StringComparer.Ordinal)
    { "pattern", "path", "-i", "-n", "head_limit", "offset", "output_mode", "multiline" };

    private const string SessionText =
        "CodeCompass is available for this workspace via MCP. For code search and navigation prefer its tools - " +
        "search_code, find_definition, find_references, find_callees, search_symbols - over Grep or reading whole " +
        "files: precise file:line:col ranges for far fewer tokens; find_references is semantic for C#, and matches " +
        "other languages (C/C++ included) by name, so same-named symbols are listed together. " +
        "The index auto-updates as files change.";

    /// <summary>Whether a Grep call (the PreToolUse hook's stdin JSON) should be redirected to CodeCompass: only when
    /// search_code gives the same answer - a non-empty literal pattern, over the whole indexed project, with no Grep
    /// option search_code lacks. The project is <paramref name="projectDir"/> (CLAUDE_PROJECT_DIR, which the MCP
    /// server indexes), else the payload's cwd; a Grep with no path searches the cwd. <paramref name="canServe"/> (default:
    /// an index exists AND a CodeCompass server is serving the project) - with no server connected the agent has no
    /// search_code, so Grep runs. An unreadable payload, or any failure deciding, keeps the redirect; this never throws.</summary>
    public static bool ShouldRedirectGrep(string hookInput, string? projectDir, Func<string, bool>? canServe = null)
    {
        try
        {
            using var doc = JsonDocument.Parse(hookInput);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object ||
                !input.TryGetProperty("pattern", out var pat) || pat.ValueKind != JsonValueKind.String)
                return true;

            foreach (var opt in input.EnumerateObject())
            {
                if (!RedirectableOptions.Contains(opt.Name)) return false;
                if (opt.Name == "multiline" && opt.Value.ValueKind == JsonValueKind.True) return false;
                if (opt.Name == "output_mode" && opt.Value.GetString() is not ("content" or "files_with_matches")) return false;
            }

            var pattern = pat.GetString()!;
            if (string.IsNullOrWhiteSpace(pattern) || IsRegex(pattern)) return false;
            if (LiteralLength(pattern) < TrigramIndex.MinQueryLength) return false; // search_code refuses it: only Grep can run it

            string? cwd = root.TryGetProperty("cwd", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var project = !string.IsNullOrWhiteSpace(projectDir) ? projectDir! : cwd;
            if (string.IsNullOrWhiteSpace(project)) return true;
            var baseDir = string.IsNullOrWhiteSpace(cwd) ? project : cwd!;

            // The scope Grep will search: its path (relative to the cwd, ~ = home), else the cwd itself. search_code
            // has no folder or file scope, so only the whole project is an equivalent search.
            string scope = input.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String &&
                           !string.IsNullOrWhiteSpace(p.GetString())
                ? Path.Combine(baseDir, ExpandHome(p.GetString()!))
                : baseDir;
            if (!PathSafety.SameDir(scope, project)) return false;

            return (canServe ?? Served)(PathSafety.NormalizeDir(project));
        }
        catch
        {
            return true;
        }
    }

    private static bool Served(string project) => RepositoryIndexer.HasIndex(project) && ServerLiveness.IsServed(project);

    // "~" and "~/..." name the home directory (Grep expands them); anything else is returned as-is.
    private static string ExpandHome(string path)
    {
        if (path == "~") return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (path.StartsWith("~/", StringComparison.Ordinal) || path.StartsWith("~\\", StringComparison.Ordinal))
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), path[2..]);
        return path;
    }

    // A pattern search_code can't express. A bare '.' doesn't count (the literal hit is what was meant), nor does an
    // escaped punctuation character; a letter escape (\b, \w, \d, \s ...), ripgrep's \< \> word boundaries, or a
    // trailing lone backslash do.
    private static bool IsRegex(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                if (i + 1 >= pattern.Length) return true;
                char next = pattern[i + 1];
                if (char.IsLetterOrDigit(next) || next is '<' or '>') return true;
                i++;
                continue;
            }
            if (c is '|' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '^' or '$') return true;
        }
        return false;
    }

    // The length of the literal text a non-regex pattern searches for: an escape (\( \. ...) is one character, the
    // character search_code would be given.
    private static int LiteralLength(string pattern)
    {
        int n = 0;
        for (int i = 0; i < pattern.Length; i++, n++)
            if (pattern[i] == '\\' && i + 1 < pattern.Length) i++;
        return n;
    }

    /// <summary>PreToolUse payload that denies the tool call and redirects to CodeCompass.</summary>
    public static string DenySearch() => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "PreToolUse",
            permissionDecision = "deny",
            permissionDecisionReason = DenyReason,
        }
    });

    /// <summary>SessionStart payload that injects the CodeCompass preference into context.</summary>
    public static string SessionContext() => JsonSerializer.Serialize(new
    {
        hookSpecificOutput = new
        {
            hookEventName = "SessionStart",
            additionalContext = SessionText,
        }
    });
}
