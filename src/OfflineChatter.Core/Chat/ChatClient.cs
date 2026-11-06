using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Net;

namespace OfflineChatter.Core.Chat;

/// <summary>Запрос к модели.</summary>
public sealed class ChatRequest
{
    public string? Model { get; set; }
    public List<ChatMessage> Messages { get; set; } = new();
    public double? Temperature { get; set; }
    public double? TopP { get; set; }
    public int? TopK { get; set; }
    public double? MinP { get; set; }
    public double? RepeatPenalty { get; set; }
    public int? MaxTokens { get; set; }
    public int? Seed { get; set; }

    /// <summary>Включить «мышление» (chat_template_kwargs.enable_thinking для llama.cpp).</summary>
    public bool? Thinking { get; set; }

    /// <summary>Описания инструментов в формате OpenAI (этап 9).</summary>
    public JArray? Tools { get; set; }

    /// <summary>Слот llama-server (для сохранения KV-кэша чата).</summary>
    public int? SlotId { get; set; }
}

/// <summary>Порция потокового ответа.</summary>
public sealed class ChatDelta
{
    public string? Content { get; set; }
    public string? Reasoning { get; set; }
    public List<ToolCall>? ToolCalls { get; set; }
}

public sealed class ChatResult
{
    public string Content { get; set; } = string.Empty;
    public string Reasoning { get; set; } = string.Empty;
    public List<ToolCall> ToolCalls { get; } = new();
    public GenerationStats Stats { get; } = new();
}

/// <summary>
/// Клиент OpenAI-совместимого /chat/completions со стримингом (SSE).
/// Работает и с локальным llama-server (timings, reasoning_content, chat_template_kwargs), и с облачными API.
/// </summary>
public sealed class ChatClient
{
    private readonly Func<Func<HttpRequestMessage>, HttpCompletionOption, CancellationToken, Task<HttpResponseMessage>> _send;
    private readonly string _baseUrl;
    private readonly string? _apiKey;
    private readonly ProviderQuirks _quirks;
    private readonly string _providerName;

    /// <param name="send">Отправка запроса: для облаков — через HttpService (прокси, TLS-уровни), для локального сервера — прямой HttpClient.</param>
    public ChatClient(Func<Func<HttpRequestMessage>, HttpCompletionOption, CancellationToken, Task<HttpResponseMessage>> send,
        string baseUrl, string? apiKey, ProviderQuirks quirks, string providerName)
    {
        _send = send;
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _quirks = quirks;
        _providerName = providerName;
    }

    public static ChatClient ForHttpService(HttpService http, string baseUrl, string? apiKey, ProviderQuirks quirks, string name) =>
        new(http.SendAsync, baseUrl, apiKey, quirks, name);

    public static ChatClient ForLocal(HttpClient client, string baseUrl, string? apiKey, string name) =>
        new((make, opt, ct) => client.SendAsync(make(), opt, ct), baseUrl, apiKey, ProviderPresets.LocalLlama, name);

    public JObject BuildBody(ChatRequest r, bool stream)
    {
        var body = new JObject { ["stream"] = stream };
        if (!string.IsNullOrEmpty(r.Model)) body["model"] = r.Model;
        body["messages"] = new JArray(r.Messages.Select(SerializeMessage));
        if (r.Temperature is { } t && _quirks.SupportsTemperature) body["temperature"] = t;
        if (r.TopP is { } tp && _quirks.SupportsTemperature) body["top_p"] = tp;
        if (r.TopK is { } tk && _quirks.SupportsTopK) body["top_k"] = tk;
        if (r.MinP is { } mp && _quirks.SupportsMinP) body["min_p"] = mp;
        if (r.RepeatPenalty is { } rp && _quirks.IsLlamaCpp && Math.Abs(rp - 1.0) > 1e-6) body["repeat_penalty"] = rp;
        if (r.MaxTokens is > 0) body[_quirks.MaxTokensField] = r.MaxTokens;
        if (r.Seed is { } seed) body["seed"] = seed;
        if (stream && _quirks.IncludeUsage) body["stream_options"] = new JObject { ["include_usage"] = true };
        if (r.Tools is { Count: > 0 } && _quirks.SupportsTools) body["tools"] = r.Tools;
        if (_quirks.IsLlamaCpp)
        {
            body["cache_prompt"] = true;
            body["timings_per_token"] = false;
            if (r.Thinking is { } th) body["chat_template_kwargs"] = new JObject { ["enable_thinking"] = th };
            if (r.SlotId is { } slot) body["id_slot"] = slot;
        }
        return body;
    }

