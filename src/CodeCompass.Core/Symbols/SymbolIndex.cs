using System.Text;

namespace CodeCompass.Core.Symbols;

/// <summary>
/// An index of definitions supporting exact-name lookup (find_definition) and
/// case-insensitive substring search. Supports incremental updates by path: removing a
/// path tombstones its symbols (queries skip tombstones); re-adding appends fresh ones.
/// </summary>
public sealed class SymbolIndex
{
    private const uint Magic = 0x584D5343; // "CSMX"
    private const int Version = 2;

    private readonly List<Symbol> _symbols = new();
    private readonly Dictionary<string, List<int>> _byName = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<int>> _byPath = new(StringComparer.Ordinal);
    private readonly HashSet<int> _removed = new();

    public int Count => _symbols.Count - _removed.Count;

    public void Add(Symbol symbol)
    {
        int id = _symbols.Count;
        _symbols.Add(symbol);

        if (!_byName.TryGetValue(symbol.Name, out var byName))
        {
            byName = new List<int>();
            _byName[symbol.Name] = byName;
        }
        byName.Add(id);

        if (!_byPath.TryGetValue(symbol.RelativePath, out var byPath))
        {
            byPath = new List<int>();
            _byPath[symbol.RelativePath] = byPath;
        }
        byPath.Add(id);
    }

    /// <summary>Tombstone all symbols currently attributed to a path.</summary>
    public void RemovePath(string relativePath)
    {
        if (_byPath.TryGetValue(relativePath, out var ids))
        {
            foreach (var id in ids) _removed.Add(id);
            _byPath.Remove(relativePath);
        }
    }

    public void AddForPath(string relativePath, IEnumerable<Symbol> symbols)
    {
        foreach (var s in symbols) Add(s);
    }

    public static SymbolIndex Build(IEnumerable<Symbol> symbols)
    {
        var idx = new SymbolIndex();
        foreach (var s in symbols) idx.Add(s);
        return idx;
    }

    /// <summary>Exact-name matches (case-sensitive), for go-to-definition.</summary>
    public IReadOnlyList<Symbol> FindByName(string name)
    {
        var result = new List<Symbol>();
        if (_byName.TryGetValue(name, out var ids))
            foreach (var id in ids)
                if (!_removed.Contains(id))
                    result.Add(_symbols[id]);
        return result;
    }

    /// <summary>Case-insensitive substring match over symbol names, for navigation.</summary>
    public IReadOnlyList<Symbol> Find(string queryText, int max = 200)
    {
        var results = new List<Symbol>();
        for (int id = 0; id < _symbols.Count; id++)
        {
            if (_removed.Contains(id)) continue;
            var s = _symbols[id];
            if (s.Name.Contains(queryText, StringComparison.OrdinalIgnoreCase))
            {
                results.Add(s);
                if (results.Count >= max) break;
            }
        }
        return results;
    }

    public void Save(Stream stream)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(Version);

        var pathIds = new Dictionary<string, int>(StringComparer.Ordinal);
        var paths = new List<string>();
        foreach (var s in _symbols)
        {
            if (!pathIds.ContainsKey(s.RelativePath))
            {
                pathIds[s.RelativePath] = paths.Count;
                paths.Add(s.RelativePath);
            }
        }

        w.Write(paths.Count);
        foreach (var p in paths) w.Write(p);

        w.Write(_symbols.Count);
        foreach (var s in _symbols)
        {
            w.Write(s.Name);
            w.Write((byte)s.Kind);
            w.Write(pathIds[s.RelativePath]);
            w.Write(s.Line);
            w.Write(s.Column);
        }

        w.Write(_removed.Count);
        foreach (var id in _removed) w.Write(id);
    }

    public static SymbolIndex Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a CodeCompass symbol file");
        int version = r.ReadInt32();
        if (version != Version) throw new InvalidDataException($"unsupported symbol index version {version}");

        int pathCount = r.ReadInt32();
        var paths = new string[pathCount];
        for (int i = 0; i < pathCount; i++) paths[i] = r.ReadString();

        int symbolCount = r.ReadInt32();
        var idx = new SymbolIndex();
        for (int i = 0; i < symbolCount; i++)
        {
            var name = r.ReadString();
            var kind = (SymbolKind)r.ReadByte();
            var path = paths[r.ReadInt32()];
            var line = r.ReadInt32();
            var col = r.ReadInt32();
            idx.Add(new Symbol(name, kind, path, line, col));
        }

        int removedCount = r.ReadInt32();
        for (int i = 0; i < removedCount; i++) idx._removed.Add(r.ReadInt32());
        // Drop tombstoned ids from the live per-path map so future RemovePath is accurate.
        idx.PruneRemovedFromByPath();

        return idx;
    }

    private void PruneRemovedFromByPath()
    {
        if (_removed.Count == 0) return;
        foreach (var key in _byPath.Keys.ToList())
        {
            var live = _byPath[key].Where(id => !_removed.Contains(id)).ToList();
            if (live.Count == 0) _byPath.Remove(key);
            else _byPath[key] = live;
        }
    }
}
