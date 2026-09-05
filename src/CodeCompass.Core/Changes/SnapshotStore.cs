using System.Text;

namespace CodeCompass.Core.Changes;

/// <summary>Persists a content snapshot (relative path -> FileState) so change detection survives restarts.</summary>
public static class SnapshotStore
{
    private const uint Magic = 0x50414E53; // "SNAP"

    public static void Save(Stream stream, IReadOnlyDictionary<string, FileState> snapshot)
    {
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        w.Write(Magic);
        w.Write(snapshot.Count);
        foreach (var (path, state) in snapshot)
        {
            w.Write(path);
            w.Write(state.Size);
            w.Write(state.MTimeTicks);
            w.Write(state.ContentHash);
        }
    }

    public static Dictionary<string, FileState> Load(Stream stream)
    {
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (r.ReadUInt32() != Magic) throw new InvalidDataException("not a CodeCompass snapshot file");

        int count = r.ReadInt32();
        var result = new Dictionary<string, FileState>(count, StringComparer.Ordinal);
        for (int i = 0; i < count; i++)
        {
            var path = r.ReadString();
            var size = r.ReadInt64();
            var mtime = r.ReadInt64();
            var hash = r.ReadString();
            result[path] = new FileState(size, mtime, hash);
        }
        return result;
    }
}
