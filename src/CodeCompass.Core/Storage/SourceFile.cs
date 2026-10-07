using System.Text;

namespace CodeCompass.Core.Storage;

/// <summary>
/// Network-aware reads of SOURCE files (the repo's own files, which may live on an SMB share), as
/// opposed to the index cache (always local). Over a share, throughput is dominated by round-trips,
/// so we open with a large buffer and the <see cref="FileOptions.SequentialScan"/> hint to pull more
/// per request and let the OS read ahead; locally we keep the framework defaults so behavior is
/// unchanged. Encoding detection matches <see cref="Text.TextDecoder"/> / <c>File.ReadAllText</c>
/// (BOM sniff, UTF-8 default), so reported line/column positions are identical on every read path.
/// </summary>
public static class SourceFile
{
    // A 1 MB buffer turns a multi-KB source file into one or two SMB reads instead of dozens of 4 KB
    // ones; locally the default keeps small reads cheap. SequentialScan asks the cache manager to read
    // ahead, which the whole-file/line scans always want.
    private const int NetworkBufferBytes = 1 << 20;
    private const int LocalBufferBytes = 4096;

    /// <summary>Open a source file for reading, tuned for a network share when <paramref name="network"/>.</summary>
    public static FileStream OpenSequential(string fullPath, bool network) =>
        new(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read,
            network ? NetworkBufferBytes : LocalBufferBytes,
            network ? FileOptions.SequentialScan : FileOptions.None);

    /// <summary>Read an already-open source stream to a string (BOM-aware, like <c>File.ReadAllText</c>).
    /// Leaves the stream open so the caller's <c>using</c> owns it.</summary>
    public static string ReadAllText(FileStream fs)
    {
        // Read the bytes once and decode them in one pass. StreamReader.ReadToEnd decodes into a growing StringBuilder and
        // then copies it into the final string - on a name search over large files that copy alone was ~2 s per GB. The
        // result is identical: same BOM rules (StreamReader's), same UTF-8 default, same U+FFFD for invalid bytes.
        long start = 0, len = -1;
        try { start = fs.Position; len = fs.Length - start; } catch { }
        if (len >= 0 && len <= Array.MaxLength)
        {
            var bytes = new byte[len];
            int got = fs.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
            if (got == bytes.Length && fs.ReadByte() < 0) return Decode(bytes.AsSpan(0, got));
            fs.Position = start; // the file changed size while we read it: take the streaming path from the start
        }
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
    }

    // StreamReader's detectEncodingFromByteOrderMarks, applied to the whole file: UTF-16 BE/LE, UTF-32 LE/BE, UTF-8 BOMs.
    private static string Decode(ReadOnlySpan<byte> b)
    {
        if (b.Length >= 2 && b[0] == 0xFE && b[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(b[2..]);
        if (b.Length >= 2 && b[0] == 0xFF && b[1] == 0xFE)
            return b.Length >= 4 && b[2] == 0 && b[3] == 0 ? Encoding.UTF32.GetString(b[4..]) : Encoding.Unicode.GetString(b[2..]);
        if (b.Length >= 3 && b[0] == 0xEF && b[1] == 0xBB && b[2] == 0xBF) return Encoding.UTF8.GetString(b[3..]);
        if (b.Length >= 4 && b[0] == 0 && b[1] == 0 && b[2] == 0xFE && b[3] == 0xFF)
            return new UTF32Encoding(bigEndian: true, byteOrderMark: false).GetString(b[4..]);
        return Encoding.UTF8.GetString(b);
    }

    /// <summary>Stream a source file line by line, BOM-aware, with a network-tuned buffer when
    /// <paramref name="network"/>. Falls back to the framework's <c>File.ReadLines</c> locally so the
    /// local path is byte-for-byte what it was before.</summary>
    public static IEnumerable<string> ReadLines(string fullPath, bool network)
    {
        if (!network) return File.ReadLines(fullPath);
        return ReadLinesBuffered(fullPath);
    }

    private static IEnumerable<string> ReadLinesBuffered(string fullPath)
    {
        using var fs = OpenSequential(fullPath, network: true);
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        string? line;
        while ((line = reader.ReadLine()) is not null) yield return line;
    }
}
