using CodeCompass.Core.Ignore;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Walking;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Text;

namespace CodeCompass.Semantics;

/// <summary>A semantically-resolved source location: 1-based line/column plus the line text. When the
/// analyzer spans multiple roots (a project plus its linked external roots), <see cref="Root"/> is the
/// absolute root the hit belongs to and <see cref="RelativePath"/> is relative to it; a single-root
/// analyzer leaves Root empty (the path is relative to that one root).</summary>
public readonly record struct SemanticLocation(string RelativePath, int Line, int Column, string LineText, string Root = "");

/// <summary>
/// Precise C# code intelligence via Roslyn. Rather than loading .sln/.csproj through
/// MSBuild (slow and fragile), it builds an in-memory workspace from the repository's
/// own .cs files plus the running framework's reference assemblies. That is enough to
/// resolve symbols defined in the codebase and find their true references - so a match
/// in a comment or string is never counted, unlike lexical search.
///
/// The workspace is built once, lazily, and cached. Rebuild by creating a new instance. It holds the
/// whole solution (every .cs file's text, plus a cached compilation after the first reference search)
/// in memory, so a long-lived server disposes it when idle to reclaim that RAM (see ServerContext).
///
/// It can span several roots (a project plus its linked external roots): all their .cs files go into one
/// compilation, so a call in the project to a type defined in a linked root resolves - true cross-root
/// go-to-references, not a per-root union that would miss the boundary. Each result carries the absolute
/// root it belongs to (see <see cref="SemanticLocation.Root"/>) so callers can address it correctly.
/// </summary>
public sealed class RoslynCSharpAnalyzer : IDisposable
{
    private readonly IReadOnlyList<string> _roots; // absolute; [0] is the primary (project) root
    private readonly object _gate = new();
    private AdhocWorkspace? _workspace;
    private Solution? _solution;
    private ProjectId? _projectId;

    public RoslynCSharpAnalyzer(string root) : this(new[] { root }) { }

    /// <summary>Span multiple roots (project + linked external roots) in one compilation, so references
    /// resolve across the boundary. The first root is treated as primary by callers for path display.</summary>
    public RoslynCSharpAnalyzer(IReadOnlyList<string> roots) =>
        _roots = roots.Select(CodeCompass.Core.Storage.PathSafety.NormalizeDir).ToList();

