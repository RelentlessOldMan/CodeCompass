using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Text;
using CodeCompass.Core.Indexing;
using Xunit;

namespace CodeCompass.Core.Tests;

// The build read each file into a fresh byte[] and decoded it into a fresh string: ~3x the file's size in new arrays per
// file, all over the 85 KB large-object threshold. They die as soon as the file is done, but only a gen2 GC frees large
// objects, and between full GCs they piled up - the 88 GB share's index peaked at 6-7 GB while its read budget allowed
// about 2. The workers now read and decode into buffers they keep, so a file allocates next to nothing for its text.
// Measured per file on the worker's own thread (GetAllocatedBytesForCurrentThread), so other tests can't add noise.
[Collection("compaction-env")] // the static test seams
public class IndexAllocationTests
{
    [Fact]
    public void Build_OfOneMegabyteFiles_DoesNotAllocateAFreshCopyOfEachFile()
    {
        const int files = 40, size = 1 << 20;
        using var repo = new TempRepo();
        var words = Enumerable.Range(0, 500).Select(i => $"word{i * 7919 % 10007}").ToArray();
        var rnd = new Random(20261030);
        for (int f = 0; f < files; f++)
        {
            var sb = new StringBuilder(size + 64);
            while (sb.Length < size) sb.Append(words[rnd.Next(words.Length)]).Append(rnd.Next(12) == 0 ? '\n' : ' ');
            sb.Length = size;
            repo.Write($"docs/f{f}.txt", sb.ToString()); // text only: no symbols, so no string for tree-sitter
        }

        var perFile = new ConcurrentDictionary<string, long>();
        RepositoryIndexer.OnFileDoneForTests = (rel, allocated) => perFile[rel] = allocated;
        try
        {
            var (t, s, _) = RepositoryIndexer.Build(repo.Root);
            using (t) using (s) Assert.Equal(files, t.DocumentCount);
        }
        finally { RepositoryIndexer.OnFileDoneForTests = null; }

        Assert.Equal(files, perFile.Count);
        long total = perFile.Values.Sum();
        // A fresh read + decode is 3 MB a file (120 MB here; 122.5 MB measured before the fix, 2.3 MB after - the postings).
        Assert.True(total < files * (long)size / 4, $"the build allocated {total / 1048576.0:N1} MB for {files} files of 1 MB " +
            $"(largest {perFile.Values.Max() / 1048576.0:N1} MB); a fresh copy of each would be {files * 3} MB");
    }

    // The buffers live in native memory, which the allocation count above can't see: after a file bigger than the keep
    // size a worker must let its buffers go, or it holds its largest file (3x, bytes plus chars) for the rest of the build.
    [Fact]
    public void Build_AfterAFileOverTheKeepSize_FreesTheWorkersBuffers()
    {
        using var repo = new TempRepo();
        repo.Write("big.txt", string.Concat(Enumerable.Repeat("a big file line\n", (6 << 20) / 16))); // 6 MB
        for (int i = 0; i < 4; i++) repo.Write($"small{i}.txt", "small file " + i);

        var held = new ConcurrentDictionary<string, long>();
        RepositoryIndexer.OnBuffersTrimmedForTests = (rel, bytes) => held[rel] = bytes;
        try
        {
            var (t, s, _) = RepositoryIndexer.Build(repo.Root);
            using (t) using (s) Assert.Equal(5, t.DocumentCount);
        }
        finally { RepositoryIndexer.OnBuffersTrimmedForTests = null; }

        Assert.Equal(5, held.Count);
        Assert.True(held["big.txt"] == 0, $"after the 6 MB file the worker still holds {held["big.txt"]:N0} bytes of buffers");
        Assert.All(held.Values, v => Assert.True(v <= 3 << 20, $"a worker kept {v:N0} bytes of buffers (1 MB of bytes + 2 MB of chars at most)"));
    }
}
