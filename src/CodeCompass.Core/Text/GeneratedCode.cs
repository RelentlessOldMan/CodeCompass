namespace CodeCompass.Core.Text;

/// <summary>Tool-generated source by file-name convention (WinForms/WPF/resx designers, XAML/source-generator
/// output). Such files repeat the same members in every project - one <c>ResourceManager</c> per Resources.Designer.cs -
/// so symbol search ranks them after hand-written code rather than letting them crowd it out at the cap.</summary>
public static class GeneratedCode
{
    private static readonly string[] Suffixes =
        { ".designer.cs", ".designer.vb", ".g.cs", ".g.i.cs", ".generated.cs" };

    public static bool IsGeneratedPath(string path)
    {
        foreach (var s in Suffixes)
            if (path.EndsWith(s, System.StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
