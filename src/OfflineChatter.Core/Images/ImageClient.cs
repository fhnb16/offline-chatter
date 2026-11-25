using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OfflineChatter.Core.Images;

/// <summary>Запрос генерации: параметры и картинки (PNG/JPEG-байты).</summary>
public sealed class ImageRequest
{
    public ImageGenParams Params { get; set; } = new();
    /// <summary>Референсы для режима редактирования (Kontext, Qwen-Image-Edit, Qwen-Image 2.1, FLUX.2).</summary>
    public IReadOnlyList<byte[]> RefImages { get; set; } = Array.Empty<byte[]>();
    /// <summary>Исходная картинка для img2img.</summary>
    public byte[]? InitImage { get; set; }
    /// <summary>Маска перерисовки (белое — перерисовать, чёрное — оставить), одноканальная или RGB.</summary>
    public byte[]? MaskImage { get; set; }
    /// <summary>Картинки внутри промпта: ID из &lt;image:ID&gt; → байты.</summary>
    public IReadOnlyDictionary<string, byte[]> PromptImages { get; set; } = new Dictionary<string, byte[]>();
    /// <summary>Превью по шагам: "none", "proj", "tae", "vae".</summary>
    public string Preview { get; set; } = "none";
    public int PreviewInterval { get; set; } = 1;
    /// <summary>Плитки VAE для этого запроса; false — как настроен сервер.</summary>
    public bool VaeTiling { get; set; }
    public int VaeTileSize { get; set; } = 512;
}

public sealed record ImageResult(IReadOnlyList<byte[]> Images, string Endpoint, TimeSpan Elapsed, long? Seed)
{
    /// <summary>Фактические параметры от сервера (исполненная DGL-программа с зерном) — по одной строке на картинку.</summary>
    public IReadOnlyList<string?> GenerationInfo { get; init; } = Array.Empty<string?>();

    /// <summary>Сколько шло кодирование промпта и картинок; null — стадию не застали (обычно это попадание в кэш).</summary>
    public TimeSpan? ConditioningTime { get; init; }

    /// <summary>Сколько шло сэмплирование и средняя длительность шага.</summary>
    public double? SecondsPerStep { get; init; }
}

/// <summary>Снимок задания родного API: стадия, шаги текущего прохода, свежий кадр превью (PNG).</summary>
public sealed record ImageJobProgress(
    string Status,
    string Stage,
    string Detail,
    int Pass,
    int? Completed,
    int? Total,
    long StageElapsedMs,
    long ElapsedMs,
    int QueuePosition,
    byte[]? Preview,
    int PreviewStep,
    int PreviewTotal)
{
    public bool IsTerminal => Status is "completed" or "failed" or "cancelled";

    /// <summary>Разбор ответа GET /sdcpp/v1/jobs/{id}; превью декодируется только если <paramref name="withPreview"/>.</summary>
    public static ImageJobProgress Parse(JToken j, bool withPreview = true)
    {
        var p = j["progress"];
        var pv = j["preview"];
        byte[]? preview = null;
        if (withPreview && pv is JObject && (string?)pv["b64_json"] is { Length: > 0 } b64)
        {
            try { preview = Convert.FromBase64String(b64); }
            catch (FormatException) { }
        }
        return new ImageJobProgress(
            (string?)j["status"] ?? string.Empty,
            (string?)p?["stage"] ?? string.Empty,
            (string?)p?["detail"] ?? string.Empty,
            (int?)p?["pass"] ?? 0,
            p?["completed"]?.Type == JTokenType.Integer ? (int?)p["completed"] : null,
            p?["total"]?.Type == JTokenType.Integer ? (int?)p["total"] : null,
            (long?)p?["stage_elapsed_ms"] ?? 0,
            (long?)p?["elapsed_ms"] ?? 0,
            (int?)j["queue_position"] ?? 0,
            preview,
            pv is JObject ? (int?)pv["step"] ?? 0 : 0,
            pv is JObject ? (int?)pv["total_steps"] ?? 0 : 0);
    }

    /// <summary>Ключ кадра превью: тот же — картинку повторно не декодируем.</summary>
    public static string? PreviewKey(JToken j) =>
        j["preview"] is JObject pv ? $"{(int?)pv["pass"]}:{(int?)pv["step"]}:{((string?)pv["b64_json"])?.Length}" : null;
}

