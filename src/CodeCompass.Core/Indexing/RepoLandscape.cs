namespace CodeCompass.Core.Indexing;

/// <summary>
/// The measured SHAPE of a repository - a size histogram + byte concentration, computed from the file sizes
/// the build walk already stats (no extra I/O, no second pass). It answers "is this repo huge because of
/// MANY files, a FEW big files, or a mid-size tail?" and turns that into an adaptive decision: over a
/// network path with a meaningful 1-8 MB tail (the register-map / debugger-script shape), lower the sidecar
/// threshold so those files get block-selective reads instead of whole-file reads on broad queries. Local
/// repos keep the default (whole-file reads are ~free locally). The chosen threshold is recorded in meta so
/// incremental updates stay consistent with the build. Deterministic and explainable - the same metrics the
/// standalone profile-repo.ps1 emits.
/// </summary>
public sealed class RepoLandscape
{
    // Bucket upper bounds (bytes), mirrored from profile-repo.ps1 so the built-in and standalone agree.
    private static readonly long[] Edges =
        { 256L * 1024, 1L << 20, 2L << 20, 4L << 20, 8L << 20, 16L << 20, 128L << 20, 1L << 30 };

    public long TotalFiles { get; private init; }
    public long TotalBytes { get; private init; }
    public long[] BucketFiles { get; private init; } = new long[9];
    public long[] BucketBytes { get; private init; } = new long[9];
    public long LargestBytes { get; private init; }
    public long MedianBytes { get; private init; }
    public double ByteShareTop1Pct { get; private init; }
    public double ByteShareTop2Pct { get; private init; }

    /// <summary>Files in the 1-8 MB band that get NO sidecar at the default 8 MB cutoff (buckets 2,3,4).</summary>
    public long MidTailFiles => BucketFiles[2] + BucketFiles[3] + BucketFiles[4];
    public long MidTailBytes => BucketBytes[2] + BucketBytes[3] + BucketBytes[4];
    public double MidTailByteShare => TotalBytes > 0 ? (double)MidTailBytes / TotalBytes : 0;

    public const long DefaultSidecarThreshold = 8L << 20; // LargeFileIndexer.SidecarThresholdBytes
    public const long LowSidecarThreshold = 2L << 20;

    /// <summary>The sidecar cutoff to use for THIS repo. Over a network path with a meaningful 1-8 MB tail
    /// (a byte share >= 4% OR >= 1000 such files - the firmware/register-map shape), drop the cutoff to 2 MB
    /// so those trigram-dense mid-size files are read block-selectively instead of whole on broad queries
    /// (the ~600 MB-per-broad-query residual measured over SMB). Local repos keep 8 MB: a 2-8 MB whole-file
    /// read is ~free locally, so building extra sidecars would be cost with no benefit. A normal source repo
    /// (no mid-tail) keeps 8 MB either way - lowering it would create almost no sidecars anyway.</summary>
    public long EffectiveSidecarThreshold(bool isNetwork)
    {
        if (!isNetwork) return DefaultSidecarThreshold;
        bool meaningfulMidTail = MidTailByteShare >= 0.04 || MidTailFiles >= 1000;
        return meaningfulMidTail ? LowSidecarThreshold : DefaultSidecarThreshold;
    }

    /// <summary>One-line archetype for logs / doctor.</summary>
    public string Summary()
    {
        var tags = new List<string>();
        if (ByteShareTop2Pct >= 0.60) tags.Add("byte-heavy");
        if (TotalFiles >= 20000 && MedianBytes < 4096) tags.Add("count-heavy");
        if (BucketFiles[8] > 0) tags.Add($"{BucketFiles[8]} >1GB");
        if (tags.Count == 0) tags.Add("ordinary");
        return $"{TotalFiles:N0} files, {TotalBytes / (1024.0 * 1024 * 1024):F1} GB, " +
               $"top2%={ByteShareTop2Pct * 100:F0}% of bytes, mid-tail(1-8MB)={MidTailByteShare * 100:F1}% " +
               $"({MidTailFiles:N0} files) [{string.Join(", ", tags)}]";
    }

    /// <summary>Compute the landscape from the sizes the walk already collected (single pass; sizes are
    /// sorted once for median + concentration). Never throws.</summary>
    public static RepoLandscape Compute(IReadOnlyList<long> sizes)
    {
        var bf = new long[9];
        var bb = new long[9];
        long total = 0;
        foreach (var s in sizes)
        {
            int i = 0;
            while (i < Edges.Length && s >= Edges[i]) i++;
            bf[i]++; bb[i] += s; total += s;
        }

        long n = sizes.Count;
        long largest = 0, median = 0;
        double top1 = 0, top2 = 0;
        if (n > 0 && total > 0)
        {
            var sorted = new long[n];
            for (int i = 0; i < n; i++) sorted[i] = sizes[i];
            System.Array.Sort(sorted); // ascending
            largest = sorted[n - 1];
            median = sorted[n / 2];
            top1 = TopShare(sorted, total, 0.01);
            top2 = TopShare(sorted, total, 0.02);
        }

        return new RepoLandscape
        {
            TotalFiles = n,
            TotalBytes = total,
            BucketFiles = bf,
            BucketBytes = bb,
            LargestBytes = largest,
            MedianBytes = median,
            ByteShareTop1Pct = top1,
            ByteShareTop2Pct = top2,
        };
    }

    // Byte share held by the largest `pct` fraction of files (sorted ascending -> read from the top).
    private static double TopShare(long[] sortedAsc, long total, double pct)
    {
        long k = System.Math.Max(1, (long)System.Math.Ceiling(sortedAsc.Length * pct));
        long sum = 0;
        for (long i = sortedAsc.Length - 1, c = 0; i >= 0 && c < k; i--, c++) sum += sortedAsc[i];
        return (double)sum / total;
    }
}
