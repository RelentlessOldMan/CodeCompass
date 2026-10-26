using System.Security.Cryptography;
using System.Text;
using CodeCompass.Core.Storage;

namespace CodeCompass.Core.SelfTest;

/// <summary>
/// Names in the report: as they are, or hashed for a report that's shared. The hash is keyed with a salt drawn for this
/// run and never written out, so a reader can't hash a guessed name and look for it; within the run a name keeps its hash.
/// </summary>
internal sealed class ReportNamer
{
    private readonly bool _anonymize;
    private readonly string _root;
    private readonly byte[] _salt = RandomNumberGenerator.GetBytes(16);
    private readonly List<(string From, string To)> _paths = new(); // longest first, so a path under another goes whole

    public ReportNamer(bool anonymize, string root, IReadOnlyList<string> links)
    {
        _anonymize = anonymize;
        _root = root;
        if (!anonymize) return;
        var cacheDir = IndexStore.CacheDirPath(root);
        Scrub(cacheDir, "<index>");
        Scrub(Path.GetDirectoryName(cacheDir), "<cache>");
        foreach (var l in links) Scrub(l, "<link-" + Hash(Path.GetFullPath(l)) + ">");
        Scrub(root, "<repo>");
        Scrub(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "<home>");
        _paths.Sort((a, b) => b.From.Length.CompareTo(a.From.Length));
    }

    // A path in both spellings: a detail may write it with either separator.
    private void Scrub(string? path, string to)
    {
        if (string.IsNullOrEmpty(path)) return;
        path = Path.TrimEndingDirectorySeparator(path);
        if (path.Length == 0) return;
        _paths.Add((path, to));
        var slashed = path.Replace('\\', '/');
        if (slashed != path) _paths.Add((slashed, to));
    }

    public string Hash(string s) => Convert.ToHexString(HMACSHA256.HashData(_salt, Encoding.UTF8.GetBytes(s)), 0, 3).ToLowerInvariant();
    public string RepoLabel => "repo-" + Hash(_root);
    public string Sym(string s) => _anonymize ? "sym-" + Hash(s) : s;
    public string File(string p) => _anonymize ? "file-" + Hash(p.Replace('\\', '/')) + Path.GetExtension(p) : p;
    public string Quote(string s) => _anonymize ? "text-" + Hash(s) : s;
    public string Folder(string s) => _anonymize ? "folder-" + Hash(s) : s;

    /// <summary>Text CodeCompass wrote (a health check's name or detail): the repo, its links, the cache and the home folder go.</summary>
    public string Text(string s)
    {
        if (!_anonymize) return s;
        foreach (var (from, to) in _paths) s = s.Replace(from, to, StringComparison.OrdinalIgnoreCase);
        return s;
    }

    /// <summary>Text from outside (a tool's answer, an exception, a log tail) may hold anything the repo does: withheld.</summary>
    public string Raw(string s) => _anonymize ? "(withheld from an anonymized report)" : s;
}
