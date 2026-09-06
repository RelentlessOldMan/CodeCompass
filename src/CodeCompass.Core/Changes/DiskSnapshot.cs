using System.Text;
using CodeCompass.Core.Changes.Segments;
using CodeCompass.Core.Diagnostics;

namespace CodeCompass.Core.Changes;

/// <summary>
/// The change-detection ledger (relative path -> <see cref="FileState"/>), kept mostly on
/// disk so it doesn't scale RAM with repo size. State lives in an immutable, memory-mapped,
/// path-sorted base file (<see cref="SnapshotBaseReader"/>); recent edits live in a small
/// in-RAM overlay (upserts) plus a tombstone set (removals) that are persisted to a compact
/// journal. Reads check the overlay/tombstones first, then binary-search the mmap base, so a
/// handful of edits never forces the whole ledger into memory.
///
/// When the overlay grows past a threshold it is folded into a fresh base file (compaction).
/// Base files use monotonic, never-reused numbers (like the trigram/symbol segments) so a
/// rewrite can create a new file while a previous mmap is still held - safe on Windows, where
/// mapped files can't be deleted. Orphaned base files are cleaned up best-effort.
///
/// Not safe for concurrent writers on the same directory; the tool serves one writer per repo
/// (the MCP server or a CLI command), with occasional readers.
/// </summary>
public sealed class DiskSnapshot : IDisposable
{
    private const string ManifestName = "snapshot.manifest";
    private const string JournalName = "snapshot.journal";
    private const string LegacyName = "snapshot.bin";
    private const string BasePattern = "snapshot-*.base";
    private const uint JournalMagic = 0x4A4E5343; // "CSNJ"
    private const int DefaultCompactThreshold = 50_000;

    private readonly string _dir;
    private readonly int _compactThreshold;
    private SnapshotBaseReader? _base;
    private readonly Dictionary<string, FileState> _overlay = new(StringComparer.Ordinal);
    private readonly HashSet<string> _removed = new(StringComparer.Ordinal);
    private int _nextBaseNumber;

    private DiskSnapshot(string dir)
    {
        _dir = dir;
        _compactThreshold = ThresholdFromEnv();
    }

    /// <summary>Open (or start) the snapshot in a cache directory. Always returns an instance
    /// (empty if none exists yet). Migrates a legacy blob snapshot on first open.</summary>
    public static DiskSnapshot Open(string dir)
    {
        System.IO.Directory.CreateDirectory(dir);
        var ds = new DiskSnapshot(dir);
        ds.MigrateLegacyIfNeeded();
        ds.LoadManifestAndBase();
        ds.LoadJournal();
        ds._nextBaseNumber = Math.Max(ds._nextBaseNumber, NextBaseNumber(dir));
        return ds;
    }

    /// <summary>Open only if a snapshot already exists (new-format base or a legacy blob).</summary>
    public static bool TryOpen(string dir, out DiskSnapshot snapshot)
    {
        if (!Exists(dir)) { snapshot = null!; return false; }
        snapshot = Open(dir);
        return true;
    }

    public static bool Exists(string dir) =>
        File.Exists(Path.Combine(dir, ManifestName)) || File.Exists(Path.Combine(dir, LegacyName));

    // ---- Dictionary-like surface (keeps the incremental call sites unchanged) ----

    public bool TryGetValue(string relPath, out FileState state)
    {
        if (_removed.Contains(relPath)) { state = default; return false; }
        if (_overlay.TryGetValue(relPath, out state)) return true;
        if (_base is not null) return _base.TryFind(relPath, out state);
        state = default;
        return false;
    }

    public bool ContainsKey(string relPath) => TryGetValue(relPath, out _);

    public FileState this[string relPath]
    {
        set { _overlay[relPath] = value; _removed.Remove(relPath); }
    }

    /// <summary>Remove a path; returns whether it was present.</summary>
    public bool Remove(string relPath)
    {
        bool existed = ContainsKey(relPath);
        _overlay.Remove(relPath);
        if (_base is not null && _base.Contains(relPath)) _removed.Add(relPath);
        return existed;
    }

    /// <summary>All live paths (streamed; base paths read from mmap on demand).</summary>
    public IEnumerable<string> Keys
    {
        get
        {
            if (_base is not null)
                for (int i = 0; i < _base.Count; i++)
                {
                    var p = _base.GetPath(i);
                    if (!_removed.Contains(p) && !_overlay.ContainsKey(p)) yield return p;
                }
            foreach (var k in _overlay.Keys) yield return k;
        }
    }

    /// <summary>Live paths starting with <paramref name="prefix"/> (base scan uses a sorted range).</summary>
    public IEnumerable<string> KeysWithPrefix(string prefix)
    {
        if (_base is not null)
            for (int i = _base.LowerBound(prefix); i < _base.Count; i++)
            {
                var p = _base.GetPath(i);
                if (!p.StartsWith(prefix, StringComparison.Ordinal)) break;
                if (!_removed.Contains(p) && !_overlay.ContainsKey(p)) yield return p;
            }
        foreach (var k in _overlay.Keys)
            if (k.StartsWith(prefix, StringComparison.Ordinal)) yield return k;
    }