public sealed class ImageApiException : Exception
{
    public ImageApiException(HttpStatusCode status, string message) : base(message) => Status = status;
    public HttpStatusCode Status { get; }
}

/// <summary>
/// Клиент sd-server. Основной путь — родной асинхронный API: POST /sdcpp/v1/img_gen создаёт задание, GET /sdcpp/v1/jobs/{id}
/// отдаёт стадию, шаги и кадр превью, POST …/cancel прерывает генерацию на ближайшей контрольной точке. В одном запросе —
/// всё сразу: исходник с силой и маской, референсы, картинки внутри промпта, свои сигмы, LoRA, плитки VAE.
/// Старые сборки без /sdcpp: из текста и img2img — A1111 /sdapi/v1/*, правка — OpenAI /v1/images/edits,
/// недостающие параметры — блоком &lt;sd_cpp_extra_args&gt; в промпте; в крайнем случае /v1/images/generations.
/// </summary>
public sealed class ImageClient
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;

    public ImageClient(HttpClient http, string baseUrl)
    {
        _http = http;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    /// <summary>Пауза между опросами задания: превью обновляется по шагу, шаг на 8 ГБ — секунда и больше.</summary>
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Предел одного запроса статуса задания.</summary>
    public TimeSpan PollTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Сколько ждать подтверждения отмены, прежде чем перестать опрашивать (сервер прерывает на контрольной точке).</summary>
    public TimeSpan CancelWait { get; set; } = TimeSpan.FromSeconds(30);

    public enum Route
    {
        Txt2Img,
        Img2Img,
        Edits,
    }

    public static Route Choose(ImageRequest r) =>
        r.Params.Mode == ImageMode.Edit && r.RefImages.Count > 0 ? Route.Edits
        : r.Params.Mode == ImageMode.ImageToImage && r.InitImage != null ? Route.Img2Img
        : Route.Txt2Img;

    /// <summary>Промпт, который уйдёт серверу: с обёрткой прозрачного фона, если она включена.</summary>
    public static string EffectivePrompt(ImageGenParams p) => p.Transparent ? TransparentPrompt.Wrap(p.Prompt) : p.Prompt;

    public async Task<ImageResult> GenerateAsync(ImageRequest r, CancellationToken ct, IProgress<ImageJobProgress>? progress = null)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            var native = await TryNativeAsync(r, progress, sw, ct).ConfigureAwait(false);
            if (native != null) return native;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("sd-server did not answer in time");
        }
        return await GenerateLegacyAsync(r, sw, ct).ConfigureAwait(false);
    }

    // ---------------- Родной API ----------------

    /// <summary>null — родного API нет (старая сборка): вызывающий переходит на /sdapi и /v1.</summary>
    private async Task<ImageResult?> TryNativeAsync(ImageRequest r, IProgress<ImageJobProgress>? progress, Stopwatch sw, CancellationToken ct)
    {
        string id;
        using (var content = new StringContent(BuildNative(r).ToString(Formatting.None), Encoding.UTF8, "application/json"))
        using (var resp = await _http.PostAsync(_baseUrl + "/sdcpp/v1/img_gen", content, ct).ConfigureAwait(false))
        {
            if ((int)resp.StatusCode is 404 or 405 or 501) return null;
            var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) throw new ImageApiException(resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {ErrorText(text)}");
            id = (string?)JToken.Parse(text)["id"] ?? throw new ImageApiException(resp.StatusCode, "No job id: " + Shorten(text));
        }

        var cancelSent = false;
        DateTime? cancelAt = null;
        string? lastPreview = null;
        long? condStart = null, condEnd = null;
        long? samplingStartMs = null, samplingEndMs = null;
        int samplingSteps = 0;
        var jobUrl = _baseUrl + "/sdcpp/v1/jobs/" + Uri.EscapeDataString(id);
        while (true)
        {
            if (ct.IsCancellationRequested && !cancelSent)
            {
                cancelSent = true;
                cancelAt = DateTime.UtcNow;
                await CancelJobAsync(id).ConfigureAwait(false);
            }

            JToken job;
            // Опрос не зависит от отмены (её нужно дождаться), но и не ждёт вечно: у LocalHttp таймаута нет
            using (var pollCts = new CancellationTokenSource(PollTimeout))
            {
                try
                {
                    using var resp = await _http.GetAsync(jobUrl, pollCts.Token).ConfigureAwait(false);
                    var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new ImageApiException(resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {ErrorText(text)}");
                    job = JToken.Parse(text);
                }
                catch (TaskCanceledException) when (pollCts.IsCancellationRequested)
                {
                    throw new TimeoutException("sd-server stopped answering job status requests");
                }
            }

            var key = ImageJobProgress.PreviewKey(job);
            var snapshot = ImageJobProgress.Parse(job, withPreview: key != null && key != lastPreview);
            if (snapshot.Preview != null) lastPreview = key;

            // Кодирование промпта: от первого появления стадии до первой следующей; не застали — было быстрым или из кэша
            var stageStart = snapshot.ElapsedMs - snapshot.StageElapsedMs;
            if (snapshot.Stage == "conditioning") condStart ??= stageStart;
            else if (condStart != null && condEnd == null && snapshot.Stage.Length > 0) condEnd = stageStart;
            if (snapshot.Stage == "sampling")
            {
                samplingStartMs ??= stageStart;
                samplingEndMs = snapshot.ElapsedMs;
                samplingSteps = Math.Max(samplingSteps, snapshot.Completed ?? 0);
            }

            progress?.Report(snapshot);

            switch (snapshot.Status)
            {
                case "completed":
                {
                    var images = new List<byte[]>();
                    var infos = new List<string?>();
                    foreach (var it in job["result"]?["images"] as JArray ?? new JArray())
                    {
                        var b64 = (string?)it["b64_json"];
                        if (string.IsNullOrEmpty(b64)) continue;
                        try { images.Add(Convert.FromBase64String(StripDataUri(b64!))); }
                        catch (FormatException) { continue; }
                        var info = it["generation_info"];
                        infos.Add(info == null || info.Type == JTokenType.Null ? null : info.Type == JTokenType.String ? (string?)info : info.ToString(Formatting.None));
                    }
                    if (images.Count == 0) throw new ImageApiException(HttpStatusCode.OK, "No images in the job result");
                    TimeSpan? cond = condStart != null && condEnd != null ? TimeSpan.FromMilliseconds(Math.Max(0, condEnd.Value - condStart.Value)) : null;
                    double? spi = samplingStartMs != null && samplingEndMs != null && samplingSteps > 0
                        ? (samplingEndMs.Value - samplingStartMs.Value) / 1000.0 / samplingSteps
                        : null;
                    return new ImageResult(images, "/sdcpp/v1/img_gen", sw.Elapsed, Seed(r)) { GenerationInfo = infos, ConditioningTime = cond, SecondsPerStep = spi };
                }
                case "failed":
                    throw new ImageApiException(HttpStatusCode.OK, (string?)job["error"]?["message"] ?? (string?)job["error"]?["code"] ?? "generation failed");
                case "cancelled":
                    throw new OperationCanceledException(ct.IsCancellationRequested ? ct : CancellationToken.None);
            }

            if (cancelAt != null && DateTime.UtcNow - cancelAt.Value > CancelWait) throw new OperationCanceledException(ct);
            try { await Task.Delay(PollInterval, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { /* следующий круг отправит отмену */ }
        }
    }

    /// <summary>Прервать задание: в очереди — сразу, во время генерации — на ближайшей контрольной точке.</summary>
    public async Task CancelJobAsync(string id)
    {
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var resp = await _http.PostAsync(_baseUrl + "/sdcpp/v1/jobs/" + Uri.EscapeDataString(id) + "/cancel", new StringContent(string.Empty), cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // сервер занят или уже упал — опрос задания покажет итог
        }
    }

    public static string DataUri(byte[] image) => (IsPng(image) ? "data:image/png;base64," : "data:image/jpeg;base64,") + Convert.ToBase64String(image);

    private static string StripDataUri(string b64)
    {
        var comma = b64.IndexOf(',');
        return b64.StartsWith("data:", StringComparison.Ordinal) && comma > 0 ? b64.Substring(comma + 1) : b64;
    }

    /// <summary>Тело POST /sdcpp/v1/img_gen. Пустые поля не передаются — остаются значения сервера и рецепта.</summary>
    public static JObject BuildNative(ImageRequest r)
    {
        var p = r.Params;
        var o = new JObject
        {
            ["prompt"] = EffectivePrompt(p),
            ["negative_prompt"] = p.NegativePrompt ?? string.Empty,
            ["width"] = p.Width,
            ["height"] = p.Height,
            ["seed"] = p.Seed,
            ["batch_count"] = Math.Max(1, p.BatchCount),
            ["sample_params"] = SampleParams(p),
            ["output_format"] = "png",
            ["embed_image_metadata"] = true,
            ["preview"] = string.IsNullOrEmpty(r.Preview) ? "none" : r.Preview,
            ["preview_interval"] = Math.Max(1, r.PreviewInterval),
        };
        if (p.ClipSkip is { } cs and > 0) o["clip_skip"] = cs;
        if (!string.IsNullOrEmpty(p.CacheMode)) o["cache_mode"] = p.CacheMode;
        if (r.VaeTiling)
        {
            var tile = Math.Max(64, r.VaeTileSize);
            o["vae_tiling_params"] = new JObject { ["enabled"] = true, ["tile_size_w"] = tile, ["tile_size_h"] = tile, ["target_overlap"] = 0.5 };
        }
        if (r.InitImage != null)
        {
            o["init_image"] = DataUri(r.InitImage);
            o["strength"] = Math.Max(0.0, Math.Min(1.0, p.Strength));
        }
        if (r.MaskImage != null) o["mask_image"] = DataUri(r.MaskImage);
        if (r.RefImages.Count > 0) o["ref_images"] = new JArray(r.RefImages.Select(DataUri));
        if (r.PromptImages.Count > 0)
        {
            var pi = new JObject();
            foreach (var kv in r.PromptImages) pi[kv.Key] = DataUri(kv.Value);
            o["prompt_images"] = pi;
        }
        if (p.Loras.Count > 0)
            o["lora"] = new JArray(p.Loras.Where(l => !string.IsNullOrWhiteSpace(l.Path))
                .Select(l => new JObject { ["path"] = l.Path.Replace('\\', '/'), ["multiplier"] = l.Multiplier, ["is_high_noise"] = false }));
        return o;
    }

    private static JObject SampleParams(ImageGenParams p)
    {
        var sample = new JObject
        {
            ["sample_steps"] = p.Steps,
            ["guidance"] = new JObject { ["txt_cfg"] = p.Cfg },
        };
        if (!string.IsNullOrEmpty(p.Sampler)) sample["sample_method"] = p.Sampler;
        if (!string.IsNullOrEmpty(p.Scheduler)) sample["scheduler"] = p.Scheduler;
        if (p.FlowShift is { } fs) sample["flow_shift"] = fs;
        if (ImageSigmas.Parse(p.Sigmas) is { } sig)
        {
            // Число шагов задают сигмы — пишем его явно, чтобы метаданные и прогресс совпадали
            sample["custom_sigmas"] = new JArray(sig);
            sample["sample_steps"] = ImageSigmas.Steps(sig);
        }
        return sample;
    }

    // ---------------- Совместимые API (старые сборки) ----------------

    private async Task<ImageResult> GenerateLegacyAsync(ImageRequest r, Stopwatch sw, CancellationToken ct)
    {
        var route = Choose(r);
        try
        {
            switch (route)
            {
                case Route.Edits:
                {
                    var images = await PostEditsAsync(r, ct).ConfigureAwait(false);
                    return new ImageResult(images, "/v1/images/edits", sw.Elapsed, Seed(r));
                }
                case Route.Img2Img:
                {
                    var images = await PostJsonAsync("/sdapi/v1/img2img", BuildImg2Img(r), ct).ConfigureAwait(false);
                    return new ImageResult(images, "/sdapi/v1/img2img", sw.Elapsed, Seed(r));
                }
                default:
                {
                    try
                    {
                        var images = await PostJsonAsync("/sdapi/v1/txt2img", BuildTxt2Img(r.Params, r), ct).ConfigureAwait(false);
                        return new ImageResult(images, "/sdapi/v1/txt2img", sw.Elapsed, Seed(r));
                    }
                    catch (ImageApiException ex) when ((int)ex.Status is 400 or 404 or 405 or 422 or 501)
                    {
                        var images = await PostJsonAsync("/v1/images/generations", BuildGenerations(r.Params), ct).ConfigureAwait(false);
                        return new ImageResult(images, "/v1/images/generations", sw.Elapsed, Seed(r));
                    }
                }
            }
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("sd-server did not answer in time");
        }
    }

    private static long? Seed(ImageRequest r) => r.Params.Seed >= 0 ? r.Params.Seed : null;

    private async Task<IReadOnlyList<byte[]>> PostJsonAsync(string path, JObject body, CancellationToken ct)
    {
        using var content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_baseUrl + path, content, ct).ConfigureAwait(false);
        return await ReadImagesAsync(resp).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<byte[]>> PostEditsAsync(ImageRequest r, CancellationToken ct)
    {
        var p = r.Params;
        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(EffectivePrompt(p) + " " + BuildExtraArgs(p, r)), "prompt");
        form.Add(new StringContent(Math.Max(1, p.BatchCount).ToString(CultureInfo.InvariantCulture)), "n");
        form.Add(new StringContent(Size(p)), "size");
        form.Add(new StringContent("png"), "output_format");
        var i = 0;
        foreach (var img in r.RefImages)
        {
            var part = new ByteArrayContent(img);
            part.Headers.ContentType = new MediaTypeHeaderValue(IsPng(img) ? "image/png" : "image/jpeg");
            form.Add(part, "image[]", "ref" + (i++).ToString(CultureInfo.InvariantCulture) + (IsPng(img) ? ".png" : ".jpg"));
        }
        if (r.MaskImage != null)
        {
            var mask = new ByteArrayContent(r.MaskImage);
            mask.Headers.ContentType = new MediaTypeHeaderValue("image/png");
            form.Add(mask, "mask", "mask.png");
        }
        using var resp = await _http.PostAsync(_baseUrl + "/v1/images/edits", form, ct).ConfigureAwait(false);
        return await ReadImagesAsync(resp).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<byte[]>> ReadImagesAsync(HttpResponseMessage resp)
    {
        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new ImageApiException(resp.StatusCode, $"HTTP {(int)resp.StatusCode}: {ErrorText(text)}");
        JToken json;
        try { json = JToken.Parse(text); }
        catch (JsonException) { throw new ImageApiException(resp.StatusCode, "Unexpected answer: " + Shorten(text)); }
        var images = ParseImages(json);
        if (images.Count == 0) throw new ImageApiException(resp.StatusCode, "No images in the answer: " + Shorten(text));
        return images;
    }

    internal static string ErrorText(string body)
    {
        try
        {
            var j = JToken.Parse(body);
            return (string?)j["error"]?["message"] ?? (string?)j["error"] ?? (string?)j["detail"] ?? (string?)j["message"] ?? Shorten(body);
        }
        catch (JsonException)
        {
            return Shorten(body);
        }
    }

    private static string Shorten(string s) => s.Length > 300 ? s.Substring(0, 300) + "…" : s;

    private static bool IsPng(byte[] b) => b.Length > 4 && b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47;

    private static string Size(ImageGenParams p) => p.Width.ToString(CultureInfo.InvariantCulture) + "x" + p.Height.ToString(CultureInfo.InvariantCulture);

    /// <summary>Картинки из ответа: A1111 — images[] (base64), OpenAI — data[].b64_json, родной API — result.images[].b64_json.</summary>
    public static List<byte[]> ParseImages(JToken json)
    {
        var list = new List<byte[]>();
        IEnumerable<JToken> items = json["images"] as JArray ?? json["data"] as JArray ?? json["result"]?["images"] as JArray ?? new JArray();
        foreach (var it in items)
        {
            var b64 = it.Type == JTokenType.String ? (string?)it : (string?)it["b64_json"] ?? (string?)it["image"];
            if (string.IsNullOrEmpty(b64)) continue;
            var comma = b64!.IndexOf(',');
            if (b64.StartsWith("data:", StringComparison.Ordinal) && comma > 0) b64 = b64.Substring(comma + 1);
            try { list.Add(Convert.FromBase64String(b64)); }
            catch (FormatException) { }
        }
        return list;
    }

    public static JObject BuildTxt2Img(ImageGenParams p) => BuildTxt2Img(p, null);

    private static JObject BuildTxt2Img(ImageGenParams p, ImageRequest? r)
    {
        var o = new JObject
        {
            ["prompt"] = EffectivePrompt(p),
            ["negative_prompt"] = p.NegativePrompt ?? string.Empty,
            ["width"] = p.Width,
            ["height"] = p.Height,
            ["steps"] = p.Steps,
            ["cfg_scale"] = p.Cfg,
            ["seed"] = p.Seed,
            ["batch_size"] = Math.Max(1, p.BatchCount),
        };
        if (!string.IsNullOrEmpty(p.Sampler)) o["sampler_name"] = p.Sampler;
        if (!string.IsNullOrEmpty(p.Scheduler)) o["scheduler"] = p.Scheduler;
        if (p.ClipSkip is { } cs and > 0) o["clip_skip"] = cs;
        if (p.Loras.Count > 0) o["lora"] = new JArray(p.Loras.Select(l => new JObject { ["path"] = l.Path.Replace('\\', '/'), ["multiplier"] = l.Multiplier }));
        // flow_shift, сигм, кэша шагов и картинок в промпте в A1111 нет — родным блоком в промпте
        var extra = new JObject();
        var sample = new JObject();
        if (p.FlowShift is { } fs) sample["flow_shift"] = fs;
        if (ImageSigmas.Parse(p.Sigmas) is { } sig) sample["custom_sigmas"] = new JArray(sig);
        if (sample.Count > 0) extra["sample_params"] = sample;
        if (!string.IsNullOrEmpty(p.CacheMode)) extra["cache_mode"] = p.CacheMode;
        AddPromptImages(extra, r);
        if (extra.Count > 0) o["prompt"] = EffectivePrompt(p) + " " + Wrap(extra);
        return o;
    }

    public static JObject BuildImg2Img(ImageRequest r)
    {
        var o = BuildTxt2Img(r.Params, r);
        o["init_images"] = new JArray(Convert.ToBase64String(r.InitImage ?? Array.Empty<byte>()));
        o["denoising_strength"] = Math.Max(0, Math.Min(1, r.Params.Strength));
        if (r.MaskImage != null) o["mask"] = Convert.ToBase64String(r.MaskImage);
        return o;
    }

    public static JObject BuildGenerations(ImageGenParams p) => new()
    {
        ["prompt"] = EffectivePrompt(p) + " " + BuildExtraArgs(p),
        ["n"] = Math.Max(1, p.BatchCount),
        ["size"] = Size(p),
        ["output_format"] = "png",
    };

    private static void AddPromptImages(JObject o, ImageRequest? r)
    {
        if (r == null || r.PromptImages.Count == 0) return;
        var pi = new JObject();
        foreach (var kv in r.PromptImages) pi[kv.Key] = DataUri(kv.Value);
        o["prompt_images"] = pi;
    }

    /// <summary>
    /// Параметры, которых нет в OpenAI-запросе, — JSON родной схемы sd-server внутри промпта; сервер вырезает блок
    /// перед генерацией: &lt;sd_cpp_extra_args&gt;{...}&lt;/sd_cpp_extra_args&gt;.
    /// </summary>
    public static string BuildExtraArgs(ImageGenParams p, ImageRequest? r = null)
    {
        var o = new JObject
        {
            ["seed"] = p.Seed,
            ["width"] = p.Width,
            ["height"] = p.Height,
            ["sample_params"] = SampleParams(p),
        };
        if (!string.IsNullOrWhiteSpace(p.NegativePrompt)) o["negative_prompt"] = p.NegativePrompt;
        if (p.ClipSkip is { } cs and > 0) o["clip_skip"] = cs;
        if (!string.IsNullOrEmpty(p.CacheMode)) o["cache_mode"] = p.CacheMode;
        if (p.Loras.Count > 0) o["lora"] = new JArray(p.Loras.Select(l => new JObject { ["path"] = l.Path.Replace('\\', '/'), ["multiplier"] = l.Multiplier }));
        AddPromptImages(o, r);
        return Wrap(o);
    }

    private static string Wrap(JObject o) => "<sd_cpp_extra_args>" + o.ToString(Formatting.None) + "</sd_cpp_extra_args>";
}