    private JObject SerializeMessage(ChatMessage m)
    {
        var o = new JObject { ["role"] = m.Role.ToString().ToLowerInvariant() };
        var images = m.Attachments.Where(a => a.Kind == AttachmentKind.Image).ToList();
        var texts = m.Attachments.Where(a => a.Kind == AttachmentKind.Text).ToList();
        var text = m.Content;
        foreach (var t in texts)
            text += $"\n\n--- {t.Name} ---\n```\n{t.Data}\n```";

        if (images.Count > 0 && _quirks.SupportsVision)
        {
            var parts = new JArray();
            if (!string.IsNullOrEmpty(text)) parts.Add(new JObject { ["type"] = "text", ["text"] = text });
            foreach (var img in images)
                parts.Add(new JObject { ["type"] = "image_url", ["image_url"] = new JObject { ["url"] = img.DataUri } });
            o["content"] = parts;
        }
        else
        {
            o["content"] = text;
        }

        if (m.Role == ChatRole.Assistant && m.ToolCalls.Count > 0)
        {
            o["tool_calls"] = new JArray(m.ToolCalls.Select(tc => new JObject
            {
                ["id"] = tc.Id,
                ["type"] = "function",
                ["function"] = new JObject { ["name"] = tc.Name, ["arguments"] = tc.Arguments },
            }));
        }
        if (m.Role == ChatRole.Tool) o["tool_call_id"] = m.ToolCallId;
        return o;
    }

    /// <summary>Потоковая генерация. <paramref name="onDelta"/> вызывается из фонового потока.</summary>
    public async Task<ChatResult> StreamAsync(ChatRequest request, Action<ChatDelta> onDelta, CancellationToken ct)
    {
        var body = BuildBody(request, stream: true).ToString(Formatting.None);
        var result = new ChatResult();
        result.Stats.Provider = _providerName;
        result.Stats.Model = request.Model;
        var sw = Stopwatch.StartNew();
        double? firstToken = null;
        double? reasoningEnd = null;
        var toolBuilders = new SortedDictionary<int, ToolCall>();

        using var response = await _send(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Post, _baseUrl + "/chat/completions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            };
            if (!string.IsNullOrEmpty(_apiKey)) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            m.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            return m;
        }, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            var err = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpStatusException(response.StatusCode, _baseUrl, ExtractError(err));
        }

        using var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var reg = ct.Register(() => { try { stream.Dispose(); } catch (IOException) { } });
        var sse = new SseReader(stream);
        try
        {
            while (true)
            {
                var ev = await sse.ReadAsync(ct).ConfigureAwait(false);
                if (ev == null) break;
                var data = ev.Value.Data;
                if (data == "[DONE]") break;
                JObject chunk;
                try { chunk = JObject.Parse(data); }
                catch (JsonException) { continue; }

                if (chunk["error"] is JToken error) throw new HttpRequestException("Provider error: " + (error["message"] ?? error));

                var choice = chunk["choices"]?.FirstOrDefault();
                var delta = choice?["delta"];
                if (delta != null)
                {
                    var d = new ChatDelta { Content = (string?)delta["content"] };
                    foreach (var f in _quirks.ReasoningFields)
                        if (delta[f] is JValue { Type: JTokenType.String } rv) d.Reasoning = (d.Reasoning ?? string.Empty) + (string?)rv;

                    if (delta["tool_calls"] is JArray tcs)
                    {
                        foreach (var tc in tcs)
                        {
                            var idx = (int?)tc["index"] ?? 0;
                            if (!toolBuilders.TryGetValue(idx, out var b)) toolBuilders[idx] = b = new ToolCall { Arguments = string.Empty };
                            if ((string?)tc["id"] is { Length: > 0 } id) b.Id = id;
                            if ((string?)tc["function"]?["name"] is { Length: > 0 } name) b.Name = name;
                            b.Arguments += (string?)tc["function"]?["arguments"] ?? string.Empty;
                        }
                    }

                    if (!string.IsNullOrEmpty(d.Reasoning))
                    {
                        firstToken ??= sw.Elapsed.TotalMilliseconds;
                        result.Reasoning += d.Reasoning;
                    }
                    if (!string.IsNullOrEmpty(d.Content))
                    {
                        firstToken ??= sw.Elapsed.TotalMilliseconds;
                        if (result.Reasoning.Length > 0) reasoningEnd ??= sw.Elapsed.TotalMilliseconds;
                        result.Content += d.Content;
                    }
                    if (d.Content != null || d.Reasoning != null) onDelta(d);
                }

                if ((string?)choice?["finish_reason"] is { } fr) result.Stats.FinishReason = fr;
                if ((string?)chunk["model"] is { Length: > 0 } model) result.Stats.Model = model;
                ApplyUsage(chunk["usage"], result.Stats);
                ApplyTimings(chunk["timings"], result.Stats);
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is IOException or ObjectDisposedException or HttpRequestException)
        {
            throw new OperationCanceledException(ct);
        }

        foreach (var b in toolBuilders.Values)
        {
            if (string.IsNullOrEmpty(b.Id)) b.Id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            if (string.IsNullOrWhiteSpace(b.Arguments)) b.Arguments = "{}";
            result.ToolCalls.Add(b);
        }

        var s = result.Stats;
        s.TotalMs = sw.Elapsed.TotalMilliseconds;
        s.TimeToFirstTokenMs = firstToken ?? s.TotalMs;
        if (reasoningEnd is { } re) s.ReasoningMs = re - (firstToken ?? 0);
        else if (result.Reasoning.Length > 0) s.ReasoningMs = s.TotalMs - (firstToken ?? 0);
        // Облачные API не присылают timings — считаем скорость на клиенте
        if (s.TokensPerSecond == null && s.CompletionTokens > 0)
        {
            var genMs = s.TotalMs - s.TimeToFirstTokenMs;
            if (genMs > 50) s.TokensPerSecond = s.CompletionTokens / (genMs / 1000.0);
        }
        return result;
    }

