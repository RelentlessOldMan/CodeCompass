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
        using var reader = new StreamReader(fs, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, leaveOpen: true);
        return reader.ReadToEnd();
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
