using System.Text;

namespace CodeCompass.Core.Symbols;

/// <summary>
/// An index of definitions supporting exact-name lookup (for find_definition) and
/// prefix search (for symbol navigation). Persisted with a shared path table so the
/// many symbols per file don't repeat the path string.
/// </summary>
public sealed class SymbolIndex
{
    private const uint Magic = 0x584D5343; // "CSMX"
    private const int Version = 1;

    private readonly List<Symbol> _symbols = new();
    private readonly Dictionary<string, List<int>> _byName = new(StringComparer.Ordinal);

    public int Count => _symbols.Count;

    public void Add(Symbol symbol)
    {
        int id = _symbols.Count;
        _symbols.Add(symbol);
        if (!_byName.TryGetValue(symbol.Name, out var list))
        {
            list = new List<int>();
            _byName[symbol.Name] = list;
        }
        list.Add(id);
    }

    public static SymbolIndex Build(IEnumerable<Symbol> symbols)
    {
        var idx = new SymbolIndex();
        foreach (var s in symbols) idx.Add(s);
        return idx;
    }

    /// <summary>Exact-name matches (case-sensitive), for go-to-definition.</summary>
    public IReadOnlyList<Symbol> FindByName(string name) =>
        _byName.TryGetValue(name, out var ids)
            ? ids.Select(i => _symbols[i]).ToList()
            : Array.Empty<Symbol>();

    /// <summary>Case-insensitive substring match over symbol names, for navigation.</summary>
    public IReadOnlyList<Symbol> Find(string queryText, int max = 200)
    {
        var results = new List<Symbol>();
        foreach (var s in _symbols)
        {
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
        return idx;
    }
}
