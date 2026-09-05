using System.Text.Json;
using System.Text.Json.Serialization;

namespace CodeCompass.Bench;

public sealed class CorpusEntry
{
    public string Id { get; set; } = "";
    public string Tier { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Repo { get; set; } = "";
    public string Ref { get; set; } = "";
    public string? Commit { get; set; }
    public string Language { get; set; } = "";
    public string? Sha256 { get; set; }
    public string? Note { get; set; }

    /// <summary>The immutable ref used to fetch: the commit SHA when pinned, else the tag.</summary>
    [JsonIgnore]
    public string FetchRef => string.IsNullOrEmpty(Commit) ? Ref : Commit;

    /// <summary>GitHub codeload tarball URL, pinned to the commit SHA for reproducibility.</summary>
    [JsonIgnore]
    public string TarballUrl => $"https://codeload.github.com/{Owner}/{Repo}/tar.gz/{FetchRef}";
}

public sealed class CorpusManifest
{
    public List<CorpusEntry> Corpora { get; set; } = new();

    public static CorpusManifest Load(string path)
    {
        var json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<CorpusManifest>(json,
                   new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
               ?? new CorpusManifest();
    }
}
