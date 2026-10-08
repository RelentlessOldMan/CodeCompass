using System.Text.Json;

namespace CodeCompass.Core.Hooks;

/// <summary>
/// JSON payloads emitted by the Claude Code hooks the plugin ships. Kept here (not in
/// the CLI's top-level program) so they're unit-testable.
/// </summary>
public static class HookPayloads
{
    // Only Grep (content search) is redirected: CodeCompass has no file-NAME search, so blocking Glob left the agent
    // a dead end it had to escape through a shell command. For the same reason a regex or a path outside the
    // workspace goes through to Grep (see ShouldRedirectGrep).
    private const string DenyReason =
        "CodeCompass is the indexed code-search tool for this workspace. Instead of Grep use: search_code " +
        "(literal text), find_definition, find_references (semantic for C#; by name elsewhere), find_callees, search_symbols - " +
        "precise file:line:col results for far fewer tokens. Use Read for a known file and Glob for file-NAME " +
        "patterns. A regex pattern or a path outside this workspace still goes to Grep. (Disable with CODECOMPASS_ENFORCE=0.)";

    private const string SessionText =
        "CodeCompass is available for this workspace via MCP. For code search and navigation prefer its tools - " +
        "search_code, find_definition, find_references, find_callees, search_symbols - over Grep or reading whole " +
        "files: precise file:line:col ranges for far fewer tokens; find_references is semantic for C#, and matches " +
        "other languages (C/C++ included) by name, so same-named symbols are listed together. " +
        "The index auto-updates as files change.";

    /// <summary>Whether a Grep call (the PreToolUse hook's stdin JSON) should be redirected to CodeCompass: only a
    /// literal pattern searched inside the workspace. A regex, a multiline search or a path outside the workspace has
    /// no CodeCompass equivalent and goes through. An unreadable payload keeps the redirect.</summary>
    public static bool ShouldRedirectGrep(string hookInput)
    {
        try
        {
            using var doc = JsonDocument.Parse(hookInput);
            if (!doc.RootElement.TryGetProperty("tool_input", out var input) || input.ValueKind != JsonValueKind.Object)
                return true;
            if (input.TryGetProperty("multiline", out var ml) && ml.ValueKind == JsonValueKind.True) return false;
            if (input.TryGetProperty("pattern", out var pat) && pat.ValueKind == JsonValueKind.String && IsRegex(pat.GetString()!))
                return false;
            if (input.TryGetProperty("path", out var path) && path.ValueKind == JsonValueKind.String &&
                doc.RootElement.TryGetProperty("cwd", out var cwd) && cwd.ValueKind == JsonValueKind.String &&
                !IsUnder(path.GetString()!, cwd.GetString()!))
                return false;
            return true;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NotSupportedException or PathTooLongException)
        {
            return true;
        }
    }

    // A pattern search_code can't express. A bare '.' doesn't count (the literal hit is what was meant), nor does an
    // escaped punctuation character; a letter escape (\b, \w, \d, \s ...) does.
    private static bool IsRegex(string pattern)
    {
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\\')
            {
                if (i + 1 < pattern.Length && char.IsLetterOrDigit(pattern[i + 1])) return true;
                i++;
                continue;
            }
            if (c is '|' or '*' or '+' or '?' or '(' or ')' or '[' or ']' or '{' or '}' or '^' or '$') return true;
        }
        return false;
    }

    private static bool IsUnder(string path, string root)
    {
        var full = Path.GetFullPath(Path.Combine(root, path)).TrimEnd('\\', '/');
        var rootFull = Path.GetFullPath(root).TrimEnd('\\', '/');
        var cmp = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        return full.Equals(rootFull, cmp) ||
               full.StartsWith(rootFull + Path.DirectorySeparatorChar, cmp) ||
               full.StartsWith(rootFull + '/', cmp);
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
