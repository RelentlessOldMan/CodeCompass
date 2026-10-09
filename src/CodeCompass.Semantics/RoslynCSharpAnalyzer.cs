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
            _clashByFile.Clear();       // per-file answers belong to this model; a rebuild recomputes them
            _conditionalByFile.Clear();
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

    // C# files whose own project gets NONE of the global usings injected for the SDK projects (a legacy project with no
    // ImplicitUsings next to SDK ones - one compilation means the usings apply to every file). Empty when nothing was
    // injected. See UsingsClash.
    private readonly HashSet<string> _outsideImplicitUsings = new(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyList<string>? _declarationClash;

    /// <summary>Where the injected global usings made a name ambiguous (CS0104) in a file whose own project never had them -
    /// e.g. a legacy project's own <c>Task</c> class next to SDK projects' <c>System.Threading.Tasks</c>. A use bound to
    /// the ambiguous name becomes an error type, so references through it are invisible to the semantic pass.
    /// <c>Declarations</c>: files where the clash is in a declaration (a field, signature or base type) - its effect
    /// reaches other files, so the whole C# pass is incomplete. <c>Files</c>: of <paramref name="candidateFullPaths"/>,
    /// those with a clash anywhere - backfill these by name. Both empty unless the repo mixes the two project kinds.</summary>
    public (IReadOnlyList<string> Declarations, IReadOnlyList<string> Files) UsingsClash(IEnumerable<string> candidateFullPaths,
        System.Threading.CancellationToken ct = default)
    {
        var (solution, _) = EnsureBuilt(ct);
        List<string> outside;
        lock (_gate) outside = _outsideImplicitUsings.ToList();
        if (outside.Count == 0) return (Array.Empty<string>(), Array.Empty<string>());

        bool Clashes(string path, bool declarationsOnly)
        {
            var id = solution.GetDocumentIdsWithFilePath(path).FirstOrDefault();
            var model = id is null ? null : solution.GetDocument(id)?.GetSemanticModelAsync(ct).GetAwaiter().GetResult();
            if (model is null) return false;
            var diags = declarationsOnly ? model.GetDeclarationDiagnostics(cancellationToken: ct) : model.GetDiagnostics(cancellationToken: ct);
            return diags.Any(d => d.Id == "CS0104");
        }

        IReadOnlyList<string>? declarations;
        lock (_gate) declarations = _declarationClash;
        if (declarations is null)
        {
            declarations = outside.Where(p => Clashes(p, declarationsOnly: true)).OrderBy(p => p, StringComparer.Ordinal).ToList();
            lock (_gate) _declarationClash ??= declarations;
        }
        var outsideSet = new HashSet<string>(outside, StringComparer.OrdinalIgnoreCase);
        bool FileClashes(string p)
        {
            lock (_gate) if (_clashByFile.TryGetValue(p, out var known)) return known;
            Interlocked.Increment(ref _clashFileChecks);
            bool clash = Clashes(p, declarationsOnly: false);
            lock (_gate) _clashByFile[p] = clash;
            return clash;
        }
        var files = candidateFullPaths.Where(outsideSet.Contains).Distinct(StringComparer.OrdinalIgnoreCase)
                                      .Where(FileClashes).OrderBy(p => p, StringComparer.Ordinal).ToList();
        return (declarations, files);
    }

    /// <summary>Per-file clash checks (a bind + GetDiagnostics each) run so far (test hook).</summary>
    internal int ClashFileChecks => Volatile.Read(ref _clashFileChecks);
    private int _clashFileChecks;
    // Per-file answers for this analyzer's lifetime (it's dropped when its sources change): the clash check binds the file,
    // and the #if check scans its text, so neither is repeated on every query.
    private readonly Dictionary<string, bool> _clashByFile = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, bool> _conditionalByFile = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The candidates that use conditional compilation, judged on the text this analyzer parsed (what Roslyn
    /// can't see into), so no candidate is re-read from disk on each query. A candidate the analyzer doesn't hold (it
    /// was unreadable at build) is read from disk as <see cref="SemanticCoverage.CSharpConditionalFiles"/> does.</summary>
    public IReadOnlyList<string> ConditionalFiles(IEnumerable<string> candidateFullPaths, System.Threading.CancellationToken ct = default)
    {
        var (solution, _) = EnsureBuilt(ct);
        var hits = new List<string>();
        foreach (var p in candidateFullPaths)
        {
            if (!SemanticCoverage.IsCSharp(p)) continue;
            bool conditional;
            lock (_gate)
                if (_conditionalByFile.TryGetValue(p, out conditional)) { if (conditional) hits.Add(p); continue; }
            var id = solution.GetDocumentIdsWithFilePath(p).FirstOrDefault();
            if (id is null)
            {
                hits.AddRange(SemanticCoverage.CSharpConditionalFiles(new[] { p })); // not in the model: disk, uncached
                continue;
            }
            var text = solution.GetDocument(id)!.GetTextAsync(ct).GetAwaiter().GetResult();
            conditional = SemanticCoverage.HasCSharpConditionalCompilation(text.ToString());
            lock (_gate) _conditionalByFile[p] = conditional;
            if (conditional) hits.Add(p);
        }
        return hits;
    }

    /// <summary>Whole-solution Roslyn reference searches run so far (test hook: the cost driver of FindReferences).</summary>
    internal int ReferenceSearches => Volatile.Read(ref _referenceSearches);
    private int _referenceSearches;

    /// <summary>SemanticModels the last FindReferences call held alive across its searches (test hook; 0 = none held).</summary>
    internal int LastPinnedModels { get; private set; }

    /// <summary>Called after each reference search with the solution searched (test hook: lets a test force a GC between
    /// searches and check what survived).</summary>
    internal Action<Solution>? AfterSearch { get; set; }

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
        var declarations = FindDeclarations(project, name, ct).Concat(PositionalRecordProperties(project, name, ct)).ToList();

        // Roslyn keeps a document's SemanticModel - and the binding done through it - only weakly, so between two searches
        // the GC takes it and the next search re-binds every candidate document from scratch. With several searches (Roslyn's
        // ~100 unrelated GetEnumerator, each re-binding every foreach) the repeat was about half of a 514 s query; held for
        // the call, later searches reuse the binding (514 s -> 278 s, peak memory unchanged). Held whenever more than one
        // declaration (source or framework) is found - cheap, as binding stays lazy; a lone declaration has nothing to share.
        var pinned = declarations.Count > 1
            ? project.Documents.Select(d => d.GetSemanticModelAsync(ct).GetAwaiter().GetResult()).ToList()
            : null;
        LastPinnedModels = pinned?.Count ?? 0;

        foreach (var symbol in declarations)
        {
            if (covered.Contains(symbol)) continue;
            Interlocked.Increment(ref _referenceSearches);
            var referenced = SymbolFinder.FindReferencesAsync(symbol, solution, ct).GetAwaiter().GetResult();
            AfterSearch?.Invoke(solution);
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
        GC.KeepAlive(pinned);
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
            var text = doc.GetTextAsync(ct).GetAwaiter().GetResult();
            if (!ContainsBoth(text, name, "record")) continue;
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

    // Does the document's text contain both strings (ordinal)? Scanned in fixed-size chunks straight from the SourceText -
    // ToString() on every document copied the whole C# corpus into new strings on every find_references. Consecutive
    // chunks overlap by the longer needle's length - 1, so a match across a chunk boundary is still seen.
    internal static bool ContainsBoth(SourceText text, string a, string b)
    {
        const int Chunk = 64 * 1024;
        int overlap = Math.Max(a.Length, b.Length) - 1;
        var buf = System.Buffers.ArrayPool<char>.Shared.Rent(Chunk + overlap);
        try
        {
            bool hasA = a.Length == 0, hasB = b.Length == 0;
            for (int start = 0; start < text.Length && !(hasA && hasB); start += Chunk)
            {
                int len = Math.Min(Chunk + overlap, text.Length - start);
                text.CopyTo(start, buf, 0, len);
                ReadOnlySpan<char> span = buf.AsSpan(0, len);
                hasA = hasA || span.IndexOf(a.AsSpan(), StringComparison.Ordinal) >= 0;
                hasB = hasB || span.IndexOf(b.AsSpan(), StringComparison.Ordinal) >= 0;
            }
            return hasA && hasB;
        }
        finally { System.Buffers.ArrayPool<char>.Shared.Return(buf); }
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
            // Which project dirs (.csproj) and props dirs (.props, applying to every project below) contribute global
            // usings, and every .cs added - to find the files whose own project gets none (see UsingsClash).
            var projectDirHasUsings = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
            var propsDirsWithUsings = new List<string>();
            var csFiles = new List<string>();
            foreach (var root in _roots)
            foreach (var file in walker.Walk(root))
            {
                if (IsMsBuildProjectFile(file.RelativePath))
                {
                    var own = new HashSet<string>(StringComparer.Ordinal);
                    try { CollectGlobalUsings(File.ReadAllText(file.FullPath), own); } catch { /* best effort */ }
                    globalUsings.UnionWith(own);
                    var fileDir = Path.GetDirectoryName(file.FullPath)!;
                    if (file.RelativePath.EndsWith(".csproj", StringComparison.OrdinalIgnoreCase))
                        projectDirHasUsings[fileDir] = (projectDirHasUsings.TryGetValue(fileDir, out var had) && had) || own.Count > 0;
                    else if (own.Count > 0)
                        propsDirsWithUsings.Add(fileDir);
                    continue;
                }
                if (!file.RelativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) continue;
                if (ct.IsCancellationRequested) { workspace.Dispose(); ct.ThrowIfCancellationRequested(); }
                string text;
                try { text = File.ReadAllText(file.FullPath); }
                catch { unreadable.Add(file.FullPath); continue; } // disclosed via UnreadableFiles, never silently "covered"

                // Key documents by ABSOLUTE path so two roots with the same relative path (e.g. both have
                // src/App.cs) don't collide; ToLocation maps the path back to its owning root for display.
                csFiles.Add(file.FullPath);
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
            _outsideImplicitUsings.Clear();
            _declarationClash = null;
            _clashByFile.Clear();
            _conditionalByFile.Clear();
            if (globalUsings.Count > 0)
                foreach (var cs in csFiles)
                    if (OwnProjectLacksUsings(cs, projectDirHasUsings, propsDirsWithUsings)) _outsideImplicitUsings.Add(cs);
            return (solution, solution.GetProject(projectId)!);
        }
    }

    // The file's own project is its nearest ancestor dir holding a .csproj. It lacks the injected usings when that project
    // declares none and no .props at or above the project's dir supplies any. A file under no project is left alone
    // (nothing says what it would have had).
    private static bool OwnProjectLacksUsings(string csFile, Dictionary<string, bool> projectDirHasUsings, List<string> propsDirsWithUsings)
    {
        for (var d = Path.GetDirectoryName(csFile); !string.IsNullOrEmpty(d); d = Path.GetDirectoryName(d))
            if (projectDirHasUsings.TryGetValue(d, out var has))
                return !has && !propsDirsWithUsings.Any(p => CodeCompass.Core.Storage.PathSafety.IsUnderOrEqual(d, p));
        return false;
    }

    /// <summary>Could a change to any of these paths alter what this analyzer read? It reads <c>.cs</c> sources and the
    /// <c>.csproj</c>/<c>.props</c> files for global usings. Only an EXISTING file of another type is safe to ignore: a
    /// directory (a folder rename is one event, with no per-file events, and names like MyApp.Core look like files) or
    /// a path that's gone (a deleted or renamed-away folder) may have held .cs files, and a dotfile may be config.</summary>
    public static bool MayAffect(IEnumerable<string> changedPaths)
    {
        foreach (var p in changedPaths)
        {
            var path = p.TrimEnd('/', '\\');
            var name = Path.GetFileName(path);
            if (name.EndsWith(".cs", StringComparison.OrdinalIgnoreCase) || IsMsBuildProjectFile(name) ||
                name.StartsWith('.') || !File.Exists(path))
                return true;
        }
        return false;
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
