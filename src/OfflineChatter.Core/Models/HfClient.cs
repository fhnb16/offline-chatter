using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Models;

public sealed record HfModelSummary(
    string Id,
    long Downloads,
    long Likes,
    DateTime? LastModified,
    string? PipelineTag,
    bool Gated,
    long? Parameters,
    int? ContextLength,
    string? Architecture,
    long? TotalFileSize)
{
    public bool IsVision => PipelineTag is "image-text-to-text" or "image-to-text" || Architecture?.Contains("vl") == true;
    public string Author => Id.Contains('/') ? Id.Substring(0, Id.IndexOf('/')) : string.Empty;
    public string Name => Id.Contains('/') ? Id.Substring(Id.IndexOf('/') + 1) : Id;
}

public sealed record HfFile(string Path, long Size, string? Sha256);

/// <summary>Вариант модели: один квант (возможно из нескольких шардов).</summary>
public sealed record HfQuantGroup(string Key, QuantInfo? Quant, IReadOnlyList<HfFile> Files)
{
    public long Size => Files.Sum(f => f.Size);
    public HfFile Primary => Files[0];
    public bool IsSharded => Files.Count > 1;
}

public sealed record HfRepoFiles(IReadOnlyList<HfQuantGroup> Quants, IReadOnlyList<HfFile> Mmproj)
{
    public HfFile? PreferredMmproj => Mmproj.OrderBy(f => QuantCatalog.MmprojPreference(f.Path)).FirstOrDefault();
}

public enum HfSort
{
    Downloads,
    Likes,
    Trending,
    Updated,
}

/// <summary>Клиент Hugging Face Hub (или зеркала): поиск GGUF, файлы репозитория, README, ссылки на скачивание.</summary>
public sealed class HfClient
{
    public const string DefaultModelRepo = "NotCherry/Qwen3.5-0.8B-weight-space-programming-GGUF";

    private readonly HttpService _http;
    private readonly Func<string?> _token;

    public HfClient(HttpService http, Func<string?> token)
    {
        _http = http;
        _token = token;
    }

    private string Endpoint => _http.Settings.HfEndpoint.TrimEnd('/');

    private void Authorize(HttpRequestMessage r)
    {
        var t = _token();
        if (!string.IsNullOrEmpty(t)) r.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", t);
    }

    public IDictionary<string, string> AuthHeaders()
    {
        var d = new Dictionary<string, string>();
        var t = _token();
        if (!string.IsNullOrEmpty(t)) d["Authorization"] = "Bearer " + t;
        return d;
    }

    public async Task<IReadOnlyList<HfModelSummary>> SearchAsync(string? query, bool visionOnly, HfSort sort, int limit, CancellationToken ct)
    {
        var sortKey = sort switch
        {
            HfSort.Likes => "likes",
            HfSort.Trending => "trendingScore",
            HfSort.Updated => "lastModified",
            _ => "downloads",
        };
        var url = $"{Endpoint}/api/models?filter=gguf&sort={sortKey}&direction=-1&limit={limit}" +
                  "&expand[]=gguf&expand[]=downloads&expand[]=likes&expand[]=pipeline_tag&expand[]=lastModified&expand[]=gated";
        if (!string.IsNullOrWhiteSpace(query)) url += "&search=" + Uri.EscapeDataString(query!.Trim());
        if (visionOnly) url += "&pipeline_tag=image-text-to-text";
        var json = await _http.GetJsonAsync(url, ct, Authorize).ConfigureAwait(false);
        return ((JArray)json).Select(ParseSummary).ToList();
    }

    public async Task<HfModelSummary> GetModelAsync(string repo, CancellationToken ct)
    {
        var url = $"{Endpoint}/api/models/{repo}?expand[]=gguf&expand[]=downloads&expand[]=likes&expand[]=pipeline_tag&expand[]=lastModified&expand[]=gated";
        return ParseSummary(await _http.GetJsonAsync(url, ct, Authorize).ConfigureAwait(false));
    }

