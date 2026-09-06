using System.Text;

namespace CodeCompass.Core.Symbols.Segments;

/// <summary>
/// Accumulates symbols and writes them as one immutable, name-sorted on-disk segment so
/// the symbol index doesn't have to live in RAM. Columnar layout enables binary search by
/// name (find_definition) and a cheap sequential name scan (search_symbols) via mmap.
///
/// Layout (little-endian): header, then name offsets, kinds, pathIds, lines, cols, the
/// UTF-8 name blob (symbol order = name-sorted), path offsets, and the UTF-8 path blob
/// (path-sorted so ContainsPath is binary-searchable).
/// </summary>
public sealed class SymbolSegmentBuilder
{
    internal const uint Magic = 0x59535343; // "CCSY"
    internal const int Version = 1;
    internal const int HeaderSize = 16 + 8 * 8;

    private readonly List<Symbol> _symbols = new();

    public int Count => _symbols.Count;
    public long ApproxBytes { get; private set; }

    public void Add(Symbol s)
    {
        _symbols.Add(s);
        ApproxBytes += (long)s.Name.Length * 2 + s.RelativePath.Length * 2 + 32;
    }

    public void WriteTo(string filePath)
    {
        var syms = _symbols.OrderBy(s => s.Name, StringComparer.Ordinal).ToList();
        int n = syms.Count;

        var paths = syms.Select(s => s.RelativePath).Distinct(StringComparer.Ordinal)
                        .OrderBy(p => p, StringComparer.Ordinal).ToArray();
        var pathId = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < paths.Length; i++) pathId[paths[i]] = i;

        var nameBytes = new byte[n][];
        long nameBlobLen = 0;
        for (int i = 0; i < n; i++) { nameBytes[i] = Encoding.UTF8.GetBytes(syms[i].Name); nameBlobLen += nameBytes[i].Length; }

        var pathBytes = new byte[paths.Length][];
        for (int i = 0; i < paths.Length; i++) pathBytes[i] = Encoding.UTF8.GetBytes(paths[i]);

        long nameOffsetsOff = HeaderSize;
        long kindsOff = nameOffsetsOff + (long)(n + 1) * 4;
        long pathIdsOff = kindsOff + n;
        long linesOff = pathIdsOff + (long)n * 4;
        long colsOff = linesOff + (long)n * 4;
        long nameBlobOff = colsOff + (long)n * 4;
        long pathOffsetsOff = nameBlobOff + nameBlobLen;
        long pathBlobOff = pathOffsetsOff + (long)(paths.Length + 1) * 4;

        using var fs = new FileStream(filePath, FileMode.Create, FileAccess.Write);
        using var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true);

        w.Write(Magic);
        w.Write(Version);
        w.Write(n);
        w.Write(paths.Length);
        w.Write(nameOffsetsOff);
        w.Write(kindsOff);
        w.Write(pathIdsOff);
        w.Write(linesOff);
        w.Write(colsOff);
        w.Write(nameBlobOff);
        w.Write(pathOffsetsOff);
        w.Write(pathBlobOff);

        int acc = 0;
        for (int i = 0; i < n; i++) { w.Write(acc); acc += nameBytes[i].Length; }
        w.Write(acc);

        for (int i = 0; i < n; i++) w.Write((byte)syms[i].Kind);
        for (int i = 0; i < n; i++) w.Write(pathId[syms[i].RelativePath]);
        for (int i = 0; i < n; i++) w.Write(syms[i].Line);
        for (int i = 0; i < n; i++) w.Write(syms[i].Column);
        for (int i = 0; i < n; i++) w.Write(nameBytes[i]);

        int pacc = 0;
        for (int i = 0; i < paths.Length; i++) { w.Write(pacc); pacc += pathBytes[i].Length; }
        w.Write(pacc);
        for (int i = 0; i < paths.Length; i++) w.Write(pathBytes[i]);

        w.Flush();
    }
}
