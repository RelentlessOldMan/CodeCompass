using System.Linq;
using System.Text;
using Xunit;

namespace CodeCompass.Core.Tests;

// `codecompass refs` stopped at 200 C# references and `callees` at 100, printing the cut count as if it were all of them;
// `search` said MORE EXIST only on stderr, so `search ... > out.txt` read as complete. A cut-off answer must say so on
// stdout, where a redirected answer keeps it (the MCP tools already do).
public class CliTruncationTests
{
    [Fact]
    public void Refs_OverTheSemanticLimit_SaysMoreExist()
    {
        using var repo = new TempRepo();
        repo.Write("t.cs", "public class Target { public void Ping() { } }\n");
        var sb = new StringBuilder("class Caller {\n");
        for (int i = 0; i < 205; i++) sb.Append("  void C").Append(i).Append("(Target t) { t.Ping(); }\n");
        repo.Write("caller.cs", sb.Append("}\n").ToString());
        var exe = TestCli.Find();
        Assert.Equal(0, TestCli.Run(exe, "index", repo.Root).Exit);

        var (exit, stdout, _) = TestCli.Run(exe, "refs", repo.Root, "Ping");
        Assert.Equal(0, exit);
        Assert.Equal(200, stdout.Split('\n').Count(l => l.Contains("t.Ping()")));
        Assert.Contains("MORE EXIST", stdout);
    }

    [Fact]
    public void Callees_OverTheLimit_SaysMoreExist()
    {
        using var repo = new TempRepo();
        var sb = new StringBuilder("class W {\n");
        for (int i = 0; i < 105; i++) sb.Append("  void M").Append(i).Append("() { }\n");
        sb.Append("  void CallsAll() {");
        for (int i = 0; i < 105; i++) sb.Append(" M").Append(i).Append("();");
        repo.Write("w.cs", sb.Append(" }\n}\n").ToString());
        var exe = TestCli.Find();
        Assert.Equal(0, TestCli.Run(exe, "index", repo.Root).Exit);

        var (exit, stdout, _) = TestCli.Run(exe, "callees", repo.Root, "CallsAll");
        Assert.Equal(0, exit);
        Assert.Equal(100, stdout.Split('\n').Count(l => l.StartsWith("w.cs:")));
        Assert.Contains("MORE EXIST", stdout);
    }

    [Fact]
    public void Search_OverTheLimit_SaysMoreExist_OnStdout()
    {
        using var repo = new TempRepo();
        var sb = new StringBuilder();
        for (int i = 0; i < 205; i++) sb.Append("// TruncationProbe ").Append(i).Append('\n');
        repo.Write("probe.cs", sb.ToString());
        var exe = TestCli.Find();
        Assert.Equal(0, TestCli.Run(exe, "index", repo.Root).Exit);

        var (exit, stdout, stderr) = TestCli.Run(exe, "search", repo.Root, "TruncationProbe");
        Assert.Equal(0, exit);
        Assert.Contains("MORE EXIST", stdout);
        Assert.Contains("MORE EXIST", stderr); // still there too: scripts read the summary from stderr
    }

    // A short name skips the name search, but the C# pass it does run can still have gaps - say them, as the MCP tool does.
    [Fact]
    public void Refs_ForAShortName_StillSaysWhatTheSemanticPassCouldntRead()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "public class A { public void Do() { } }\n");
        repo.Write("b.cs", "class B { void N(A a) { a.Do(); } }\n");
        var exe = TestCli.Find();
        Assert.Equal(0, TestCli.Run(exe, "index", repo.Root).Exit);

        string stdout;
        using (new System.IO.FileStream(repo.FullPath("b.cs"), System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.None))
            stdout = TestCli.Run(exe, "refs", repo.Root, "Do").Stdout;
        Assert.Contains("too short for the text index", stdout);
        Assert.Contains("could not be read when the semantic model was built", stdout);
    }

    // Under one trigram the index can't narrow anything: every .cs file was a candidate, each re-read for #if, and an #if
    // anywhere in the repo printed an INCOMPLETE note naming unrelated files. Skipped now, and said so (as the MCP tool does).
    [Fact]
    public void Callees_ForAShortName_SkipsTheConditionalScan_AndSaysSo()
    {
        using var repo = new TempRepo();
        repo.Write("a.cs", "class A { void Do() { X(); } void X() { } }\n");
        repo.Write("other.cs", "class O { void Q() {\n#if DEBUG\n Q();\n#endif\n } }\n");
        var exe = TestCli.Find();
        Assert.Equal(0, TestCli.Run(exe, "index", repo.Root).Exit);

        var (exit, stdout, _) = TestCli.Run(exe, "callees", repo.Root, "Do");
        Assert.Equal(0, exit);
        Assert.Contains("a.cs", stdout);                 // X, the callee
        Assert.DoesNotContain("other.cs", stdout);       // no note naming an unrelated file
        Assert.Contains("too short for the text index", stdout);
    }
}
