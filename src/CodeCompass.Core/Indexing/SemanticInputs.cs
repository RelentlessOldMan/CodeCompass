using CodeCompass.Core.Config;

namespace CodeCompass.Core.Indexing;

/// <summary>The files a C# semantic model reads: <c>.cs</c> sources, the <c>.csproj</c>/<c>.props</c> files it takes global
/// usings from, and the repo's <c>.codecompass.json</c>, whose ignore/keepDirs decide which folders it walks. A change to
/// any other file can't alter what it resolves.</summary>
public static class SemanticInputs
{
    public static bool IsCSharpInput(string path)
    {
        var name = Path.GetFileName(path.TrimEnd('/', '\\'));
        return name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".props", StringComparison.OrdinalIgnoreCase) ||
               name.Equals(CodeCompassConfig.FileName, StringComparison.OrdinalIgnoreCase);
    }
}