    public int Count
    {
        get
        {
            int c = (_base?.Count ?? 0) - _removed.Count;
            foreach (var k in _overlay.Keys)
                if (_base is null || !_base.Contains(k)) c++;
            return c;
        }
    }

    /// <summary>Pending (unpersisted-to-base) edit count - overlay upserts plus tombstones.</summary>
    public int PendingCount => _overlay.Count + _removed.Count;

    // ---- persistence ----

    /// <summary>Persist pending edits. Writes a small journal, or compacts into a fresh base
    /// once the overlay grows past the threshold.</summary>
    public void Save()
    {
        if (_base is null || PendingCount >= _compactThreshold) Compact();
        else WriteJournal();
    }

    /// <summary>Force a fold of the overlay into a new base file (used by tests and Save()).</summary>
    public void Compact()
    {
        int count = Count;
        long blob = _base?.PathBlobLen ?? 0;
        foreach (var k in _overlay.Keys)
            if (_base is null || !_base.Contains(k)) blob += Encoding.UTF8.GetByteCount(k);
        foreach (var k in _removed) blob -= Encoding.UTF8.GetByteCount(k); // all removed exist in base

        int num = _nextBaseNumber;
        var name = BaseFileName(num);
        var full = Path.Combine(_dir, name);
        SnapshotBaseFile.Write(full, count, blob, MergeSorted());
        _nextBaseNumber = num + 1;

        WriteManifest(name);
        DeleteJournal();

        var old = _base;
        _base = new SnapshotBaseReader(full);
        old?.Dispose();
        _overlay.Clear();
        _removed.Clear();
        CleanupOrphans(name);
    }

    // base (minus tombstones, minus overlay-shadowed) merged with the sorted overlay.
    private IEnumerable<(string Path, FileState State)> MergeSorted()
    {
        var overlayKeys = _overlay.Keys.ToArray();
        Array.Sort(overlayKeys, StringComparer.Ordinal);

        int bi = 0, oi = 0;
        int bn = _base?.Count ?? 0;

        (string Path, FileState State)? NextBase()
        {
            while (bi < bn)
            {
                var p = _base!.GetPath(bi);
                if (_removed.Contains(p) || _overlay.ContainsKey(p)) { bi++; continue; }
                var s = _base.GetState(bi);
                bi++;
                return (p, s);
            }
            return null;
        }

        var pendingBase = NextBase();
        while (pendingBase is not null || oi < overlayKeys.Length)
        {
            if (oi >= overlayKeys.Length)
            {
                yield return pendingBase!.Value;
                pendingBase = NextBase();
            }
            else if (pendingBase is null)
            {
                var k = overlayKeys[oi++];
                yield return (k, _overlay[k]);
            }
            else
            {
                int cmp = string.CompareOrdinal(pendingBase.Value.Path, overlayKeys[oi]);
                if (cmp < 0) { yield return pendingBase.Value; pendingBase = NextBase(); }
                else // overlay keys are never equal to a yielded base key (shadowed base is skipped)
                {
                    var k = overlayKeys[oi++];
                    yield return (k, _overlay[k]);
                }
            }
        }
    }

    /// <summary>Write a fresh base directly from a full in-RAM snapshot (build / full reconcile).</summary>
    public static void WriteFullBase(string dir, IReadOnlyDictionary<string, FileState> snapshot)
    {
        System.IO.Directory.CreateDirectory(dir);
        var keys = snapshot.Keys.ToArray();
        Array.Sort(keys, StringComparer.Ordinal);

        long blob = 0;
        foreach (var k in keys) blob += Encoding.UTF8.GetByteCount(k);

        int num = NextBaseNumber(dir);
        var name = BaseFileName(num);
        SnapshotBaseFile.Write(Path.Combine(dir, name), keys.Length, blob,
                               keys.Select(k => (k, snapshot[k])));

        WriteManifestTo(dir, name, num + 1);
        TryDelete(Path.Combine(dir, JournalName));
        CleanupOrphansIn(dir, name);
    }

    // ---- loading ----

    private void MigrateLegacyIfNeeded()
    {
        if (File.Exists(Path.Combine(_dir, ManifestName))) return;
        var legacy = Path.Combine(_dir, LegacyName);
        if (!File.Exists(legacy)) return;
        try
        {
            using var fs = File.OpenRead(legacy);
            var dict = SnapshotStore.Load(fs);
            fs.Dispose();
            WriteFullBase(_dir, dict);
            TryDelete(legacy);
            Log.Global.Info($"migrated legacy snapshot ({dict.Count:N0} entries) to on-disk base in {_dir}");
        }
        catch (Exception ex) { Log.Global.Warn($"legacy snapshot migration failed in {_dir}: {ex.Message}"); }
    }

