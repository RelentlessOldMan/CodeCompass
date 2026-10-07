using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CodeCompass.Core.Indexing;
using CodeCompass.Core.Indexing.Segments;
using CodeCompass.Core.Symbols.Segments;
using Xunit;

namespace CodeCompass.Core.Tests;

// A long run of incremental updates must never drift from a from-scratch build of the same tree. A field
// report saw an index that had slowly diverged after days of editing (root cause never found), and the other
// incremental tests are single-pass. This drives a seeded random churn - adds, rewrites, deletes, renames,
// emptied files, whole-directory deletes - through BOTH update paths (self-diffing Update and the watcher's
// targeted UpdatePaths) with compaction kicking in along the way, and at checkpoints compares the persisted
// index with a fresh build of a copy of the tree. Seeded, so a failure reproduces exactly.
[Collection("compaction-env")] // sets the process-wide CODECOMPASS_COMPACT_SEGMENTS
public class ChurnDriftTests
{
    private static readonly string[] Vocab =
    {
        "alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet",
        "kilo", "lima", "mike", "november", "oscar", "papa", "quebec", "romeo", "sierra", "tango",
    };

    private sealed class Churn
    {
        private readonly Random _rng;
        private readonly TempRepo _repo;
        private readonly HashSet<string> _files = new(StringComparer.Ordinal);
        private int _clock;
        public Churn(TempRepo repo, int seed) { _repo = repo; _rng = new Random(seed); }
        public IReadOnlyCollection<string> Files => _files;

        private string Word() => Vocab[_rng.Next(Vocab.Length)];
        private string Name() => char.ToUpperInvariant(Word()[0]) + Word()[1..] + _rng.Next(4);

        private string Content(string rel)
        {
            int lines = _rng.Next(1, 12);
            var sb = new System.Text.StringBuilder();
            switch (Path.GetExtension(rel))
            {
                case ".cs":
                    sb.Append("namespace N {\n");
                    for (int i = 0; i < lines; i++) sb.Append($"  class {Name()} {{ void {Name()}() {{ var x = \"{Word()} {Word()}\"; }} }}\n");
                    sb.Append("}\n");
                    break;
                case ".c":
                    for (int i = 0; i < lines; i++) sb.Append($"int {Word()}_{_rng.Next(4)}(void) {{ return {Word()}_{_rng.Next(4)}(); }}\n");
                    break;
                case ".py":
                    for (int i = 0; i < lines; i++) sb.Append($"def {Word()}_{_rng.Next(4)}():\n    return '{Word()} {Word()}'\n");
                    break;
                default:
                    for (int i = 0; i < lines; i++) sb.Append($"{Word()} {Word()} {Word()}\n");
                    break;
            }
            return sb.ToString();
        }

        private string NewPath()
        {
            string[] exts = { ".cs", ".c", ".py", ".txt" };
            string rel;
            do rel = $"d{_rng.Next(5)}/s{_rng.Next(3)}/f{_rng.Next(10_000)}{exts[_rng.Next(exts.Length)]}";
            while (_files.Contains(rel));
            return rel;
        }

        // Every write gets a fresh, strictly later mtime: change detection is mtime/size gated.
        private void Put(string rel, string content)
        {
            _repo.Write(rel, content);
            File.SetLastWriteTimeUtc(_repo.FullPath(rel), DateTime.UtcNow.AddSeconds(++_clock));
            _files.Add(rel);
        }

        public void Seed(int n) { for (int i = 0; i < n; i++) Put(NewPath(), Content("x.cs")); }

        /// <summary>One random edit; returns the full paths it touched (what a watcher would report).</summary>
        public List<string> Step()
        {
            var touched = new List<string>();
            int op = _rng.Next(100);
            var existing = _files.OrderBy(f => f, StringComparer.Ordinal).ToList();
            string Pick() => existing[_rng.Next(existing.Count)];

            if (op < 25 || existing.Count < 5)                        // add
            {
                var rel = NewPath(); Put(rel, Content(rel)); touched.Add(_repo.FullPath(rel));
            }
            else if (op < 60)                                         // rewrite
            {
                var rel = Pick(); Put(rel, Content(rel)); touched.Add(_repo.FullPath(rel));
            }
            else if (op < 75)                                         // delete
            {
                var rel = Pick(); _repo.Delete(rel); _files.Remove(rel); touched.Add(_repo.FullPath(rel));
            }
            else if (op < 87)                                         // rename (same content, new path)
            {
                var rel = Pick(); var to = NewPath();
                var text = File.ReadAllText(_repo.FullPath(rel));
                _repo.Delete(rel); _files.Remove(rel); Put(to, text);
                touched.Add(_repo.FullPath(rel)); touched.Add(_repo.FullPath(to));
            }
            else if (op < 94)                                         // emptied
            {
                var rel = Pick(); Put(rel, ""); touched.Add(_repo.FullPath(rel));
            }
            else                                                      // whole directory deleted
            {
                var dir = Pick().Split('/')[0] + "/" + Pick().Split('/')[1];
                var full = _repo.FullPath(dir);
                if (Directory.Exists(full))
                {
                    Directory.Delete(full, recursive: true);
                    _files.RemoveWhere(f => f.StartsWith(dir + "/", StringComparison.Ordinal));
                    touched.Add(full);
                }
            }
            return touched;
        }
    }

