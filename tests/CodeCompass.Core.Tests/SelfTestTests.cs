using System.Security.Cryptography;
using CodeCompass.Core.SelfTest;
using CodeCompass.Core.Storage;
using Xunit;

namespace CodeCompass.Core.Tests;

public class SelfTestTests
{
    // ---- the oracles: parsing an answer, checking it against the file ----

    [Fact]
    public void Hits_ParsesRowsUpToTheFooter_IncludingAbsoluteWindowsPaths()
    {
        var answer = "src/a.cs:3:5: var x = Foo();\nC:\\linked\\b.cs:10:1: Foo();\n\n(2 matches)\nnot:1:1: a row";
        var hits = AnswerChecks.Hits(answer);
        Assert.Equal(2, hits.Count);
        Assert.Equal(new AnswerHit("src/a.cs", 3, 5, "var x = Foo();"), hits[0]);
        Assert.Equal(new AnswerHit("C:\\linked\\b.cs", 10, 1, "Foo();"), hits[1]);
    }

    [Fact]
    public void Defs_ParsesSpanAndKind_AndStopsBeforeTheInlinedSource()
    {
        var answer = "src/a.cs:4-8:20: Method ComputeTotal\n\n4: public decimal ComputeTotal(Order order)\n5: {";
        var d = Assert.Single(AnswerChecks.Defs(answer));
        Assert.Equal(new AnswerDef("src/a.cs", 4, 8, 20, "Method", "ComputeTotal"), d);
        Assert.Equal(7, Assert.Single(AnswerChecks.Defs("x.c:7:5: Function parse\n(1 match)")).EndLine);
    }

    [Fact]
    public void FooterReaders_SeeTruncationAndTheRefsSplit()
    {
        var refs = "a.cs:1:1: Foo();\n\n(3 C# semantic + 1 name-matched reference(s)) - MORE EXIST";
        Assert.True(AnswerChecks.Truncated(refs));
        Assert.Equal((3, 1), AnswerChecks.RefsBreakdown(refs));
        Assert.False(AnswerChecks.Truncated("a.cs:1:1: x\n\n(1 matches)"));
        Assert.Null(AnswerChecks.RefsBreakdown("No references found for \"Foo\"."));
    }

    [Theory]
    [InlineData("return Foo(x);", 8, "Foo", true)]
    [InlineData("return Foo(x);", 7, "Foo", false)]      // one column off
    [InlineData("return FooBar(x);", 8, "Foo", false)]   // a longer name
    [InlineData("return xFoo(x);", 9, "Foo", false)]     // the tail of another name
    [InlineData("Foo", 1, "Foo", true)]
    [InlineData("Fo", 1, "Foo", false)]
    public void WholeWordAt_HoldsOnlyTheWholeNameAtTheColumn(string text, int col, string word, bool expected) =>
        Assert.Equal(expected, AnswerChecks.WholeWordAt(text, col, word));

    [Theory]
    [InlineData("a.cs", "    var s = OrderService.Create();", 13, "OrderService", true, true)]
    [InlineData("a.cs", "    private readonly OrderService _service = new();", 46, "OrderService", true, true)] // target-typed new
    [InlineData("a.cs", "    Run(new ());", 9, "OrderService", true, true)]                                      // ...even without the name
    [InlineData("a.cs", "    Run(new());", 9, "ComputeTotal", false, false)]                                     // a method isn't constructed
    [InlineData("a.cs", "    var l = new List<int>();", 13, "OrderService", true, false)]                        // new of another type
    [InlineData("a.cs", "    var n = new { A = 1 };", 13, "OrderService", true, false)]                          // an anonymous object
    [InlineData("a.c", "    x = new_buffer();", 9, "OrderService", true, false)]                                 // C has no target-typed new
    [InlineData("a.cs", "[Obsolete(\"x\")]", 2, "ObsoleteAttribute", true, true)]                                // an attribute's short form
    [InlineData("a.cs", "    var s = OrderServices.Create();", 13, "OrderService", true, false)]
    [InlineData("a.cs", "    var s = OrderService.Create();", 12, "OrderService", true, false)]
    public void ReferenceAt_TakesWhatAReferenceCanLookLike(string path, string line, int col, string name, bool isType, bool expected) =>
        Assert.Equal(expected, AnswerChecks.ReferenceAt(path, line, col, name, isType));

