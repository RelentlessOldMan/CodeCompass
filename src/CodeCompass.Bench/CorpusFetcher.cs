using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;

namespace CodeCompass.Bench;

/// <summary>
/// Downloads a pinned corpus as a GitHub codeload tarball, verifies its checksum
/// (fails loudly on mismatch so a moved/deleted tag can't silently change what we
/// benchmark), and extracts it into the gitignored corpus cache. Already-fetched
/// corpora are reused.
/// </summary>
public static class CorpusFetcher
{
    public static string CorpusDir =>
        Environment.GetEnvironmentVariable("CODECOMPASS_CORPUS_DIR")
        ?? Path.Combine(Directory.GetCurrentDirectory(), ".corpus");

    public static async Task<string> FetchAsync(CorpusEntry entry)
    {
        Directory.CreateDirectory(CorpusDir);
        var dest = Path.Combine(CorpusDir, entry.Id);

        if (Directory.Exists(dest) && Directory.EnumerateFileSystemEntries(dest).Any())
        {
            Console.Error.WriteLine($"[{entry.Id}] already fetched");
            return ResolveRoot(dest);
        }
        Directory.CreateDirectory(dest);

        var tgz = Path.Combine(CorpusDir, entry.Id + ".tar.gz");
        Console.Error.WriteLine($"[{entry.Id}] downloading {entry.TarballUrl}");

        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(60) })
        {
            http.DefaultRequestHeaders.UserAgent.ParseAdd("CodeCompass-Bench/0.1");
            using var resp = await http.GetAsync(entry.TarballUrl, HttpCompletionOption.ResponseHeadersRead);
            resp.EnsureSuccessStatusCode();
            using var outFile = File.Create(tgz);
            await resp.Content.CopyToAsync(outFile);
        }

        string sha;
        using (var s = File.OpenRead(tgz))
            sha = Convert.ToHexString(SHA256.HashData(s)).ToLowerInvariant();

        if (!string.IsNullOrEmpty(entry.Sha256) &&
            !string.Equals(entry.Sha256, sha, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tgz);
            throw new InvalidOperationException(
                $"[{entry.Id}] checksum mismatch: manifest expects {entry.Sha256}, got {sha}. " +
                "The upstream tag may have moved. Re-pin deliberately.");
        }
        Console.Error.WriteLine($"[{entry.Id}] sha256={sha}");

        Console.Error.WriteLine($"[{entry.Id}] extracting...");
        using (var s = File.OpenRead(tgz))
        using (var gz = new GZipStream(s, CompressionMode.Decompress))
            TarFile.ExtractToDirectory(gz, dest, overwriteFiles: true);
        File.Delete(tgz);

        return ResolveRoot(dest);
    }

    // GitHub tarballs contain a single top-level "<repo>-<ref>/" directory.
    private static string ResolveRoot(string dest)
    {
        var subs = Directory.GetDirectories(dest);
        return subs.Length == 1 ? subs[0] : dest;
    }
}
