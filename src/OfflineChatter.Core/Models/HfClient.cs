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

/// <summary>Задача модели (pipeline_tag на Hugging Face). Порядок — порядок в списке фильтра.</summary>
public enum HfTask
{
    /// <summary>Модели для чата: text-generation и близкие (как раньше по умолчанию).</summary>
    Chat,
    /// <summary>Понимают изображения (image-text-to-text).</summary>
    Vision,
    /// <summary>Текст в текст (text2text-generation: T5, перевод, пересказ).</summary>
    TextToText,
    /// <summary>Любой вход и выход (any-to-any: омни-модели).</summary>
    AnyToAny,
    /// <summary>Картинка из текста.</summary>
    TextToImage,
    /// <summary>Картинка из картинки (редактирование, перерисовка).</summary>
    ImageToImage,
    TextToVideo,
    ImageToVideo,
    TextToSpeech,
    SpeechToText,
    /// <summary>Эмбеддинги (feature-extraction / sentence-similarity).</summary>
    Embeddings,
    /// <summary>Без фильтра по задаче.</summary>
    All,
}

/// <summary>Чем программа может запустить модель этой задачи.</summary>
public enum HfRuntime
{
    /// <summary>llama.cpp (GGUF): чат.</summary>
    Llama,
    /// <summary>stable-diffusion.cpp: изображения.</summary>
    StableDiffusion,
    /// <summary>Встроенного движка для такой задачи нет — модель можно только скачать.</summary>
    None,
}

public static class HfTasks
{
    public static string? PipelineTag(HfTask t) => t switch
    {
        HfTask.Chat => "text-generation",
        HfTask.Vision => "image-text-to-text",
        HfTask.TextToText => "text2text-generation",
        HfTask.AnyToAny => "any-to-any",
        HfTask.TextToImage => "text-to-image",
        HfTask.ImageToImage => "image-to-image",
        HfTask.TextToVideo => "text-to-video",
        HfTask.ImageToVideo => "image-to-video",
        HfTask.TextToSpeech => "text-to-speech",
        HfTask.SpeechToText => "automatic-speech-recognition",
        HfTask.Embeddings => "feature-extraction",
        _ => null,
    };

    public static HfRuntime RuntimeFor(HfTask t) => t switch
    {
        HfTask.Chat or HfTask.Vision or HfTask.TextToText or HfTask.AnyToAny => HfRuntime.Llama,
        HfTask.TextToImage or HfTask.ImageToImage => HfRuntime.StableDiffusion,
        _ => HfRuntime.None,
    };

    /// <summary>Движок по pipeline_tag найденной модели (у модели без тега — llama.cpp, если есть GGUF).</summary>
    public static HfRuntime RuntimeFor(string? pipelineTag) => pipelineTag switch
    {
        null or "text-generation" or "image-text-to-text" or "text2text-generation" or "conversational" or "any-to-any" => HfRuntime.Llama,
        "text-to-image" or "image-to-image" or "unconditional-image-generation" => HfRuntime.StableDiffusion,
        _ => HfRuntime.None,
    };

    /// <summary>
    /// Нужен ли фильтр «только GGUF» по умолчанию: для чата — да (llama.cpp читает только GGUF), для изображений — нет
    /// (stable-diffusion.cpp загружает и safetensors).
    /// </summary>
    public static bool GgufByDefault(HfTask t) => RuntimeFor(t) != HfRuntime.StableDiffusion;
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

    public Task<IReadOnlyList<HfModelSummary>> SearchAsync(string? query, bool visionOnly, HfSort sort, int limit, CancellationToken ct) =>
        SearchAsync(query, visionOnly ? HfTask.Vision : HfTask.All, ggufOnly: true, sort, limit, ct);

    /// <summary>Поиск по задаче. <paramref name="ggufOnly"/> — только репозитории с GGUF (фильтр library=gguf).</summary>
    public async Task<IReadOnlyList<HfModelSummary>> SearchAsync(string? query, HfTask task, bool ggufOnly, HfSort sort, int limit, CancellationToken ct)
    {
        var json = await _http.GetJsonAsync(SearchUrl(Endpoint, query, task, ggufOnly, sort, limit), ct, Authorize).ConfigureAwait(false);
        return ((JArray)json).Select(ParseSummary).ToList();
    }

    public static string SearchUrl(string endpoint, string? query, HfTask task, bool ggufOnly, HfSort sort, int limit)
    {
        var sortKey = sort switch
        {
            HfSort.Likes => "likes",
            HfSort.Trending => "trendingScore",
            HfSort.Updated => "lastModified",
            _ => "downloads",
        };
        var url = $"{endpoint.TrimEnd('/')}/api/models?sort={sortKey}&direction=-1&limit={limit}" +
                  "&expand[]=gguf&expand[]=downloads&expand[]=likes&expand[]=pipeline_tag&expand[]=lastModified&expand[]=gated";
        if (ggufOnly) url += "&filter=gguf";
        if (!string.IsNullOrWhiteSpace(query)) url += "&search=" + Uri.EscapeDataString(query!.Trim());
        if (HfTasks.PipelineTag(task) is { } tag) url += "&pipeline_tag=" + tag;
        return url;
    }

    /// <summary>Все файлы весов репозитория (gguf, safetensors…), без частей формата diffusers — для моделей изображений.</summary>
    public async Task<IReadOnlyList<HfFile>> GetWeightFilesAsync(string repo, CancellationToken ct)
    {
        var json = await _http.GetJsonAsync($"{Endpoint}/api/models/{repo}/tree/main?recursive=true", ct, Authorize).ConfigureAwait(false);
        return ((JArray)json)
            .Where(t => (string?)t["type"] == "file")
            .Select(t => new HfFile((string)t["path"]!, (long?)t["lfs"]?["size"] ?? (long?)t["size"] ?? 0, (string?)t["lfs"]?["oid"]))
            .Where(f => Images.ImagePresets.IsWeightFile(f.Path) && !Images.ImagePresets.IsDiffusersPart(f.Path))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
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