    // Roslyn finds these where the code never spells them: a using's Dispose, a foreach's GetEnumerator, an await's
    // GetAwaiter, a collection initializer's Add. A row there isn't wrong, it's unprovable from the text.
    [Theory]
    [InlineData("a.cs", "Dispose", true)]
    [InlineData("a.cs", "GetEnumerator", true)]
    [InlineData("a.cs", "GetAwaiter", true)]
    [InlineData("a.cs", "Deconstruct", true)]
    [InlineData("a.cs", "ComputeTotal", false)]
    [InlineData("a.c", "Dispose", false)] // matched by name outside C#: the name is always spelled
    public void CanBeImplicit_NamesTheCallsCSharpMakesUnseen(string path, string name, bool expected) =>
        Assert.Equal(expected, AnswerChecks.CanBeImplicit(path, name));

    [Theory]
    [InlineData("a.cs:1:1: Foo();\n\n(1 C# semantic + 0 name-matched reference(s))", RefsAnswer.Rows)]
    [InlineData("No references found for \"Foo\". Tip: try search_code for a raw text search", RefsAnswer.NoneFound)]
    [InlineData("The query failed unexpectedly. It has been logged; try a narrower query", RefsAnswer.Other)]
    [InlineData("a.cs:1:1: Foo();", RefsAnswer.Other)] // rows without the footer: cut short
    [InlineData("", RefsAnswer.Other)]
    public void RefsShape_TellsRowsFromNoneFoundFromAnythingElse(string answer, RefsAnswer expected) =>
        Assert.Equal(expected, AnswerChecks.RefsShape(answer));

    [Theory]
    [InlineData("Focused on 1 of 2 root(s): C:\\r. 1 root(s) excluded from searches until you clear focus", true)]
    [InlineData("Focus set to all 1 root(s) - nothing is excluded (clear with action=focus, no path).", true)]
    [InlineData("No root matches: r. Known roots: C:\\r. Focus unchanged - pass a repo folder name", false)]
    public void FocusTook_ReadsTheFocusAnswer(string answer, bool expected) =>
        Assert.Equal(expected, AnswerChecks.FocusTook(answer));

    // ---- the anonymized report's names ----

    [Fact]
    public void ReportNamer_Anonymized_IsSaltedPerRun_AndScrubsPathsFromFreeText()
    {
        var root = Path.Combine(Path.GetTempPath(), "SecretRepo");
        var link = Path.Combine(Path.GetTempPath(), "SecretLink");
        var a = new ReportNamer(true, root, [link]);
        var b = new ReportNamer(true, root, [link]);
        Assert.NotEqual(a.Sym("ComputeTotal"), b.Sym("ComputeTotal")); // no fixed hash a guessed name can be checked against
        Assert.Equal(a.Sym("ComputeTotal"), a.Sym("ComputeTotal"));    // ...but steady within the run
        Assert.StartsWith("sym-", a.Sym("ComputeTotal"));
        Assert.StartsWith("repo-", a.RepoLabel);

        var cache = IndexStore.CacheDirPath(root);
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var text = a.Text($"linked root indexed: {link}; at {cache}; repo {root.Replace('\\', '/')}; elsewhere {Path.Combine(home, "x")}");
        foreach (var secret in new[] { "SecretRepo", "SecretLink", cache, home })
            Assert.DoesNotContain(secret, text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("boom at C:\\x", a.Raw("boom at C:\\x"));

        var plain = new ReportNamer(false, root, [link]);
        Assert.Equal("ComputeTotal", plain.Sym("ComputeTotal"));
        Assert.Equal("at " + link, plain.Text("at " + link));
        Assert.Equal("boom", plain.Raw("boom"));
    }

    // ---- the server dying mid-run ----

    [Fact]
    public void Session_TheServerDying_IsAnAnswer_NotACrash()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A {}");
        using var s = McpStdioSession.Start(TestCli.FindMcp(), repo.Root, 0, TimeSpan.FromMinutes(2));
        using (var p = System.Diagnostics.Process.GetProcessById(s.ProcessId)) { p.Kill(entireProcessTree: true); p.WaitForExit(); }
        for (int i = 0; i < 2; i++) // the second write meets a closed pipe
        {
            var res = s.Call("search_code", new Dictionary<string, object?> { ["query"] = "A" }, TimeSpan.FromSeconds(30));
            Assert.True(res.IsError);
            Assert.False(res.TimedOut);
        }
    }

