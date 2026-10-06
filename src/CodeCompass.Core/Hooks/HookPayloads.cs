using System.Text.Json;

namespace CodeCompass.Core.Hooks;

/// <summary>
/// JSON payloads emitted by the Claude Code hooks the plugin ships. Kept here (not in
/// the CLI's top-level program) so they're unit-testable.
/// </summary>
public static class HookPayloads
{
    // Only Grep (content search) is redirected: CodeCompass has no file-NAME search, so blocking Glob left the agent
    // a dead end it had to escape through a shell command.
    private const string DenyReason =
        "CodeCompass is the indexed code-search tool for this workspace. Instead of Grep use: search_code " +
        "(literal text), find_definition, find_references (semantic for C#; by name elsewhere), find_callees, search_symbols - " +
        "precise file:line:col results for far fewer tokens. Use Read for a known file and Glob for file-NAME " +
        "patterns. (Disable with CODECOMPASS_ENFORCE=0.)";

    private const string SessionText =
        "CodeCompass is available for this workspace via MCP. For code search and navigation prefer its tools - " +
        "search_code, find_definition, find_references, find_callees, search_symbols - over Grep or reading whole " +
        "files: precise file:line:col ranges for far fewer tokens; find_references is semantic for C#, and matches " +
        "other languages (C/C++ included) by name, so same-named symbols are listed together. " +
        "The index auto-updates as files change.";

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