    private void LoadManifestAndBase()
    {
        var mf = Path.Combine(_dir, ManifestName);
        if (!File.Exists(mf)) return;
        string[] lines;
        try { lines = File.ReadAllLines(mf); } catch { return; }

        if (lines.Length >= 1 && lines[0].Length > 0)
        {
            var bf = Path.Combine(_dir, lines[0]);
            if (File.Exists(bf))
            {
                try { _base = new SnapshotBaseReader(bf); }
                catch (Exception ex) { Log.Global.Warn($"snapshot base unreadable ({bf}): {ex.Message}"); _base = null; }
            }
        }
        if (lines.Length >= 2 && int.TryParse(lines[1], out var n)) _nextBaseNumber = n;
    }

    private void LoadJournal()
    {
        var jp = Path.Combine(_dir, JournalName);
        if (!File.Exists(jp)) return;
        try
        {
            using var fs = File.OpenRead(jp);
            using var r = new BinaryReader(fs, Encoding.UTF8, leaveOpen: true);
            if (r.ReadUInt32() != JournalMagic) throw new InvalidDataException("bad journal magic");

            int sets = r.ReadInt32();
            for (int i = 0; i < sets; i++)
            {
                var path = r.ReadString();
                var size = r.ReadInt64();
                var mtime = r.ReadInt64();
                var hash = r.ReadString();
                _overlay[path] = new FileState(size, mtime, hash);
            }
            int rems = r.ReadInt32();
            for (int i = 0; i < rems; i++)
            {
                var p = r.ReadString();
                // Keep the invariant _removed ⊆ base: a tombstone for a path not in the base
                // is a no-op, and admitting one would desync Compact's count/blob math.
                if (_base is not null && _base.Contains(p)) _removed.Add(p);
            }
        }
        catch (Exception ex)
        {
            // A torn/corrupt journal means recent unpersisted edits are lost; those files are
            // simply re-detected as changed on the next reconcile, so this is safe.
            _overlay.Clear();
            _removed.Clear();
            Log.Global.Warn($"snapshot journal dropped (corrupt) in {_dir}: {ex.Message}");
        }
    }

    // ---- writers (atomic via temp + replace) ----

    private void WriteJournal()
    {
        var jp = Path.Combine(_dir, JournalName);
        var tmp = jp + ".tmp";
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var w = new BinaryWriter(fs, Encoding.UTF8, leaveOpen: true))
        {
            w.Write(JournalMagic);
            w.Write(_overlay.Count);
            foreach (var (path, state) in _overlay)
            {
                w.Write(path);
                w.Write(state.Size);
                w.Write(state.MTimeTicks);
                w.Write(state.ContentHash);
            }
            w.Write(_removed.Count);
            foreach (var p in _removed) w.Write(p);
        }
        File.Move(tmp, jp, overwrite: true);
    }

    private void WriteManifest(string baseName) => WriteManifestTo(_dir, baseName, _nextBaseNumber);

    private static void WriteManifestTo(string dir, string baseName, int nextNumber)
    {
        var mf = Path.Combine(dir, ManifestName);
        var tmp = mf + ".tmp";
        using (var w = new StreamWriter(tmp, append: false))
        {
            w.WriteLine(baseName);
            w.WriteLine(nextNumber);
        }
        File.Move(tmp, mf, overwrite: true);
    }

    private void DeleteJournal() => TryDelete(Path.Combine(_dir, JournalName));

    // ---- helpers ----

    public static string BaseFileName(int number) => $"snapshot-{number:D8}.base";

    private static int NextBaseNumber(string dir)
    {
        int max = -1;
        if (System.IO.Directory.Exists(dir))
            foreach (var f in System.IO.Directory.EnumerateFiles(dir, BasePattern))
            {
                var name = Path.GetFileNameWithoutExtension(f); // "snapshot-00000123"
                int dash = name.LastIndexOf('-');
                if (dash >= 0 && int.TryParse(name.AsSpan(dash + 1), out var n) && n > max) max = n;
            }
        return max + 1;
    }

    private void CleanupOrphans(string keepName) => CleanupOrphansIn(_dir, keepName);

    private static void CleanupOrphansIn(string dir, string keepName)
    {
        foreach (var f in System.IO.Directory.EnumerateFiles(dir, BasePattern))
            if (!string.Equals(Path.GetFileName(f), keepName, StringComparison.OrdinalIgnoreCase))
                TryDelete(f); // may still be mmapped by another reader: best-effort
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* locked/gone: ignore */ }
    }

    private static int ThresholdFromEnv()
    {
        var env = Environment.GetEnvironmentVariable("CODECOMPASS_SNAPSHOT_COMPACT");
        return int.TryParse(env, out var v) && v >= 1 ? v : DefaultCompactThreshold;
    }

    public void Dispose() => _base?.Dispose();
}
