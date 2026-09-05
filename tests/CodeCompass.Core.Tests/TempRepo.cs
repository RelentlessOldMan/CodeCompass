using System;
using System.Collections.Generic;
using System.IO;
using CodeCompass.Core.Ignore;
using CodeCompass.Core.Walking;

namespace CodeCompass.Core.Tests;

/// <summary>A throwaway directory tree for tests; deletes itself on Dispose.</summary>
public sealed class TempRepo : IDisposable
{
    public string Root { get; }

    public TempRepo()
    {
        Root = Path.Combine(Path.GetTempPath(), "cc-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Root);
    }

    public void Write(string relativePath, string content)
    {
        var full = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, content); // UTF-8, no BOM
    }

    public List<(string relPath, string fullPath)> Docs()
    {
        var walker = new FileWalker(new IgnoreRules());
        var list = new List<(string, string)>();
        foreach (var f in walker.Walk(Root))
            list.Add((f.RelativePath, f.FullPath));
        return list;
    }

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); } catch { /* best effort */ }
    }
}
