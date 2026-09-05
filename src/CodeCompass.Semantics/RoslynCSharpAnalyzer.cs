using CodeCompass.Core.Ignore;
using CodeCompass.Core.Walking;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace CodeCompass.Semantics;

/// <summary>A semantically-resolved source location: 1-based line/column plus the line text.</summary>
public readonly record struct SemanticLocation(string RelativePath, int Line, int Column, string LineText);

/// <summary>
/// Precise C# code intelligence via Roslyn. Rather than loading .sln/.csproj through
/// MSBuild (slow and fragile), it builds an in-memory workspace from the repository's
/// own .cs files plus the running framework's reference assemblies. That is enough to
/// resolve symbols defined in the codebase and find their true references - so a match
/// in a comment or string is never counted, unlike lexical search.
///
/// The workspace is built once, lazily, and cached. Rebuild by creating a new instance.
/// </summary>
public sealed class RoslynCSharpAnalyzer
{
    private readonly string _root;
    private readonly object _gate = new();
    private Solution? _solution;
    private ProjectId? _projectId;

    public RoslynCSharpAnalyzer(string root) => _root = Path.GetFullPath(root);

    /// <summary>Definitions of <paramref name="name"/> declared in the C# sources.</summary>
    public IReadOnlyList<SemanticLocation> FindDefinitions(string name)
    {
        var (_, project) = EnsureBuilt();
        var result = new List<SemanticLocation>();
        foreach (var symbol in FindDeclarations(project, name))
            foreach (var loc in symbol.Locations)
                if (loc.IsInSource)
                    result.Add(ToLocation(loc));
        return result;
    }

    /// <summary>True (semantic) references to any C# symbol named <paramref name="name"/>.</summary>
    public IReadOnlyList<SemanticLocation> FindReferences(string name, int max = 200)
    {
        var (solution, project) = EnsureBuilt();
        var result = new List<SemanticLocation>();
        var seen = new HashSet<(string, int, int)>();

        foreach (var symbol in FindDeclarations(project, name))
        {
            var referenced = SymbolFinder.FindReferencesAsync(symbol, solution).GetAwaiter().GetResult();
            foreach (var r in referenced)
            {
                foreach (var rl in r.Locations)
                {
                    var loc = rl.Location;
                    if (!loc.IsInSource) continue;
                    var s = ToLocation(loc);
                    if (seen.Add((s.RelativePath, s.Line, s.Column)))
                    {
                        result.Add(s);
                        if (result.Count >= max) return result;
                    }
                }
            }
        }
        return result;
    }

    private static IEnumerable<ISymbol> FindDeclarations(Project project, string name) =>
        SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: false).GetAwaiter().GetResult();

    private (Solution Solution, Project Project) EnsureBuilt()
    {
        lock (_gate)
        {
            if (_solution is not null && _projectId is not null)
                return (_solution, _solution.GetProject(_projectId)!);

            var workspace = new AdhocWorkspace();
            var projectId = ProjectId.CreateNewId();

            var info = ProjectInfo.Create(
                    projectId, VersionStamp.Create(), "Repo", "Repo", LanguageNames.CSharp)
                .WithMetadataReferences(FrameworkReferences())
                .WithCompilationOptions(new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));

            var solution = workspace.CurrentSolution.AddProject(info);

            var walker = new FileWalker(new IgnoreRules());
            foreach (var file in walker.Walk(_root))
            {
                if (!file.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                string text;
                try { text = File.ReadAllText(file.FullPath); }
                catch { continue; }

                var documentId = DocumentId.CreateNewId(projectId);
                solution = solution.AddDocument(DocumentInfo.Create(
                    documentId,
                    name: file.RelativePath,
                    filePath: file.RelativePath,
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(text), VersionStamp.Create(), file.RelativePath))));
            }

            _solution = solution;
            _projectId = projectId;
            return (solution, solution.GetProject(projectId)!);
        }
    }

    private static IEnumerable<MetadataReference> FrameworkReferences()
    {
        // Reference the whole running framework so binding is as accurate as possible.
        var tpa = AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") as string;
        if (string.IsNullOrEmpty(tpa))
            return new[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) };

        var refs = new List<MetadataReference>();
        foreach (var path in tpa.Split(Path.PathSeparator))
        {
            if (path.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) && File.Exists(path))
            {
                try { refs.Add(MetadataReference.CreateFromFile(path)); }
                catch { /* skip unreadable */ }
            }
        }
        return refs;
    }

    private static SemanticLocation ToLocation(Location location)
    {
        var span = location.GetLineSpan();
        int line = span.StartLinePosition.Line;
        int column = span.StartLinePosition.Character;

        string lineText = "";
        var tree = location.SourceTree;
        if (tree is not null)
        {
            var text = tree.GetText();
            if (line < text.Lines.Count)
                lineText = text.Lines[line].ToString().Trim();
        }
        return new SemanticLocation(span.Path, line + 1, column + 1, lineText);
    }
}
