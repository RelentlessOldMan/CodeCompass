using System.Security.Cryptography;
using System.Text;

namespace CodeCompass.Core.Changes;

/// <summary>The set of paths that differ between two snapshots.</summary>
public sealed class MerkleDiff
{
    public List<string> Added { get; } = new();
    public List<string> Removed { get; } = new();
    public List<string> Modified { get; } = new();

    public bool IsEmpty => Added.Count == 0 && Removed.Count == 0 && Modified.Count == 0;
    public int Count => Added.Count + Removed.Count + Modified.Count;
}

/// <summary>
/// A content-addressed tree over a snapshot. Each directory's hash derives from its
/// children, so two trees can be diffed by comparing hashes and descending only into
/// branches that differ - the whole point being that an unchanged subtree costs one
/// hash comparison, not a full walk. This is the change-detection core (the same idea
/// Cursor uses for incremental indexing).
/// </summary>
public sealed class MerkleTree
{
    private sealed class Node
    {
        public SortedDictionary<string, Node> Dirs { get; } = new(StringComparer.Ordinal);
        public SortedDictionary<string, string> Files { get; } = new(StringComparer.Ordinal); // name -> contentHash
        public string Hash { get; set; } = "";
    }

    private readonly Node _root;

    public string RootHash => _root.Hash;

    private MerkleTree(Node root) => _root = root;

    public static MerkleTree Build(IReadOnlyDictionary<string, FileState> snapshot)
    {
        var root = new Node();
        foreach (var (relPath, state) in snapshot)
        {
            var parts = relPath.Split('/');
            var node = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!node.Dirs.TryGetValue(parts[i], out var child))
                {
                    child = new Node();
                    node.Dirs[parts[i]] = child;
                }
                node = child;
            }
            node.Files[parts[^1]] = state.ContentHash;
        }
        ComputeHash(root);
        return new MerkleTree(root);
    }

    private static void ComputeHash(Node node)
    {
        var sb = new StringBuilder();
        foreach (var (name, child) in node.Dirs)
        {
            ComputeHash(child);
            sb.Append('D').Append(name).Append(':').Append(child.Hash).Append('\n');
        }
        foreach (var (name, h) in node.Files)
            sb.Append('F').Append(name).Append(':').Append(h).Append('\n');
        node.Hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    /// <summary>Paths that were added/removed/modified going from <paramref name="other"/> to this tree.</summary>
    public MerkleDiff Diff(MerkleTree other)
    {
        var diff = new MerkleDiff();
        DiffNodes("", other._root, _root, diff);
        return diff;
    }

    private static void DiffNodes(string prefix, Node? oldN, Node? newN, MerkleDiff diff)
    {
        // Identical subtree: nothing below changed, stop here.
        if (oldN is not null && newN is not null && oldN.Hash == newN.Hash) return;

        var oldFiles = oldN?.Files;
        var newFiles = newN?.Files;

        if (newFiles is not null)
        {
            foreach (var (name, h) in newFiles)
            {
                var p = prefix.Length == 0 ? name : prefix + "/" + name;
                if (oldFiles is null || !oldFiles.TryGetValue(name, out var oh)) diff.Added.Add(p);
                else if (oh != h) diff.Modified.Add(p);
            }
        }
        if (oldFiles is not null)
        {
            foreach (var (name, _) in oldFiles)
            {
                if (newFiles is null || !newFiles.ContainsKey(name))
                    diff.Removed.Add(prefix.Length == 0 ? name : prefix + "/" + name);
            }
        }

        var dirNames = new SortedSet<string>(StringComparer.Ordinal);
        if (oldN is not null) foreach (var k in oldN.Dirs.Keys) dirNames.Add(k);
        if (newN is not null) foreach (var k in newN.Dirs.Keys) dirNames.Add(k);

        foreach (var name in dirNames)
        {
            Node? on = null, nn = null;
            oldN?.Dirs.TryGetValue(name, out on);
            newN?.Dirs.TryGetValue(name, out nn);
            if (on is not null && nn is not null && on.Hash == nn.Hash) continue; // skip identical subtree
            DiffNodes(prefix.Length == 0 ? name : prefix + "/" + name, on, nn, diff);
        }
    }
}