    /// <summary>Release the in-memory solution/compilation (hundreds of MB to GB on a large repo). Safe
    /// to call while the instance is being discarded; a fresh instance rebuilds lazily on next use.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _workspace?.Dispose();
            _workspace = null;
            _solution = null;
            _projectId = null;
        }
    }

    /// <summary>Definitions of <paramref name="name"/> declared in the C# sources. Test oracle only: find_definition
    /// uses the tree-sitter symbol index - don't wire a tool to this assuming parity.</summary>
    internal IReadOnlyList<SemanticLocation> FindDefinitions(string name, System.Threading.CancellationToken ct = default)
    {
        var (_, project) = EnsureBuilt(ct);
        var result = new List<SemanticLocation>();
        foreach (var symbol in FindDeclarations(project, name, ct))
        {
            ct.ThrowIfCancellationRequested();
            foreach (var loc in symbol.Locations)
                if (loc.IsInSource)
                    result.Add(ToLocation(loc));
        }
        return Canonical(result, int.MaxValue, null);
    }

    /// <summary>C# sources that could not be read when the workspace was built (an editor's exclusive lock, an
    /// access error). They are ABSENT from the compilation, so a reference in one is invisible to the semantic
    /// pass - callers must treat the pass as incomplete (lexical backfill) and name them.</summary>
    public IReadOnlyList<string> UnreadableFiles { get { lock (_gate) return _unreadable.ToList(); } }
    private readonly List<string> _unreadable = new();

    /// <summary>Whole-solution Roslyn reference searches run so far (test hook: the cost driver of FindReferences).</summary>
    internal int ReferenceSearches => Volatile.Read(ref _referenceSearches);
    private int _referenceSearches;

    /// <summary>True (semantic) references to any C# symbol named <paramref name="name"/>. <paramref name="include"/>
    /// (optional) keeps only hits it accepts - applied BEFORE the <paramref name="max"/> cut, so a session focus
    /// can't have its in-scope hits crowded out by out-of-scope ones.</summary>
    public IReadOnlyList<SemanticLocation> FindReferences(string name, int max = 200, System.Threading.CancellationToken ct = default,
        Func<SemanticLocation, bool>? include = null)
    {
        var (solution, project) = EnsureBuilt(ct);
        var all = new List<SemanticLocation>();

        // A search cascades to linked symbols (overrides, the member they override, interface implementations) and returns
        // each one's references under its own Definition. A declaration already returned as a Definition has had its
        // references collected, so searching it again only repeats the work: with every `override Equals` linked through
        // object.Equals, one search per declaration was quadratic (EF Core's 547 Equals ran 20+ minutes).
        var covered = new HashSet<ISymbol>(SymbolEqualityComparer.Default);
        foreach (var symbol in FindDeclarations(project, name, ct).Concat(PositionalRecordProperties(project, name, ct)))
        {
            if (covered.Contains(symbol)) continue;
            Interlocked.Increment(ref _referenceSearches);            var referenced = SymbolFinder.FindReferencesAsync(symbol, solution, ct).GetAwaiter().GetResult();
            foreach (var r in referenced)
            {
                covered.Add(r.Definition);
                foreach (var rl in r.Locations)
                {
                    var loc = rl.Location;
                    if (!loc.IsInSource) continue;
                    if (IsInDocComment(loc)) continue; // <see cref="X"/> resolves as a real reference, but
                                                       // it lives in a comment - excluded so the "ignores
                                                       // comments" contract actually holds.
                    all.Add(ToLocation(loc));
                }
            }
        }
        return Canonical(all, max, include);
    }

    // DETERMINISTIC result: SymbolFinder's enumeration order is not contractual (it works on documents in parallel),
    // so dedup and sort by (root, path, line, column) BEFORE truncating - otherwise identical queries could return
    // different subsets at the cap.
    private static List<SemanticLocation> Canonical(List<SemanticLocation> all, int max, Func<SemanticLocation, bool>? include, bool sort = true)
    {
        var seen = new HashSet<(string, string, int, int)>();
        var kept = all.Where(s => (include is null || include(s)) && seen.Add((s.Root, s.RelativePath, s.Line, s.Column)));
        if (sort)
            kept = kept.OrderBy(s => s.Root, StringComparer.Ordinal)
                       .ThenBy(s => s.RelativePath, StringComparer.Ordinal)
                       .ThenBy(s => s.Line).ThenBy(s => s.Column);
        return kept.Take(max).ToList();
    }

    /// <summary>The in-repository methods/types that the C# method(s) named <paramref name="name"/>
    /// call - resolved SEMANTICALLY, so <c>x.ToString()</c> binds to the one real declaration and
    /// framework/external calls are omitted. That resolution is exactly what a syntactic call graph
    /// cannot do (it returns every same-named overload in the repo); here each result is the callee's
    /// own definition, so an agent can jump straight to the next hop without reading the body.</summary>
    public IReadOnlyList<SemanticLocation> FindCallees(string name, int max = 100, System.Threading.CancellationToken ct = default,
        Func<SemanticLocation, bool>? include = null)
    {
        var (_, project) = EnsureBuilt(ct);
        var result = new List<SemanticLocation>();
        var compilation = project.GetCompilationAsync(ct).GetAwaiter().GetResult();
        if (compilation is null) return result;

        // Body order is kept (it's the call order an agent walks); only the order of same-named declarations is
        // unstable across runs, so visit those in a canonical order.
        foreach (var symbol in FindDeclarations(project, name, ct).OrderBy(DeclKey, StringComparer.Ordinal))
        {
            ct.ThrowIfCancellationRequested(); // bail promptly on shutdown/re-point between symbols
            if (symbol is not IMethodSymbol) continue;
            foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
            {
                var body = syntaxRef.GetSyntax();
                var model = compilation.GetSemanticModel(body.SyntaxTree);
                foreach (var node in body.DescendantNodes())
                {
                    if (node is not (InvocationExpressionSyntax or ObjectCreationExpressionSyntax)) continue;
                    if (model.GetSymbolInfo(node).Symbol is not IMethodSymbol called) continue;
                    // A constructor call's useful target is the type being constructed; otherwise the method.
                    ISymbol target = called.MethodKind == MethodKind.Constructor ? called.ContainingType : called;
                    foreach (var loc in target.Locations)
                    {
                        if (!loc.IsInSource) continue; // in-repo only - drops BCL/framework calls (the noise filter)
                        var s = ToLocation(loc);
                        result.Add(s with { LineText = string.IsNullOrEmpty(s.LineText) ? target.Name : $"{target.Name}  {s.LineText}" });
                    }
                }
            }
        }
        return Canonical(result, max, include, sort: false);
    }

    // A stable key for a declaration: its first source location (path, then position).
    private static string DeclKey(ISymbol s)
    {
        var loc = s.Locations.FirstOrDefault(l => l.IsInSource);
        return loc is null ? s.ToDisplayString() : $"{loc.SourceTree?.FilePath}\u0001{loc.SourceSpan.Start:D10}";
    }

    /// <summary>Callees that <see cref="FindCallees"/> CANNOT see because they sit inside inactive
    /// <c>#if</c>/<c>#elif</c> branches - Roslyn parses those as disabled text, so the semantic walk never
    /// visits them and a <c>#if</c>-guarded call is silently missing (the forward/reverse asymmetry: <c>refs</c>
    /// finds the edge, <c>callees</c> doesn't). We recover them HONESTLY: re-lex each disabled region with
    /// Roslyn (so comments/strings/verbatim are handled for free and only real invocations/constructions are
    /// picked up), then resolve each discovered NAME against the repo. Resolution is by name, not semantic
    /// binding (disabled text can't be bound), so it may surface more than one same-named declaration - callers
    /// MUST label these as "resolved by name" and segregate them from the authoritative semantic list. Empty
    /// when the method has no disabled regions, so callers can gate on conditional compilation and skip the work.</summary>
    public IReadOnlyList<SemanticLocation> FindCalleesInInactiveBranches(string name, int max = 100, System.Threading.CancellationToken ct = default,
        Func<SemanticLocation, bool>? include = null)
    {
        var (_, project) = EnsureBuilt(ct);
        var result = new List<SemanticLocation>();

        // 1. Harvest call-like names from the DISABLED regions of each method named `name`.
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var symbol in FindDeclarations(project, name, ct))
        {
            ct.ThrowIfCancellationRequested();
            if (symbol is not IMethodSymbol) continue;
            foreach (var syntaxRef in symbol.DeclaringSyntaxReferences)
            {
                var decl = syntaxRef.GetSyntax();
                var disabled = new System.Text.StringBuilder();
                foreach (var tr in decl.DescendantTrivia(descendIntoTrivia: true))
                    if (tr.IsKind(SyntaxKind.DisabledTextTrivia)) disabled.Append(tr.ToFullString()).Append('\n');
                if (disabled.Length == 0) continue;

                // Wrap in a block so a sequence of guarded statements parses as one; Roslyn is error-tolerant, so
                // partial fragments still yield their invocation/object-creation nodes (and real comments/strings
                // in the branch never masquerade as calls).
                var block = SyntaxFactory.ParseStatement("{\n" + disabled + "\n}");
                foreach (var node in block.DescendantNodes())
                {
                    string? callee = node switch
                    {
                        InvocationExpressionSyntax inv => SimpleCalleeName(inv.Expression),
                        ObjectCreationExpressionSyntax oc => TypeName(oc.Type),
                        _ => null,
                    };
                    if (!string.IsNullOrEmpty(callee) && callee != name) names.Add(callee!);
                }
            }
        }
        if (names.Count == 0) return result;

        // 2. Resolve each name to its in-repo definition(s). By name only - disabled text can't be semantically
        // bound - so overloads/same-named decls across the repo all surface; that's the labeled caveat.
        foreach (var n in names)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var sym in FindDeclarations(project, n, ct))
            {
                if (sym is not (IMethodSymbol or INamedTypeSymbol)) continue;
                foreach (var loc in sym.Locations)
                {
                    if (!loc.IsInSource) continue; // in-repo only, same noise filter as FindCallees
                    var s = ToLocation(loc);
                    result.Add(s with { LineText = string.IsNullOrEmpty(s.LineText) ? sym.Name : $"{sym.Name}  {s.LineText}" });
                }
            }
        }
        return Canonical(result, max, include);
    }

    // The invoked simple name of a call expression: `Foo()` -> Foo, `x.Foo()` -> Foo. Anything else (e.g. a
    // delegate held in an element access) yields null and is skipped - we only recover plainly-named calls.
    private static string? SimpleCalleeName(ExpressionSyntax expr) => expr switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        MemberAccessExpressionSyntax ma => ma.Name.Identifier.Text,
        _ => null,
    };

    // The simple name of a constructed type: `Foo` / `A.Foo` / `Foo<T>` -> Foo.
    private static string? TypeName(TypeSyntax type) => type switch
    {
        IdentifierNameSyntax id => id.Identifier.Text,
        GenericNameSyntax g => g.Identifier.Text,
        QualifiedNameSyntax q => q.Right.Identifier.Text,
        _ => null,
    };

    // True if the reference sits inside a documentation comment (an XML-doc <see cref="..."/> or the
    // like). Roslyn resolves those to the real symbol, but for a "find usages" answer they are comment
    // mentions, not code that uses the symbol - so we drop them to keep the semantic result honest.
    private static bool IsInDocComment(Location loc)
    {
        var tree = loc.SourceTree;
        if (tree is null) return false;
        var token = tree.GetRoot().FindToken(loc.SourceSpan.Start, findInsideTrivia: true);
        for (var n = token.Parent; n is not null; n = n.Parent)
            if (n is DocumentationCommentTriviaSyntax) return true;
        return false;
    }

    private static IEnumerable<ISymbol> FindDeclarations(Project project, string name, System.Threading.CancellationToken ct = default) =>
        SymbolFinder.FindDeclarationsAsync(project, name, ignoreCase: false, ct).GetAwaiter().GetResult();

    // A positional record member (`record struct FileState(..., string ContentHash)`) is a property SYNTHESIZED from the
    // primary-ctor parameter, and Roslyn's declaration index (FindDeclarations) holds neither - so without this the
    // name resolves to nothing and every `f.ContentHash` use is lost. Text-prefilter the documents (only those
    // mentioning both `record` and the name are parsed), then map each matching positional parameter to its property.
    private static IEnumerable<ISymbol> PositionalRecordProperties(Project project, string name, System.Threading.CancellationToken ct)
    {
        foreach (var doc in project.Documents)
        {
            ct.ThrowIfCancellationRequested();
            var text = doc.GetTextAsync(ct).GetAwaiter().GetResult().ToString();
            if (!text.Contains(name, StringComparison.Ordinal) || !text.Contains("record", StringComparison.Ordinal)) continue;
            var root = doc.GetSyntaxRootAsync(ct).GetAwaiter().GetResult();
            if (root is null) continue;
            SemanticModel? model = null;
            foreach (var rec in root.DescendantNodes().OfType<RecordDeclarationSyntax>())
            {
                if (rec.ParameterList is null || !rec.ParameterList.Parameters.Any(p => p.Identifier.ValueText == name)) continue;
                model ??= doc.GetSemanticModelAsync(ct).GetAwaiter().GetResult();
                if (model?.GetDeclaredSymbol(rec, ct) is not INamedTypeSymbol type) continue;
                foreach (var p in type.GetMembers(name).OfType<IPropertySymbol>())
                    yield return p;
            }
        }
    }

    // Cancellable: the build walks and reads every .cs under every root (minutes over a share) while the caller holds
    // the server's read lock - a re-point must be able to stop it. A cancelled build installs nothing (the solution is
    // only assigned at the end), so the next call starts over cleanly.
    private (Solution Solution, Project Project) EnsureBuilt(System.Threading.CancellationToken ct = default)
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
            var unreadable = new List<string>();

            var walker = new FileWalker(new IgnoreRules());
            var globalUsings = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var root in _roots)
            foreach (var file in walker.Walk(root))
            {
                if (IsMsBuildProjectFile(file.RelativePath))
                {
                    try { CollectGlobalUsings(File.ReadAllText(file.FullPath), globalUsings); } catch { /* best effort */ }
                    continue;
                }
                if (!file.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (ct.IsCancellationRequested) { workspace.Dispose(); ct.ThrowIfCancellationRequested(); }
                string text;
                try { text = File.ReadAllText(file.FullPath); }
                catch { unreadable.Add(file.FullPath); continue; } // disclosed via UnreadableFiles, never silently "covered"

                // Key documents by ABSOLUTE path so two roots with the same relative path (e.g. both have
                // src/App.cs) don't collide; ToLocation maps the path back to its owning root for display.
                var documentId = DocumentId.CreateNewId(projectId);
                solution = solution.AddDocument(DocumentInfo.Create(
                    documentId,
                    name: file.FullPath,
                    filePath: file.FullPath,
                    loader: TextLoader.From(TextAndVersion.Create(
                        SourceText.From(text), VersionStamp.Create(), file.FullPath))));
            }

            // The build injects these as an invisible generated file (obj/*.GlobalUsings.g.cs); without them every type
            // they bring in (List<T>, IReadOnlyDictionary<,>, ...) is an error type here and references through it vanish.
            // One project spans all roots, so the union applies everywhere - extra usings only matter on a name clash.
            if (globalUsings.Count > 0)
                solution = solution.AddDocument(DocumentInfo.Create(
                    DocumentId.CreateNewId(projectId), name: "CodeCompass.GlobalUsings.g.cs",
                    loader: TextLoader.From(TextAndVersion.Create(SourceText.From(
                        string.Concat(globalUsings.Select(u => $"global using global::{u};\n"))), VersionStamp.Create()))));

            _workspace = workspace;
            _solution = solution;
            _projectId = projectId;
            _unreadable.Clear();
            _unreadable.AddRange(unreadable);
            return (solution, solution.GetProject(projectId)!);
        }
    }

    private static bool IsMsBuildProjectFile(string relativePath) =>
        relativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase) ||
        relativePath.EndsWith(".props", StringComparison.OrdinalIgnoreCase);

    // Microsoft.NET.Sdk's <ImplicitUsings> set (the Web/Worker SDKs add ASP.NET/Extensions namespaces whose assemblies
    // aren't referenced here anyway, so they'd bind nothing).
    private static readonly string[] SdkImplicitUsings =
        { "System", "System.Collections.Generic", "System.IO", "System.Linq", "System.Net.Http", "System.Threading", "System.Threading.Tasks" };

    private static readonly System.Text.RegularExpressions.Regex ImplicitUsingsOn =
        new(@"<ImplicitUsings>\s*(enable|true)\s*</ImplicitUsings>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
    // Plain namespace imports only: <Using Include="X" /> without Static/Alias (those need a different directive form).
    private static readonly System.Text.RegularExpressions.Regex UsingItem =
        new(@"<Using\s+Include\s*=\s*""([A-Za-z_][\w.]*)""(?![^>]*\b(?:Static|Alias)\s*=)[^>]*>", System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    internal static void CollectGlobalUsings(string projectXml, ISet<string> into)
    {
        if (ImplicitUsingsOn.IsMatch(projectXml))
            foreach (var u in SdkImplicitUsings) into.Add(u);
        foreach (System.Text.RegularExpressions.Match m in UsingItem.Matches(projectXml))
            into.Add(m.Groups[1].Value);
    }

    // The framework reference set (the running runtime's ~150 TPA assemblies) never changes for the life of
    // the process, but reading each one's metadata via CreateFromFile is not free. A live-watched C# repo
    // disposes+rebuilds this analyzer on every incremental edit, so building the set per rebuild re-read the
    // whole framework on every keystroke-save. Build it ONCE, process-wide: PortableExecutableReference is
    // immutable and safe to share across compilations/workspaces.
    private static readonly Lazy<IReadOnlyList<MetadataReference>> CachedFrameworkRefs = new(BuildFrameworkReferences);

    private static IReadOnlyList<MetadataReference> FrameworkReferences() => CachedFrameworkRefs.Value;

    private static IReadOnlyList<MetadataReference> BuildFrameworkReferences()
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

    private SemanticLocation ToLocation(Location location)
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
            {
                var raw = text.Lines[line].ToString();
                lineText = CodeCompass.Core.Text.LineSnippet.Make(raw, Math.Clamp(column, 0, raw.Length), 1).Text.Trim(); // bounded + scrubbed
            }
        }
        var (root, rel) = OwnerOf(span.Path);
        return new SemanticLocation(rel, line + 1, column + 1, lineText, root);
    }

    // Map a document's absolute path back to the (owning root, forward-slash relative path) pair. For a
    // single-root analyzer this yields exactly the FileWalker relative path (root-relative, '/'-separated),
    // so single-root behaviour is unchanged; Root is empty for the primary root so single-root callers and
    // tests that ignore Root see identical results.
    private (string Root, string Rel) OwnerOf(string fullPath)
    {
        for (int i = 0; i < _roots.Count; i++)
        {
            if (!PathSafety.IsUnderOrEqual(fullPath, _roots[i])) continue;
            var rel = Path.GetRelativePath(_roots[i], fullPath).Replace('\\', '/');
            return (i == 0 ? "" : _roots[i], rel); // primary root -> empty (path is repo-relative)
        }
        return ("", fullPath.Replace('\\', '/')); // unexpected: show as-is under the primary
    }
}