    private static HashSet<string> SearchSet(SegmentedIndex idx, string q) =>
        idx.Search(q, 1_000_000).Select(m => $"{m.Path}:{m.Line}:{m.Column}").ToHashSet();

    private static HashSet<string> NameSet(SegmentedSymbolIndex idx, string n) =>
        idx.FindByName(n).Select(s => $"{s.RelativePath}:{s.Line}:{s.Column}:{s.Kind}").ToHashSet();

    private static void AssertMatchesFreshBuild(TempRepo repo, IReadOnlyCollection<string> files, int step)
    {
        using var mirror = new TempRepo();
        foreach (var rel in files) mirror.Write(rel, File.ReadAllText(repo.FullPath(rel)));
        var (fullText, fullSymbols, _) = RepositoryIndexer.Build(mirror.Root);
        Assert.True(RepositoryIndexer.TryLoad(repo.Root, out var liveText, out var liveSymbols), $"step {step}: live index won't load");
        using (fullText) using (fullSymbols) using (liveText) using (liveSymbols)
        {
            Assert.True(fullText.DocumentCount == liveText.DocumentCount,
                $"step {step}: {liveText.DocumentCount} docs in the live index vs {fullText.DocumentCount} in a fresh build");
            foreach (var w in Vocab.Append("return").Append("class"))
            {
                var live = SearchSet(liveText, w); var full = SearchSet(fullText, w);
                Assert.True(live.SetEquals(full),
                    $"step {step}: search '{w}' drifted. live-only: {string.Join(", ", live.Except(full).Take(5))}; " +
                    $"missing: {string.Join(", ", full.Except(live).Take(5))}");
            }
            foreach (var w in Vocab)
            {
                for (int i = 0; i < 4; i++)
                {
                    foreach (var n in new[] { $"{w}_{i}", char.ToUpperInvariant(w[0]) + w[1..] + i })
                        Assert.True(NameSet(liveSymbols, n).SetEquals(NameSet(fullSymbols, n)), $"step {step}: symbol '{n}' drifted");
                }
            }
        }
    }

    // Each path is churned on its own as well as mixed: a self-diffing Update re-walks the disk and would quietly
    // repair what a targeted UpdatePaths got wrong, so a mixed run alone can hide a watcher-path drift.
    [Theory]
    [InlineData("watcher")]
    [InlineData("update")]
    [InlineData("mixed")]
    public void LongRandomChurn_NeverDriftsFromAFreshBuild(string mode)
    {
        var prev = Environment.GetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS");
        Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", "6"); // compaction fires several times
        try
        {
            using var repo = new TempRepo();
            var churn = new Churn(repo, seed: 20261007 + mode.Length);
            churn.Seed(40);
            var (t0, s0, _) = RepositoryIndexer.Build(repo.Root);
            t0.Dispose(); s0.Dispose();

            const int steps = 90;
            int compactions = 0;
            for (int step = 1; step <= steps; step++)
            {
                var touched = churn.Step();
                if (step % 3 == 0) touched.AddRange(churn.Step()); // some batches carry several edits

                bool selfDiff = mode == "update" || (mode == "mixed" && step % 2 == 0);
                var (t, s, _) = selfDiff
                    ? RepositoryIndexer.Update(repo.Root)                // self-diff (the `update` command)
                    : RepositoryIndexer.UpdatePaths(repo.Root, touched); // targeted (the file watcher)
                bool compact = RepositoryIndexer.NeedsCompaction(t, s);
                t.Dispose(); s.Dispose();
                if (compact)
                {
                    var (ct, cs) = RepositoryIndexer.Compact(repo.Root);
                    ct.Dispose(); cs.Dispose();
                    compactions++;
                }

                if (step % 30 == 0) AssertMatchesFreshBuild(repo, churn.Files, step);
            }
            Assert.True(compactions > 0, "the churn never reached the compaction threshold - the test lost coverage");
        }
        finally { Environment.SetEnvironmentVariable("CODECOMPASS_COMPACT_SEGMENTS", prev); }
    }
}
