using CodeCompass.Core.Diagnostics;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Storage;
using CodeCompass.Core.Symbols.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

public class IndexerContentVersionTests
{
    // TRIPWIRE. These three numbers move together in intent: any change to the on-disk segment layout
    // (a format Version) is by definition a change to what a rebuild produces, so it MUST also bump
    // BuildInfo.IndexerContentVersion - otherwise a stale index would masquerade as current and the
    // staleness nudge would never fire. If you land such a change and this test fails, that's the reminder:
    // bump IndexerContentVersion (and update the expected values here) as a single deliberate act.
    [Fact]
    public void Format_And_Content_Versions_Are_Pinned_Together()
    {
        Assert.Equal(1, SegmentBuilder.Version);          // trigram text segment layout
        Assert.Equal(2, SymbolSegmentBuilder.Version);    // symbol segment layout (v2 added endLines)
        Assert.Equal(0, BuildInfo.IndexerContentVersion); // baseline; bump when indexer OUTPUT changes
    }

    [Fact]
    public void IndexerBehind_False_ForCurrentOrNullMeta()
    {
        // Null meta: unknown provenance -> don't cry wolf.
        Assert.False(IndexMetaFile.IndexerBehind(null, out _, out _));

        // A meta stamped with the current content version is not behind.
        var current = new IndexMeta("r", "1.0.0", "t", 1, ContentVersion: BuildInfo.IndexerContentVersion);
        Assert.False(IndexMetaFile.IndexerBehind(current, out var bw, out var cur));
        Assert.Equal(cur, bw);
    }

    [Fact]
    public void IndexerBehind_True_WhenContentVersionOlder()
    {
        // Simulate a future binary: an index stamped one content version back is stale.
        var old = new IndexMeta("r", "1.0.0", "t", 1, ContentVersion: BuildInfo.IndexerContentVersion - 1);
        Assert.True(IndexMetaFile.IndexerBehind(old, out var builtWith, out var current));
        Assert.Equal(BuildInfo.IndexerContentVersion - 1, builtWith);
        Assert.Equal(BuildInfo.IndexerContentVersion, current);
    }

    [Fact]
    public void ProductVersionDifference_Alone_IsNotStale()
    {
        // The whole point: a different (older) product version with the SAME content version is NOT stale,
        // so upgrading the binary without touching the indexer raises no rebuild nudge.
        var meta = new IndexMeta("r", "0.0.1-ancient", "t", 1, ContentVersion: BuildInfo.IndexerContentVersion);
        Assert.False(IndexMetaFile.IndexerBehind(meta, out _, out _));
    }

    [Fact]
    public void Build_Stamps_The_Current_Content_Version()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A {}");
        var (t, s, _) = RepositoryIndexer.Build(repo.Root);
        t.Dispose(); s.Dispose();
        IndexMetaFile.Write(repo.Root, 1);

        var meta = IndexMetaFile.Read(repo.Root);
        Assert.NotNull(meta);
        Assert.Equal(BuildInfo.IndexerContentVersion, meta!.ContentVersion);
        Assert.False(IndexMetaFile.IndexerBehind(meta, out _, out _));
    }
}
