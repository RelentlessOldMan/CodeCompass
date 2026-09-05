using System.Text.Json;

namespace CodeCompass.Core.Hooks;

/// <summary>
/// JSON payloads emitted by the Claude Code hooks the plugin ships. Kept here (not in
/// the CLI's top-level program) so they're unit-testable.
/// </summary>
public static class HookPayloads
{
    private const string DenyReason =
        "CodeCompass is the required, indexed code-search tool for this workspace. " +
        "Use the CodeCompass MCP tools instead of Grep/Glob: search_code (literal text), " +
        "find_definition (go-to-definition), find_references (semantic for C#/C++), and " +
        "search_symbols. They return precise file:line:col results and cost far fewer tokens " +
        "than grepping or reading whole files. Use Read to open a specific known file. " +
        "(Grep/Glob can be re-enabled by setting CODECOMPASS_ENFORCE=0.)";

    private const string SessionText =
        "CodeCompass is available for this workspace via MCP. For any code search or navigation, " +
        "prefer the CodeCompass tools - search_code, find_definition, find_references, search_symbols - " +
        "over Grep/Glob or reading whole files. They return precise file:line:col ranges and use far " +
        "fewer tokens; find_references is semantic for C# and C/C++. The index auto-updates as files change.";

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