    [Fact]
    public void RepoLines_ReadsLinesAsTheIndexDoes_CrlfBomAndRelativeOrAbsolute()
    {
        using var repo = new TempRepo();
        repo.WriteBytes("a.cs", [0xEF, 0xBB, 0xBF, .. "line one\r\n  Foo();\r\nlast"u8.ToArray()]);
        var lines = new RepoLines(repo.Root);
        Assert.Equal("line one", lines.Line("a.cs", 1));         // the BOM isn't part of line 1
        Assert.Equal("  Foo();", lines.Line("a.cs", 2));          // nor is the \r
        Assert.Equal("last", lines.Line(Path.Combine(repo.Root, "a.cs"), 3));
        Assert.Null(lines.Line("a.cs", 4));
        Assert.Null(lines.Line("gone.cs", 1));
        Assert.True(AnswerChecks.WholeWordAt(lines.Line("a.cs", 2)!, 3, "Foo"));
        Assert.Equal("line one\n  Foo();", lines.Span("a.cs", 1, 2));
    }

    // ---- the command, end to end: the real CLI indexing, the real server over stdio ----

    private static void MixedRepo(TempRepo repo)
    {
        repo.Write("src/OrderService.cs", """
            namespace Shop;

            public class OrderService
            {
                public decimal ComputeTotal(Order order)
                {
                    var subtotal = SumOrderLines(order);
                    return ApplyDiscount(subtotal);
                }

                private decimal SumOrderLines(Order order)
                {
                    decimal sum = 0;
                    foreach (var line in order.Lines) sum += line.Price;
                    return sum;
                }

                private decimal ApplyDiscount(decimal amount)
                {
                    return amount > 100 ? amount * 0.9m : amount;
                }
            }
            """);
        repo.Write("src/Order.cs", """
            namespace Shop;

            public class Order
            {
                public List<OrderLine> Lines { get; } = new();
            }

            public class OrderLine
            {
                public decimal Price { get; set; }
            }
            """);
        repo.Write("src/Checkout.cs", """
            namespace Shop;

            public class Checkout
            {
                private readonly OrderService _service = new();

                public string Receipt(Order order)
                {
                    var total = _service.ComputeTotal(order);
                    return $"Total: {total}";
                }
            }
            """);
        CRepo(repo);
    }

    private static void CRepo(TempRepo repo)
    {
        repo.Write("native/parser.h", """
            #pragma once
            int parse_header(const char *buffer, int length);
            int checksum_block(const char *buffer, int length);
            """);
        repo.Write("native/parser.c", """
            #include "parser.h"

            int checksum_block(const char *buffer, int length)
            {
                int sum = 0;
                for (int i = 0; i < length; i++) sum += buffer[i];
                return sum;
            }

            int parse_header(const char *buffer, int length)
            {
                if (length < 4) return -1;
                return checksum_block(buffer, 4);
            }
            """);
        repo.Write("native/main.c", """
            #include "parser.h"

            int main(void)
            {
                const char header_bytes[] = "ABCD";
                return parse_header(header_bytes, 4);
            }
            """);
    }

