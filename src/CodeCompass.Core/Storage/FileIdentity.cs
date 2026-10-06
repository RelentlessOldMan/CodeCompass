using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace CodeCompass.Core.Storage;

/// <summary>
/// A file's identity as seen through an OPEN HANDLE: size, last-write time, change time (any metadata change, including
/// setting the last-write time back) and the filesystem's file id (the NTFS file index - the same value
/// <c>GetFileInformationByHandle</c> reports). Ticks are UTC .NET ticks, comparable with <c>FileInfo.LastWriteTimeUtc.Ticks</c>.
/// All zero (<c>default</c>) = unknown: off Windows, or the query failed - callers then fall back to the directory listing
/// alone. Recorded in the hash ledger (v2) so another tool can tell a same-size rewrite whose modified time was put back
/// (docs/hash-ledger-format.md).
///
/// <para>Cost matters: every file a build or update reads is statted just before and just after the read, and over SMB
/// each query is a network round trip. So the "before" stat is ONE query (FileAllInformation: times, size and file index
/// together) and the "after" stat is one (FileBasicInfo: the two times, which move on any write).</para>
/// </summary>
public readonly record struct FileIdentity(long Size, long MTimeTicks, long ChangeTicks, long FileId)
{
    public bool Known => MTimeTicks > 0;

    /// <summary>Full identity of an open handle, in one query. Unknown off Windows or on failure; never throws.</summary>
    public static FileIdentity Of(SafeFileHandle h)
    {
        if (!OperatingSystem.IsWindows()) return default;
        try
        {
            var buf = new byte[AllInfoBytes];
            int status = NtQueryInformationFile(h, out _, buf, buf.Length, FileAllInformationClass);
            // STATUS_BUFFER_OVERFLOW: the trailing file name didn't fit, but every fixed field we read was filled.
            if (status != 0 && status != StatusBufferOverflow) return default;
            return new FileIdentity(
                BitConverter.ToInt64(buf, OffEndOfFile),
                Ticks(BitConverter.ToInt64(buf, OffLastWrite)),
                Ticks(BitConverter.ToInt64(buf, OffChange)),
                BitConverter.ToInt64(buf, OffIndexNumber));
        }
        catch { return default; }
    }

    /// <summary>Just the two times of an open handle (one query) - enough to see that the file was written to.</summary>
    public static (long MTimeTicks, long ChangeTicks) TimesOf(SafeFileHandle h)
    {
        if (!OperatingSystem.IsWindows()) return default;
        try
        {
            return GetFileInformationByHandleEx(h, FileBasicInfoClass, out var basic, (uint)Marshal.SizeOf<FileBasicInfo>())
                ? (Ticks(basic.LastWriteTime), Ticks(basic.ChangeTime))
                : default;
        }
        catch { return default; }
    }

    /// <summary>Open with attribute access only (never blocks or is blocked by a writer) and stat. Long paths are passed
    /// with the <c>\\?\</c> prefix, as .NET does for its own opens. Unknown on failure.</summary>
    public static FileIdentity OfPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return default;
        try
        {
            using var h = CreateFileW(LongPath(path), FileReadAttributes, 7 /* share read|write|delete */, IntPtr.Zero,
                                      3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
            return h.IsInvalid ? default : Of(h);
        }
        catch { return default; }
    }

    /// <summary>Read a whole file, statting its handle just before (full identity) and just after (the two times, with
    /// the size and file id carried from before) the read.</summary>
    public static byte[] ReadAllBytes(string path, out FileIdentity before, out FileIdentity after)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1, FileOptions.SequentialScan);
        before = Of(fs.SafeFileHandle);
        long len = fs.Length;
        if (len > Array.MaxLength) throw new IOException($"file too large to read whole: {path}");
        var bytes = new byte[len];
        int read = 0;
        while (read < bytes.Length)
        {
            int n = fs.Read(bytes, read, bytes.Length - read);
            if (n == 0) break;
            read += n;
        }
        if (read < bytes.Length) Array.Resize(ref bytes, read); // shrank while reading: the read size won't match
        var (m, c) = before.Known ? TimesOf(fs.SafeFileHandle) : default;
        after = before with { MTimeTicks = m, ChangeTicks = c };
        return bytes;
    }

    /// <summary>The <c>\\?\</c> form of a full path (<c>\\?\UNC\server\share\...</c> for a UNC path), which lifts the
    /// 260-character limit for a raw CreateFileW.</summary>
    internal static string LongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        return @"\\?\" + path;
    }

    private static long Ticks(long fileTime) => fileTime <= 0 ? 0 : DateTime.FromFileTimeUtc(fileTime).Ticks;

    // FILE_ALL_INFORMATION: FILE_BASIC_INFORMATION (40 bytes: creation, access, write, change, attributes + padding), then
    // FILE_STANDARD_INFORMATION (allocation size, END OF FILE, links, flags: 24 bytes), then FILE_INTERNAL_INFORMATION
    // (the 64-bit index number), then smaller blocks and the variable-length name.
    private const int FileAllInformationClass = 18;
    private const int OffLastWrite = 16, OffChange = 24, OffEndOfFile = 48, OffIndexNumber = 64;
    private const int AllInfoBytes = 104 + 2 * 520; // fixed part + room for a typical name (overflow is fine)
    private const int StatusBufferOverflow = unchecked((int)0x80000005);

    private const int FileBasicInfoClass = 0;
    private const uint FileReadAttributes = 0x80;

    [StructLayout(LayoutKind.Sequential)]
    private struct FileBasicInfo { public long CreationTime, LastAccessTime, LastWriteTime, ChangeTime; public uint FileAttributes; }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock { public IntPtr Status; public IntPtr Information; }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationFile(SafeFileHandle h, out IoStatusBlock io, byte[] info, int length, int infoClass);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandleEx(SafeFileHandle h, int infoClass, out FileBasicInfo info, uint size);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sa, uint disposition, uint flags, IntPtr template);
}
