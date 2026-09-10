using System.Reflection;

namespace CodeCompass.Core.Diagnostics;

/// <summary>The build's version string (e.g. <c>1.0.123+a1b2c3d4</c>), derived from git at build
/// time via Directory.Build.props. Logged at startup and reported by <c>codecompass version</c> so a
/// user's reported version pins the exact commit.</summary>
public static class BuildInfo
{
    public static string Version { get; } =
        typeof(BuildInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
        ?? typeof(BuildInfo).Assembly.GetName().Version?.ToString()
        ?? "unknown";
}