    // Every file under the root with its bytes' hash and write time: the self-test must leave all of it as it was.
    private static Dictionary<string, (string Hash, DateTime Written)> Snapshot(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories).ToDictionary(
            f => Path.GetRelativePath(root, f),
            f => (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(f))), File.GetLastWriteTimeUtc(f)));

    private static (int Exit, string Stdout, string Stderr, string Html) SelfTest(TempRepo repo, params string[] extra) =>
        SelfTestWith(repo, TestCli.FindMcp(), extra);

    [Fact]
    public void SelfTest_MixedRepo_EveryFeaturePasses_AndTheRepoIsLeftAsItWas()
    {
        using var repo = new TempRepo();
        MixedRepo(repo);
        var before = Snapshot(repo.Root);

        var (exit, stdout, stderr, html) = SelfTest(repo);

        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");
        Assert.StartsWith("CodeCompass self-test of ", stdout);
        Assert.Contains(": PASS", stdout);
        foreach (var feature in new[] { "Indexing", "search_code", "find_definition", "search_symbols", "find_references",
                                        "find_callees", "manage_links", "find_references (deep)", "reindex" })
            Assert.Contains($"id=\"f-{new string(feature.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '-').ToArray())}\"", html);
        Assert.DoesNotContain("badge fail", html);
        Assert.Contains(repo.Root, html);                              // names are shown as they are...
        Assert.Contains("<details><summary>answer</summary>", html);   // ...and so are the answers
        Assert.Equal(before, Snapshot(repo.Root));
    }

    [Fact]
    public void SelfTest_COnlyRepo_FindCalleesIsNotApplicable_AndTheRestPasses()
    {
        using var repo = new TempRepo();
        CRepo(repo);

        var (exit, stdout, stderr, html) = SelfTest(repo);

        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("no C# in this repo", html);
        Assert.Matches(@"find_callees\s+N/A", stdout);
        Assert.Matches(@"find_definition\s+PASS", stdout);
        Assert.Matches(@"find_references\s+PASS", stdout);
    }

    [Fact]
    public void SelfTest_Anonymized_HidesNamesPathsAndAnswers()
    {
        using var repo = new TempRepo();
        MixedRepo(repo);

        var (exit, stdout, stderr, html) = SelfTest(repo, "--anonymize");

        Assert.True(exit == 0, $"exit {exit}\n{stdout}\n{stderr}");
        var report = html + stdout;
        foreach (var secret in new[] { "ComputeTotal", "SumOrderLines", "ApplyDiscount", "OrderService", "Receipt", "parse_header",
                                       "checksum_block", "OrderService.cs", "parser.c", Path.GetFileName(repo.Root), repo.Root })
            Assert.DoesNotContain(secret, report);
        Assert.DoesNotContain("<details>", html);
        Assert.Contains("sym-", html);
        Assert.Contains("repo-", stdout);
    }

    // A server that won't start still gives a report, and an anonymized one doesn't leak the error's paths into it.
    [Fact]
    public void SelfTest_ServerWontStart_StillReports_AndAnonymizedHidesTheError()
    {
        using var repo = new TempRepo();
        CRepo(repo);
        repo.Write("tools/notanexe.exe", "this is not a program");

        var (exit, stdout, stderr, html) = SelfTestWith(repo, Path.Combine(repo.Root, "tools", "notanexe.exe"), "--anonymize");

        Assert.True(exit == 2, $"exit {exit}\n{stdout}\n{stderr}");
        Assert.Contains("badge fail", html);
        foreach (var secret in new[] { Path.GetFileName(repo.Root), "notanexe" })
            Assert.DoesNotContain(secret, html + stdout);
    }

    private static (int Exit, string Stdout, string Stderr, string Html) SelfTestWith(TempRepo repo, string mcp, params string[] extra)
    {
        var html = Path.Combine(Path.GetTempPath(), "cc-selftest-" + Guid.NewGuid().ToString("N") + ".html");
        try
        {
            var (exit, stdout, stderr) = TestCli.Run(TestCli.Find(), ["selftest", repo.Root, "--mcp", mcp, "--html", html, .. extra]);
            return (exit, stdout, stderr, File.Exists(html) ? File.ReadAllText(html) : "");
        }
        finally { try { File.Delete(html); } catch { } }
    }

    [Fact]
    public void SelfTest_NotADirectory_IsAUsageError()
    {
        var (exit, _, stderr) = TestCli.Run(TestCli.Find(), "selftest", Path.Combine(Path.GetTempPath(), "cc-no-such-" + Guid.NewGuid().ToString("N")));
        Assert.Equal(1, exit);
        Assert.Contains("not a directory", stderr);
    }
}
