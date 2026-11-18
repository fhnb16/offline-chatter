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
    /// <summary>Референсы для режима редактирования (Kontext, Qwen-Image-Edit, FLUX.2).</summary>
    public IReadOnlyList<byte[]> RefImages { get; set; } = Array.Empty<byte[]>();
    /// <summary>Исходная картинка для img2img.</summary>
    public byte[]? InitImage { get; set; }
}

public sealed record ImageResult(IReadOnlyList<byte[]> Images, string Endpoint, TimeSpan Elapsed, long? Seed);

public sealed class ImageApiException : Exception
{
    public ImageApiException(HttpStatusCode status, string message) : base(message) => Status = status;
    public HttpStatusCode Status { get; }
}

/// <summary>
/// Клиент sd-server. Из текста и img2img — A1111-совместимый /sdapi/v1/* (все параметры отдельными полями);
/// редактирование по референсам — OpenAI /v1/images/edits (image[]), параметры — блоком &lt;sd_cpp_extra_args&gt; в промпте.
/// Если /sdapi нет или он отказал (старая сборка, незнакомое имя сэмплера), используется /v1/images/generations.
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

    public async Task<ImageResult> GenerateAsync(ImageRequest r, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
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
                        var images = await PostJsonAsync("/sdapi/v1/txt2img", BuildTxt2Img(r.Params), ct).ConfigureAwait(false);
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
        form.Add(new StringContent(p.Prompt + " " + BuildExtraArgs(p)), "prompt");
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

    public static JObject BuildTxt2Img(ImageGenParams p)
    {
        var o = new JObject
        {
            ["prompt"] = p.Prompt,
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
        // flow_shift в A1111 нет — родным блоком в промпте
        if (p.FlowShift is { } fs)
            o["prompt"] = p.Prompt + " " + Wrap(new JObject { ["sample_params"] = new JObject { ["flow_shift"] = fs } });
        return o;
    }

    public static JObject BuildImg2Img(ImageRequest r)
    {
        var o = BuildTxt2Img(r.Params);
        o["init_images"] = new JArray(Convert.ToBase64String(r.InitImage ?? Array.Empty<byte>()));
        o["denoising_strength"] = Math.Max(0, Math.Min(1, r.Params.Strength));
        return o;
    }

    public static JObject BuildGenerations(ImageGenParams p) => new()
    {
        ["prompt"] = p.Prompt + " " + BuildExtraArgs(p),
        ["n"] = Math.Max(1, p.BatchCount),
        ["size"] = Size(p),
        ["output_format"] = "png",
    };

    /// <summary>
    /// Параметры, которых нет в OpenAI-запросе, — JSON родной схемы sd-server внутри промпта; сервер вырезает блок
    /// перед генерацией: &lt;sd_cpp_extra_args&gt;{...}&lt;/sd_cpp_extra_args&gt;.
    /// </summary>
    public static string BuildExtraArgs(ImageGenParams p)
    {
        var sample = new JObject
        {
            ["sample_steps"] = p.Steps,
            ["guidance"] = new JObject { ["txt_cfg"] = p.Cfg },
        };
        if (!string.IsNullOrEmpty(p.Sampler)) sample["sample_method"] = p.Sampler;
        if (!string.IsNullOrEmpty(p.Scheduler)) sample["scheduler"] = p.Scheduler;
        if (p.FlowShift is { } fs) sample["flow_shift"] = fs;
        var o = new JObject
        {
            ["seed"] = p.Seed,
            ["width"] = p.Width,
            ["height"] = p.Height,
            ["sample_params"] = sample,
        };
        if (!string.IsNullOrWhiteSpace(p.NegativePrompt)) o["negative_prompt"] = p.NegativePrompt;
        if (p.ClipSkip is { } cs and > 0) o["clip_skip"] = cs;
        return Wrap(o);
    }

    private static string Wrap(JObject o) => "<sd_cpp_extra_args>" + o.ToString(Formatting.None) + "</sd_cpp_extra_args>";
}
