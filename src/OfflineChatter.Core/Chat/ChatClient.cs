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

    /// <summary>Упрощённо: true — средний уровень размышлений, false — выключить, null — как решит модель.</summary>
    public bool? Thinking
    {
        get => Reasoning switch { ReasoningLevel.Auto => null, ReasoningLevel.Off => false, _ => true };
        set => Reasoning = value switch { null => ReasoningLevel.Auto, true => ReasoningLevel.Medium, false => ReasoningLevel.Off };
    }

    /// <summary>Уровень размышлений.</summary>
    public ReasoningLevel Reasoning { get; set; } = ReasoningLevel.Auto;

    /// <summary>Как модель включает размышления (Auto — определить).</summary>
    public ReasoningStyle ReasoningStyle { get; set; } = ReasoningStyle.Auto;

    /// <summary>Шаблон чата модели (из /props llama-server) — для точного определения стиля.</summary>
    public string? ChatTemplate { get; set; }

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
        // Рассуждения возвращаем модели только внутри текущего цикла инструментов (после последнего вопроса):
        // так её учили, и без них Qwen 3.x после результата инструмента сбивается с формата ответа
        var lastUser = r.Messages.FindLastIndex(m => m.Role == ChatRole.User);
        body["messages"] = new JArray(r.Messages.Select((m, i) => SerializeMessage(m, echoReasoning: _quirks.EchoReasoning && i > lastUser)));
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
            // timings в каждом чанке: число токенов и скорость видны во время генерации и не теряются при остановке
            body["timings_per_token"] = stream;
            if (r.SlotId is { } slot) body["id_slot"] = slot;
        }
        AppliedReasoningStyle = Reasoning.Apply(body, r.ReasoningStyle, r.Reasoning, r.Model, r.ChatTemplate, _quirks.IsLlamaCpp, _baseUrl);
        return body;
    }

    /// <summary>Какой стиль размышлений применился в последнем запросе (для подсказки в интерфейсе).</summary>
    public ReasoningStyle AppliedReasoningStyle { get; private set; }

    private JObject SerializeMessage(ChatMessage m, bool echoReasoning = false)
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
            if (echoReasoning && !string.IsNullOrEmpty(m.Reasoning)) o["reasoning_content"] = m.Reasoning;
        }
        if (m.Role == ChatRole.Tool) o["tool_call_id"] = m.ToolCallId;
        return o;
    }

    /// <summary>Потоковая генерация. <paramref name="onDelta"/> вызывается из фонового потока.</summary>
    /// <param name="result">
    /// Куда складывать ответ. Вызывающий может передать свой объект: тогда текст, рассуждения и статистика
    /// (в том числе живые числа токенов) доступны во время генерации и остаются при остановке или ошибке.
    /// </param>
    public async Task<ChatResult> StreamAsync(ChatRequest request, Action<ChatDelta> onDelta, CancellationToken ct, ChatResult? result = null)
    {
        var body = BuildBody(request, stream: true).ToString(Formatting.None);
        result ??= new ChatResult();
        result.Stats.Provider = _providerName;
        result.Stats.Model = request.Model;
        var timing = new StreamTiming();
        try
        {
            await StreamCoreAsync(body, onDelta, result, timing, ct).ConfigureAwait(false);
        }
        finally
        {
            // И при остановке, и при обрыве связи: время, скорость и число токенов — по тому, что успело прийти
            FinishStats(request, result, timing);
        }
        return result;
    }

    private sealed class StreamTiming
    {
        public readonly Stopwatch Clock = Stopwatch.StartNew();
        public double? FirstToken;
        public double? ReasoningEnd;
    }

    private void FinishStats(ChatRequest request, ChatResult result, StreamTiming timing)
    {
        var s = result.Stats;
        s.TotalMs = timing.Clock.Elapsed.TotalMilliseconds;
        s.TimeToFirstTokenMs = timing.FirstToken ?? s.TotalMs;
        if (timing.ReasoningEnd is { } re) s.ReasoningMs = re - (timing.FirstToken ?? 0);
        else if (result.Reasoning.Length > 0) s.ReasoningMs = s.TotalMs - (timing.FirstToken ?? 0);

        // Провайдер не прислал usage (не умеет, выключено или поток оборван) — оцениваем по тексту
        var generated = result.Content.Length > 0 || result.Reasoning.Length > 0 || result.ToolCalls.Count > 0;
        if (s.CompletionTokens <= 0 && generated)
        {
            s.CompletionTokens = TokenEstimator.Estimate(result.Content) + TokenEstimator.Estimate(result.Reasoning)
                                 + result.ToolCalls.Sum(t => TokenEstimator.Estimate(t.Name) + TokenEstimator.Estimate(t.Arguments));
            s.Estimated = true;
        }
        if (s.PromptTokens <= 0 && timing.FirstToken != null)
        {
            s.PromptTokens = TokenEstimator.EstimatePrompt(request.Messages);
            s.Estimated = true;
        }

        // Облачные API не присылают timings — считаем скорость на клиенте
        if (s.TokensPerSecond == null && s.CompletionTokens > 0)
        {
            var genMs = s.TotalMs - s.TimeToFirstTokenMs;
            if (genMs > 50) s.TokensPerSecond = s.CompletionTokens / (genMs / 1000.0);
        }
    }

    private async Task StreamCoreAsync(string body, Action<ChatDelta> onDelta, ChatResult result, StreamTiming timing, CancellationToken ct)
    {
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
        try
        {
            // Сервер или прокси проигнорировал stream=true и прислал обычный JSON: без этого разбора ответ был бы пустым
            var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
            if (media.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                using var reader = new StreamReader(stream, new UTF8Encoding(false));
                var whole = await reader.ReadToEndAsync().ConfigureAwait(false);
                if (TryParseObject(whole) is { } single)
                {
                    ApplyChunk(single, onDelta, result, timing, toolBuilders);
                }
                else
                {
                    // Поток с неверным заголовком (SSE или JSON по строке) — читаем как поток
                    using var copy = new MemoryStream(Encoding.UTF8.GetBytes(whole));
                    await ReadEventsAsync(new SseReader(copy), onDelta, result, timing, toolBuilders, ct).ConfigureAwait(false);
                }
            }
            else
            {
                await ReadEventsAsync(new SseReader(stream), onDelta, result, timing, toolBuilders, ct).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ct.IsCancellationRequested && ex is IOException or ObjectDisposedException or HttpRequestException)
        {
            throw new OperationCanceledException(ct);
        }

        foreach (var b in toolBuilders.Values)
        {
            if (string.IsNullOrEmpty(b.Name)) continue; // обрывок без имени вызвать нельзя
            if (string.IsNullOrEmpty(b.Id)) b.Id = "call_" + Guid.NewGuid().ToString("N").Substring(0, 12);
            if (string.IsNullOrWhiteSpace(b.Arguments)) b.Arguments = "{}";
            result.ToolCalls.Add(b);
        }
    }

    private async Task ReadEventsAsync(SseReader sse, Action<ChatDelta> onDelta, ChatResult result, StreamTiming timing,
        SortedDictionary<int, ToolCall> toolBuilders, CancellationToken ct)
    {
        while (true)
        {
            var ev = await sse.ReadAsync(ct).ConfigureAwait(false);
            if (ev == null) break;
            var data = ev.Value.Data.Trim();
            if (data == "[DONE]") break;
            if (TryParseObject(data) is { } chunk) ApplyChunk(chunk, onDelta, result, timing, toolBuilders);
        }
    }

    private static JObject? TryParseObject(string text)
    {
        var t = text.Trim();
        if (t.Length == 0 || t[0] != '{') return null;
        try { return JObject.Parse(t); }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Один чанк потока (или весь ответ без потока). Поля могут прийти как null или не того типа — разные
    /// «совместимые» серверы — поэтому всё читается без исключений: текст из delta, а если её нет — из message или text.
    /// </summary>
    private void ApplyChunk(JObject chunk, Action<ChatDelta> onDelta, ChatResult result, StreamTiming timing, SortedDictionary<int, ToolCall> toolBuilders)
    {
        if (chunk["error"] is { Type: not JTokenType.Null } error)
            throw new HttpRequestException("Provider error: " + ((error as JObject)?["message"] ?? error));

        var sw = timing.Clock;
        var choice = (chunk["choices"] as JArray)?.FirstOrDefault() as JObject;
        var delta = choice?["delta"] as JObject ?? choice?["message"] as JObject;
        if (delta == null && Str(choice?["text"]) is { } legacyText) delta = new JObject { ["content"] = legacyText };
        if (delta != null)
        {
            var (content, thinking) = TextParts(delta["content"]);
            var d = new ChatDelta { Content = content, Reasoning = thinking };
            foreach (var f in _quirks.ReasoningFields)
                if (TextParts(delta[f]) is var (rv, rt) && (rv ?? rt) is { } r) d.Reasoning = (d.Reasoning ?? string.Empty) + r;

            if (delta["tool_calls"] is JArray tcs)
            {
                var position = 0;
                foreach (var tc in tcs.OfType<JObject>())
                {
                    // Без index (весь ответ без потока, часть серверов) — по порядку в массиве, а не все в один вызов
                    var idx = Int(tc["index"]) ?? position;
                    position++;
                    if (!toolBuilders.TryGetValue(idx, out var b)) toolBuilders[idx] = b = new ToolCall { Arguments = string.Empty };
                    var fn = tc["function"] as JObject;
                    if (Str(tc["id"]) is { Length: > 0 } id) b.Id = id;
                    if (Str(fn?["name"]) is { Length: > 0 } name) b.Name = name;
                    // Аргументы обычно строка JSON, но бывают и объектом
                    b.Arguments += fn?["arguments"] switch
                    {
                        JValue { Type: JTokenType.String } s => (string?)s,
                        JObject o => o.ToString(Formatting.None),
                        _ => null,
                    } ?? string.Empty;
                }
            }

            if (!string.IsNullOrEmpty(d.Reasoning))
            {
                timing.FirstToken ??= sw.Elapsed.TotalMilliseconds;
                result.Reasoning += d.Reasoning;
            }
            if (!string.IsNullOrEmpty(d.Content))
            {
                timing.FirstToken ??= sw.Elapsed.TotalMilliseconds;
                if (result.Reasoning.Length > 0) timing.ReasoningEnd ??= sw.Elapsed.TotalMilliseconds;
                result.Content += d.Content;
            }
            if (toolBuilders.Count > 0) timing.FirstToken ??= sw.Elapsed.TotalMilliseconds;
            if (d.Content != null || d.Reasoning != null) onDelta(d);
        }

        if (Str(choice?["finish_reason"]) is { } fr) result.Stats.FinishReason = fr;
        if (Str(chunk["model"]) is { Length: > 0 } model) result.Stats.Model = model;
        ApplyUsage(chunk["usage"], result.Stats);
        ApplyTimings(chunk["timings"], result.Stats);
    }

    /// <summary>
    /// Текст поля: строка или массив частей ([{type:"text",text:"…"}], Mistral и прокси к мультимодальным API).
    /// Части с размышлениями (type thinking/reasoning) возвращаются отдельно.
    /// </summary>
    private static (string? text, string? thinking) TextParts(JToken? t)
    {
        if (t is JValue { Type: JTokenType.String } v) return ((string?)v, null);
        if (t is not JArray parts) return (null, null);
        StringBuilder? text = null, thinking = null;
        foreach (var p in parts)
        {
            if (p is JValue { Type: JTokenType.String } plain)
            {
                (text ??= new StringBuilder()).Append((string?)plain);
                continue;
            }
            if (p is not JObject o) continue;
            var type = Str(o["type"]) ?? "text";
            if (type is "thinking" or "reasoning")
            {
                var inner = o["thinking"] ?? o["reasoning"] ?? o["text"];
                var (it, ith) = TextParts(inner);
                if ((it ?? ith) is { } r) (thinking ??= new StringBuilder()).Append(r);
            }
            else if (Str(o["text"]) is { } s)
            {
                (text ??= new StringBuilder()).Append(s);
            }
        }
        return (text?.ToString(), thinking?.ToString());
    }

    private static string? Str(JToken? t) => t is JValue { Type: JTokenType.String } v ? (string?)v : null;

    private static int? Int(JToken? t) => t is JValue { Type: JTokenType.Integer or JTokenType.Float } v ? (int)Math.Round((double)v) : null;

    private static double? Dbl(JToken? t) => t is JValue { Type: JTokenType.Integer or JTokenType.Float } v ? (double)v : null;

    /// <summary>
    /// usage приходит в последнем чанке (OpenAI), в каждом чанке нарастающим итогом (Gemini, Anthropic) или отдельным
    /// чанком без choices. Числа за один запрос только растут, поэтому берём максимум: порядок чанков не важен.
    /// </summary>
    internal static void ApplyUsage(JToken? usage, GenerationStats s)
    {
        if (usage is not JObject u) return;
        var prompt = Int(u["prompt_tokens"]) ?? Int(u["input_tokens"]);
        var completion = Int(u["completion_tokens"]) ?? Int(u["output_tokens"]);
        var reasoning = Int((u["completion_tokens_details"] as JObject)?["reasoning_tokens"]);
        // Gemini не включает размышления в completion_tokens, но включает в total_tokens
        if (prompt is { } p && completion is { } c && Int(u["total_tokens"]) is { } total && total > p + c)
        {
            reasoning ??= total - p - c;
            completion = total - p;
        }
        if (prompt is { } pt) s.PromptTokens = Math.Max(s.PromptTokens, pt);
        if (completion is { } ct) s.CompletionTokens = Math.Max(s.CompletionTokens, ct);
        if (reasoning is > 0) s.ReasoningTokens = reasoning;
        var cached = Int((u["prompt_tokens_details"] as JObject)?["cached_tokens"]) ?? Int(u["prompt_cache_hit_tokens"]) ?? Int(u["cache_read_input_tokens"]);
        if (cached != null) s.CachedTokens = cached;
        if (prompt != null || completion != null) s.Estimated = false;
    }

    /// <summary>
    /// llama-server: timings (в каждом чанке при timings_per_token и в последнем). prompt_n — только заново
    /// обработанная часть промпта: при попадании в кэш это несколько токенов, а не весь промпт. Полный размер —
    /// cache_n + prompt_n (или usage.prompt_tokens у сборок без cache_n).
    /// </summary>
    internal static void ApplyTimings(JToken? timings, GenerationStats s)
    {
        if (timings is not JObject t) return;
        var evaluated = Int(t["prompt_n"]);
        var cached = Int(t["cache_n"]);
        if (evaluated is >= 0) s.PromptEvalTokens = evaluated;
        if (cached is >= 0) s.CachedTokens = cached;
        var prompt = Math.Max(0, evaluated ?? 0) + Math.Max(0, cached ?? 0);
        if (prompt > 0) s.PromptTokens = Math.Max(s.PromptTokens, prompt);
        if (Int(t["predicted_n"]) is { } predicted && predicted > 0) s.CompletionTokens = Math.Max(s.CompletionTokens, predicted);
        s.PromptTokensPerSecond = Dbl(t["prompt_per_second"]) ?? s.PromptTokensPerSecond;
        s.TokensPerSecond = Dbl(t["predicted_per_second"]) ?? s.TokensPerSecond;
        s.DraftTotal = Int(t["draft_n"]) ?? s.DraftTotal;
        s.DraftAccepted = Int(t["draft_n_accepted"]) ?? s.DraftAccepted;
        if (prompt > 0) s.Estimated = false;
    }

    /// <summary>
    /// Проверка связи с эндпоинтом: лёгкий запрос списка моделей с тем же ключом, прокси и TLS, что и чат.
    /// Исключений не бросает — причина сбоя возвращается в <see cref="EndpointStatus.Error"/>.
    /// </summary>
    public async Task<EndpointStatus> ProbeAsync(CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var response = await _send(() =>
            {
                var m = new HttpRequestMessage(HttpMethod.Get, _baseUrl + "/models");
                if (!string.IsNullOrEmpty(_apiKey)) m.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                return m;
            }, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            var state = code switch
            {
                >= 200 and < 300 => EndpointState.Online,
                401 or 403 => EndpointState.Unauthorized,
                // Адрес отвечает, но списка моделей у него нет — для чата это не помеха
                404 or 405 => EndpointState.Online,
                _ => EndpointState.Error,
            };
            string? error = null;
            if (state is EndpointState.Unauthorized or EndpointState.Error)
            {
                var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
                error = ExtractError(text);
                if (error.Length > 200) error = error.Substring(0, 200) + "…";
            }
            return new EndpointStatus(state, code, sw.Elapsed.TotalMilliseconds, error);
        }
        catch (OfflineModeException)
        {
            return new EndpointStatus(EndpointState.Offline, null, null, null);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new EndpointStatus(EndpointState.Unknown, null, null, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException)
        {
            return new EndpointStatus(EndpointState.Unreachable, null, null, ex.GetBaseException().Message);
        }
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

/// <summary>
/// Отделение рассуждений от ответа в тексте — для серверов и моделей, которые не выделяют их сами
/// (reasoning_content пустой). Поддерживаются обёртки:
/// &lt;think&gt;…&lt;/think&gt; (DeepSeek-R1, Qwen), &lt;thinking&gt;…&lt;/thinking&gt;,
/// &lt;|channel&gt;thought…&lt;channel|&gt; (Gemma 4), &lt;|channel|&gt;analysis&lt;|message|&gt;…&lt;|end|&gt; (GPT-OSS).
/// Пока закрывающего тега нет, весь текст считается рассуждениями (open = true).
/// </summary>
public static class ThinkSplitter
{
    private static readonly (string open, string close)[] Wrappers =
    {
        ("<think>", "</think>"),
        ("<thinking>", "</thinking>"),
        ("<|channel>thought", "<channel|>"),
        ("<|channel|>analysis<|message|>", "<|end|>"),
    };

    /// <summary>Похоже ли начало текста на обёртку рассуждений (для решения, разбирать ли поток).</summary>
    public static bool StartsWithWrapper(string text)
    {
        var t = text.TrimStart();
        return Wrappers.Any(w => t.StartsWith(w.open, StringComparison.Ordinal) || w.open.StartsWith(t, StringComparison.Ordinal) && t.Length > 0);
    }

    public static (string reasoning, string content, bool open) Split(string text)
    {
        foreach (var (openTag, closeTag) in Wrappers)
        {
            var start = text.IndexOf(openTag, StringComparison.Ordinal);
            if (start < 0 || text.Substring(0, start).Trim().Length > 0) continue;
            var end = text.IndexOf(closeTag, start + openTag.Length, StringComparison.Ordinal);
            if (end < 0) return (text.Substring(start + openTag.Length).TrimStart(), string.Empty, true);
            var content = StripFinalChannel(text.Substring(end + closeTag.Length).TrimStart());
            return (text.Substring(start + openTag.Length, end - start - openTag.Length).Trim(), content, false);
        }
        return (string.Empty, text, false);
    }

    /// <summary>GPT-OSS после рассуждений открывает канал ответа: «&lt;|start|&gt;assistant&lt;|channel|&gt;final&lt;|message|&gt;».</summary>
    private static string StripFinalChannel(string content)
    {
        const string marker = "<|channel|>final<|message|>";
        var i = content.IndexOf(marker, StringComparison.Ordinal);
        if (i >= 0 && i < 64) content = content.Substring(i + marker.Length);
        return content.Replace("<|return|>", string.Empty).TrimStart();
    }
}