    internal static HfModelSummary ParseSummary(JToken m)
    {
        var gguf = m["gguf"];
        var gatedToken = m["gated"];
        var gated = gatedToken != null && gatedToken.Type != JTokenType.Null && !(gatedToken.Type == JTokenType.Boolean && !(bool)gatedToken);
        return new HfModelSummary(
            (string?)m["id"] ?? (string?)m["modelId"] ?? "?",
            (long?)m["downloads"] ?? 0,
            (long?)m["likes"] ?? 0,
            (DateTime?)m["lastModified"],
            (string?)m["pipeline_tag"],
            gated,
            (long?)gguf?["total"],
            (int?)gguf?["context_length"],
            (string?)gguf?["architecture"],
            (long?)gguf?["totalFileSize"]);
    }

    public async Task<HfRepoFiles> GetFilesAsync(string repo, CancellationToken ct)
    {
        var json = await _http.GetJsonAsync($"{Endpoint}/api/models/{repo}/tree/main?recursive=true", ct, Authorize).ConfigureAwait(false);
        var files = ((JArray)json)
            .Where(t => (string?)t["type"] == "file")
            .Select(t => new HfFile((string)t["path"]!, (long?)t["lfs"]?["size"] ?? (long?)t["size"] ?? 0, (string?)t["lfs"]?["oid"]))
            .Where(f => f.Path.EndsWith(".gguf", StringComparison.OrdinalIgnoreCase))
            .ToList();
        return GroupFiles(files);
    }

    /// <summary>Группировка: mmproj отдельно; шарды одного кванта — вместе (по общему префиксу и подпапке).</summary>
    public static HfRepoFiles GroupFiles(IEnumerable<HfFile> files)
    {
        var mmproj = new List<HfFile>();
        var groups = new Dictionary<string, List<HfFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            if (QuantCatalog.IsMmproj(f.Path))
            {
                mmproj.Add(f);
                continue;
            }
            var shard = QuantCatalog.Shard(f.Path);
            var key = shard == null ? f.Path : System.Text.RegularExpressions.Regex.Replace(f.Path, @"-\d{5}-of-\d{5}\.gguf$", string.Empty, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (!groups.TryGetValue(key, out var list)) groups[key] = list = new List<HfFile>();
            list.Add(f);
        }

        var quants = groups.Select(g =>
            {
                var ordered = g.Value.OrderBy(f => QuantCatalog.Shard(f.Path)?.index ?? 0).ToList();
                return new HfQuantGroup(g.Key, QuantCatalog.FromFileName(System.IO.Path.GetFileName(ordered[0].Path)), ordered);
            })
            .OrderBy(q => q.Quant?.QualityRank ?? 99)
            .ThenBy(q => q.Size)
            .ToList();
        return new HfRepoFiles(quants, mmproj);
    }

    public string FileUrl(string repo, string path) =>
        $"{Endpoint}/{repo}/resolve/main/{string.Join("/", path.Split('/').Select(Uri.EscapeDataString))}";

    public string RepoPageUrl(string repo) => $"{Endpoint}/{repo}";

    public async Task<string> GetReadmeAsync(string repo, CancellationToken ct)
    {
        try
        {
            return await _http.GetStringAsync(FileUrl(repo, "README.md"), ct, Authorize).ConfigureAwait(false);
        }
        catch (HttpStatusException ex) when ((int)ex.Status == 404)
        {
            return string.Empty;
        }
    }

    /// <summary>Метаданные GGUF удалённого файла (для оценки памяти до скачивания).</summary>
    public Task<GgufInfo> ReadRemoteGgufAsync(string repo, string path, CancellationToken ct) =>
        GgufReader.ReadRemoteAsync(_http, FileUrl(repo, path), AuthHeaders(), ct);

    public static string FormatParams(long? p)
    {
        if (p is not > 0) return string.Empty;
        var v = p.Value;
        return v >= 1_000_000_000 ? (v / 1e9).ToString(v >= 10_000_000_000 ? "0" : "0.#", CultureInfo.InvariantCulture) + "B"
             : (v / 1e6).ToString("0", CultureInfo.InvariantCulture) + "M";
    }
}