    private static void ApplyUsage(JToken? usage, GenerationStats s)
    {
        if (usage == null || usage.Type != JTokenType.Object) return;
        s.PromptTokens = (int?)usage["prompt_tokens"] ?? s.PromptTokens;
        s.CompletionTokens = (int?)usage["completion_tokens"] ?? s.CompletionTokens;
        s.CachedTokens = (int?)usage["prompt_tokens_details"]?["cached_tokens"] ?? s.CachedTokens;
    }

    /// <summary>llama-server: timings в последнем чанке (prompt_n, predicted_per_second, draft_n…).</summary>
    private static void ApplyTimings(JToken? t, GenerationStats s)
    {
        if (t == null || t.Type != JTokenType.Object) return;
        s.PromptTokens = (int?)t["prompt_n"] ?? s.PromptTokens;
        s.CompletionTokens = (int?)t["predicted_n"] ?? s.CompletionTokens;
        s.PromptTokensPerSecond = (double?)t["prompt_per_second"] ?? s.PromptTokensPerSecond;
        s.TokensPerSecond = (double?)t["predicted_per_second"] ?? s.TokensPerSecond;
        s.CachedTokens = (int?)t["cache_n"] ?? s.CachedTokens;
        s.DraftTotal = (int?)t["draft_n"] ?? s.DraftTotal;
        s.DraftAccepted = (int?)t["draft_n_accepted"] ?? s.DraftAccepted;
    }

    private static string ExtractError(string body)
    {
        try
        {
            var o = JObject.Parse(body);
            return (string?)o["error"]?["message"] ?? (string?)o["error"] ?? (string?)o["message"] ?? body;
        }
        catch (JsonException)
        {
            return body;
        }
    }

    /// <summary>Список моделей провайдера (/models).</summary>
    public async Task<List<string>> ListModelsAsync(CancellationToken ct)
    {
        using var response = await _send(() =>
        {
            var m = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
            if (!string.IsNullOrEmpty(_apiKey)) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            return m;
        }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpStatusException(response.StatusCode, _baseUrl + "/models", ExtractError(text));
        var o = JToken.Parse(text);
        var arr = o is JArray a ? a : o["data"] as JArray ?? o["models"] as JArray ?? new JArray();
        return arr.Select(x => (string?)x["id"] ?? (string?)x["name"] ?? (string?)x).Where(x => !string.IsNullOrEmpty(x))
                  .Select(x => x!.StartsWith("models/", StringComparison.Ordinal) ? x.Substring(7) : x)
                  .Distinct().OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>Разделение «&lt;think&gt;…&lt;/think&gt;» в тексте для серверов, которые не выделяют рассуждения сами.</summary>
public static class ThinkSplitter
{
    public static (string reasoning, string content, bool open) Split(string text)
    {
        const string open = "<think>", close = "</think>";
        var start = text.IndexOf(open, StringComparison.Ordinal);
        if (start < 0 || text.Substring(0, start).Trim().Length > 0) return (string.Empty, text, false);
        var end = text.IndexOf(close, start, StringComparison.Ordinal);
        if (end < 0) return (text.Substring(start + open.Length), string.Empty, true);
        return (text.Substring(start + open.Length, end - start - open.Length).Trim(), text.Substring(end + close.Length).TrimStart(), false);
    }
}
