using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class SseReaderTests
{
    [Fact]
    public async Task Parses_Events_Comments_Multiline_And_Crlf()
    {
        var text = ": ping\r\n\r\ndata: {\"a\":1}\r\n\r\nevent: x\ndata: line1\ndata: line2\n\ndata:no-space\n\ndata: [DONE]\n\n";
        var r = new SseReader(new MemoryStream(Encoding.UTF8.GetBytes(text)));
        var list = new List<(string?, string)>();
        while (await r.ReadAsync(CancellationToken.None) is { } ev) list.Add(ev);
        Assert.Equal(new (string?, string)[] { (null, "{\"a\":1}"), ("x", "line1\nline2"), (null, "no-space"), (null, "[DONE]") }, list);
    }
}

public class ChatSessionTests
{
    [Fact]
    public void Branching_And_Active_Path()
    {
        var chat = new ChatSession();
        var u1 = chat.Append(new ChatMessage { Role = ChatRole.User, Content = "q1" });
        var a1 = chat.Append(new ChatMessage { Role = ChatRole.Assistant, Content = "a1" });
        // перегенерация: второй вариант ответа на тот же вопрос
        var a1b = chat.Append(new ChatMessage { Role = ChatRole.Assistant, Content = "a1b", ParentId = u1.Id, CreatedAt = a1.CreatedAt.AddSeconds(1) });
        Assert.Equal(new[] { "q1", "a1b" }, chat.ActivePath().Select(m => m.Content));
        Assert.Equal(2, chat.Siblings(a1).Count);

        var u2 = chat.Append(new ChatMessage { Role = ChatRole.User, Content = "q2" });
        Assert.Equal(a1b.Id, u2.ParentId);

        // переключение на первый вариант
        chat.ActiveLeafId = chat.DeepestLeaf(a1).Id;
        Assert.Equal(new[] { "q1", "a1" }, chat.ActivePath().Select(m => m.Content));

        // удаление ветки вместе с потомками
        chat.Remove(a1b);
        Assert.DoesNotContain(chat.Messages, m => m.Id == u2.Id);
        Assert.Equal(new[] { "q1", "a1" }, chat.ActivePath().Select(m => m.Content));
    }

    [Fact]
    public void Store_Roundtrip_And_Search()
    {
        using var tmp = new TempDir();
        var store = new ChatStore(tmp.Path);
        var chat = new ChatSession { Title = "Формулы" };
        chat.Append(new ChatMessage { Role = ChatRole.User, Content = "Что такое интеграл?" });
        chat.Append(new ChatMessage { Role = ChatRole.Assistant, Content = "$\\int f(x)dx$", Reasoning = "думаю" });
        store.Save(chat);
        var loaded = Assert.Single(store.LoadAll());
        Assert.Equal(2, loaded.Messages.Count);
        Assert.Equal(chat.ActiveLeafId, loaded.ActiveLeafId);
        Assert.True(ChatStore.Matches(loaded, "ИНТЕГРАЛ"));
        Assert.False(ChatStore.Matches(loaded, "дифференциал"));
    }

    [Fact]
    public void Old_Chats_With_Evaluated_Only_Prompt_Count_Are_Repaired_On_Load()
    {
        using var tmp = new TempDir();
        var store = new ChatStore(tmp.Path);
        var chat = new ChatSession { Title = "old" };
        chat.Append(new ChatMessage { Role = ChatRole.User, Content = "q" });
        // Старая запись: вход 84 при 832 токенах из кэша; новая запись уже правильная и не должна измениться
        chat.Append(new ChatMessage { Role = ChatRole.Assistant, Content = "a", Stats = new GenerationStats { PromptTokens = 84, CachedTokens = 832, CompletionTokens = 93 } });
        chat.Append(new ChatMessage { Role = ChatRole.Assistant, Content = "b", Stats = new GenerationStats { PromptTokens = 1500, PromptEvalTokens = 8, CachedTokens = 1492, CompletionTokens = 2 } });
        store.Save(chat);

        var loaded = Assert.Single(store.LoadAll());
        var old = loaded.Messages[1].Stats!;
        Assert.Equal(916, old.PromptTokens);
        Assert.Equal(84, old.PromptEvalTokens);
        Assert.Equal(1500, loaded.Messages[2].Stats!.PromptTokens);
        Assert.Equal(8, loaded.Messages[2].Stats!.PromptEvalTokens);
    }

    [Theory]
    [InlineData("<think>plan</think>\n\nAnswer", "plan", "Answer", false)]
    [InlineData("<think>still thinking", "still thinking", "", true)]
    [InlineData("No tags here", "", "No tags here", false)]
    [InlineData("Text <think>x</think>", "", "Text <think>x</think>", false)]
    public void Think_Splitter(string text, string reasoning, string content, bool open)
    {
        var (r, c, o) = ThinkSplitter.Split(text);
        Assert.Equal(reasoning, r);
        Assert.Equal(content, c);
        Assert.Equal(open, o);
    }
}

public class ChatClientTests
{
    /// <summary>Фейковый OpenAI-сервер: отдаёт заранее заданный SSE и запоминает тело запроса.</summary>
    private sealed class FakeServer : IDisposable
    {
        private readonly TcpListener _l = new(IPAddress.Loopback, 0);
        public string? LastBody;

        public FakeServer(string sse, int status = 200)
        {
            _l.Start();
            _ = Task.Run(async () =>
            {
                using var c = await _l.AcceptTcpClientAsync();
                var s = c.GetStream();
                var reader = new StreamReader(s, Encoding.UTF8);
                var length = 0;
                string? line;
                while (!string.IsNullOrEmpty(line = reader.ReadLine()))
                    if (line!.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(line.Substring(15).Trim());
                var buf = new char[length];
                var read = 0;
                while (read < length) read += reader.Read(buf, read, length - read);
                LastBody = new string(buf, 0, read);
                var payload = Encoding.UTF8.GetBytes(sse);
                var head = Encoding.ASCII.GetBytes($"HTTP/1.1 {status} X\r\nContent-Type: text/event-stream\r\nContent-Length: {payload.Length}\r\nConnection: close\r\n\r\n");
                s.Write(head, 0, head.Length);
                s.Write(payload, 0, payload.Length);
                s.Flush();
            });
        }

        public string Url => $"http://127.0.0.1:{((IPEndPoint)_l.LocalEndpoint).Port}/v1";
        public void Dispose() => _l.Stop();
    }

    private static string Chunk(object delta, string? finish = null, object? extra = null)
    {
        var o = new JObject { ["choices"] = new JArray(new JObject { ["delta"] = JObject.FromObject(delta), ["finish_reason"] = finish }) };
        if (extra != null) foreach (var p in JObject.FromObject(extra).Properties()) o[p.Name] = p.Value;
        return "data: " + o.ToString(Newtonsoft.Json.Formatting.None) + "\n\n";
    }

    [Fact]
    public async Task Streams_Reasoning_Content_Timings_And_Builds_LlamaCpp_Body()
    {
        var sse = Chunk(new { reasoning_content = "Let me " }) + Chunk(new { reasoning_content = "think." }) +
                  Chunk(new { content = "Hello" }) + Chunk(new { content = ", world" }, "stop",
                      new { timings = new { prompt_n = 12, predicted_n = 5, prompt_per_second = 300.5, predicted_per_second = 55.2 } }) +
                  "data: [DONE]\n\n";
        using var server = new FakeServer(sse);
        using var http = new HttpClient();
        var client = ChatClient.ForLocal(http, server.Url, "secret", "local");

        var deltas = new List<ChatDelta>();
        var req = new ChatRequest { Thinking = true, Temperature = 0.5, TopK = 20, MaxTokens = 100 };
        req.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "hi", Attachments = { new ChatAttachment { Kind = AttachmentKind.Image, Data = "AAAA", MimeType = "image/png" } } });
        var result = await client.StreamAsync(req, deltas.Add, CancellationToken.None);

        Assert.Equal("Hello, world", result.Content);
        Assert.Equal("Let me think.", result.Reasoning);
        Assert.Equal(4, deltas.Count);
        Assert.Equal(55.2, result.Stats.TokensPerSecond);
        Assert.Equal(12, result.Stats.PromptTokens);
        Assert.Equal("stop", result.Stats.FinishReason);
        Assert.NotNull(result.Stats.ReasoningMs);

        var body = JObject.Parse(server.LastBody!);
        Assert.True((bool)body["chat_template_kwargs"]!["enable_thinking"]!);
        Assert.Equal(20, (int)body["top_k"]!);
        Assert.Equal(100, (int)body["max_tokens"]!);
        Assert.Equal("data:image/png;base64,AAAA", (string)body["messages"]![0]!["content"]![1]!["image_url"]!["url"]!);
    }

    [Fact]
    public async Task OpenAi_Quirks_And_Tool_Call_Assembly()
    {
        var sse = Chunk(new { tool_calls = new[] { new { index = 0, id = "call_1", function = new { name = "calc", arguments = "{\"ex" } } } }) +
                  Chunk(new { tool_calls = new[] { new { index = 0, function = new { arguments = "pr\":\"2+2\"}" } } } }, "tool_calls") +
                  "data: " + new JObject { ["choices"] = new JArray(), ["usage"] = new JObject { ["prompt_tokens"] = 7, ["completion_tokens"] = 3 } }.ToString(Newtonsoft.Json.Formatting.None) + "\n\n" +
                  "data: [DONE]\n\n";
        using var server = new FakeServer(sse);
        using var http = new HttpClient();
        var quirks = OfflineChatter.Core.Chat.ProviderPresets.Get("openai").Quirks;
        var client = new ChatClient((make, opt, ct) => http.SendAsync(make(), opt, ct), server.Url, "k", quirks, "openai");
        var req = new ChatRequest { Model = "gpt", MaxTokens = 50, TopK = 5, Thinking = true };
        req.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "2+2?" });
        var result = await client.StreamAsync(req, _ => { }, CancellationToken.None);

        var call = Assert.Single(result.ToolCalls);
        Assert.Equal("calc", call.Name);
        Assert.Equal("{\"expr\":\"2+2\"}", call.Arguments);
        Assert.Equal(3, result.Stats.CompletionTokens);
        var body = JObject.Parse(server.LastBody!);
        Assert.Equal(50, (int)body["max_completion_tokens"]!);
        Assert.Null(body["top_k"]);
        Assert.Null(body["chat_template_kwargs"]);
        Assert.True((bool)body["stream_options"]!["include_usage"]!);
    }

    private static string UsageChunk(object usage, object? timings = null)
    {
        var o = new JObject { ["choices"] = new JArray(), ["usage"] = JObject.FromObject(usage) };
        if (timings != null) o["timings"] = JObject.FromObject(timings);
        return "data: " + o.ToString(Newtonsoft.Json.Formatting.None) + "\n\n";
    }

    private static async Task<ChatResult> RunAsync(string sse, ProviderQuirks? quirks = null, ChatRequest? request = null)
    {
        using var server = new FakeServer(sse);
        using var http = new HttpClient();
        var client = new ChatClient((make, opt, ct) => http.SendAsync(make(), opt, ct), server.Url, null, quirks ?? ProviderPresets.LocalLlama, "test");
        if (request == null)
        {
            request = new ChatRequest();
            request.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "Hello there, how are you today?" });
        }
        return await client.StreamAsync(request, _ => { }, CancellationToken.None);
    }

    [Fact]
    public async Task Cached_Prompt_Counts_Whole_Prompt_Not_Only_Evaluated_Part()
    {
        // llama-server кладёт usage и timings в один чанк; prompt_n — только заново обработанные токены
        var sse = Chunk(new { content = "Hi" }, "stop") +
                  UsageChunk(new { prompt_tokens = 1500, completion_tokens = 2, total_tokens = 1502 },
                      new { cache_n = 1492, prompt_n = 8, predicted_n = 2, prompt_per_second = 400.0, predicted_per_second = 50.0 }) +
                  "data: [DONE]\n\n";
        var s = (await RunAsync(sse)).Stats;
        Assert.Equal(1500, s.PromptTokens);
        Assert.Equal(8, s.PromptEvalTokens);
        Assert.Equal(1492, s.CachedTokens);
        Assert.Equal(2, s.CompletionTokens);
        Assert.Equal(1502, s.TotalTokens);
        Assert.False(s.Estimated);
    }

    [Fact]
    public async Task Timings_Without_Usage_Sum_Cache_And_Evaluated()
    {
        var sse = Chunk(new { content = "Hi" }, "stop", new { timings = new { cache_n = 100, prompt_n = 1, predicted_n = 3 } }) + "data: [DONE]\n\n";
        var s = (await RunAsync(sse)).Stats;
        Assert.Equal(101, s.PromptTokens);
        Assert.Equal(3, s.CompletionTokens);
    }

    [Fact]
    public async Task Live_Timings_Never_Lower_The_Count_And_Order_Does_Not_Matter()
    {
        // timings_per_token: числа в каждом чанке; usage раньше timings и наоборот дают один итог
        var sse = Chunk(new { content = "a" }, null, new { timings = new { cache_n = 0, prompt_n = 40, predicted_n = 1 } }) +
                  UsageChunk(new { prompt_tokens = 40, completion_tokens = 2 }) +
                  Chunk(new { content = "b" }, "stop", new { timings = new { cache_n = 0, prompt_n = 40, predicted_n = 2 } }) +
                  "data: [DONE]\n\n";
        var s = (await RunAsync(sse)).Stats;
        Assert.Equal(40, s.PromptTokens);
        Assert.Equal(2, s.CompletionTokens);
    }

    [Fact]
    public async Task Usage_With_Null_Details_And_Null_Delta_Does_Not_Fail()
    {
        var sse = "data: {\"choices\":[{\"delta\":null,\"finish_reason\":null}],\"usage\":null}\n\n" +
                  Chunk(new { content = "ok" }, "stop") +
                  "data: {\"choices\":[],\"usage\":{\"prompt_tokens\":9,\"completion_tokens\":1,\"prompt_tokens_details\":null,\"completion_tokens_details\":null}}\n\n" +
                  "data: [DONE]\n\n";
        var result = await RunAsync(sse, ProviderPresets.Get("openrouter").Quirks);
        Assert.Equal("ok", result.Content);
        Assert.Equal(9, result.Stats.PromptTokens);
        Assert.Equal(1, result.Stats.CompletionTokens);
    }

    [Fact]
    public async Task Gemini_Thoughts_And_Anthropic_Names_Are_Counted()
    {
        var gemini = Chunk(new { content = "ok" }, "stop") + UsageChunk(new { prompt_tokens = 10, completion_tokens = 5, total_tokens = 115 }) + "data: [DONE]\n\n";
        var g = (await RunAsync(gemini, ProviderPresets.Get("gemini").Quirks)).Stats;
        Assert.Equal(105, g.CompletionTokens);
        Assert.Equal(100, g.ReasoningTokens);

        var anthropic = Chunk(new { content = "ok" }, "stop") + UsageChunk(new { input_tokens = 21, output_tokens = 4, cache_read_input_tokens = 16 }) + "data: [DONE]\n\n";
        var a = (await RunAsync(anthropic, ProviderPresets.Get("anthropic").Quirks)).Stats;
        Assert.Equal(21, a.PromptTokens);
        Assert.Equal(4, a.CompletionTokens);
        Assert.Equal(16, a.CachedTokens);
    }

    [Fact]
    public async Task No_Usage_Falls_Back_To_Estimate()
    {
        var sse = Chunk(new { content = "The quick brown fox jumps over the lazy dog." }, "stop") + "data: [DONE]\n\n";
        var s = (await RunAsync(sse, ProviderPresets.Get("anthropic").Quirks)).Stats;
        Assert.True(s.Estimated);
        Assert.InRange(s.CompletionTokens, 6, 16);
        Assert.InRange(s.PromptTokens, 6, 20);
    }

    [Fact]
    public async Task Stopped_Generation_Keeps_Text_And_Stats()
    {
        var sse = Chunk(new { content = "Part one. " }, null, new { timings = new { cache_n = 0, prompt_n = 30, predicted_n = 3 } }) +
                  Chunk(new { content = "Part two." }, null, new { timings = new { cache_n = 0, prompt_n = 30, predicted_n = 6 } }) +
                  Chunk(new { content = " Never read." }, "stop") + "data: [DONE]\n\n";
        using var server = new FakeServer(sse);
        using var http = new HttpClient();
        var client = ChatClient.ForLocal(http, server.Url, null, "local");
        var req = new ChatRequest();
        req.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "x" });
        using var cts = new CancellationTokenSource();
        var partial = new ChatResult();
        var seen = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.StreamAsync(req, _ =>
        {
            if (++seen == 2) cts.Cancel();
        }, cts.Token, partial));
        Assert.Equal("Part one. Part two.", partial.Content);
        Assert.Equal(30, partial.Stats.PromptTokens);
        Assert.Equal(6, partial.Stats.CompletionTokens);
        Assert.True(partial.Stats.TotalMs > 0);
    }

    [Fact]
    public void Reasoning_Is_Echoed_Only_Inside_The_Current_Tool_Loop()
    {
        var req = new ChatRequest();
        req.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "q1" });
        req.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Reasoning = "old thoughts", ToolCalls = { new ToolCall { Id = "a", Name = "calc" } } });
        req.Messages.Add(new ChatMessage { Role = ChatRole.Tool, ToolCallId = "a", Content = "4" });
        req.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Content = "4", Reasoning = "plain answer" });
        req.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "q2" });
        req.Messages.Add(new ChatMessage { Role = ChatRole.Assistant, Reasoning = "need the tool", ToolCalls = { new ToolCall { Id = "b", Name = "calc" } } });
        req.Messages.Add(new ChatMessage { Role = ChatRole.Tool, ToolCallId = "b", Content = "9" });

        using var http = new HttpClient();
        var local = ChatClient.ForLocal(http, "http://127.0.0.1:1/v1", null, "local");
        var messages = (JArray)local.BuildBody(req, stream: true)["messages"]!;
        Assert.Null(messages[1]["reasoning_content"]);
        Assert.Null(messages[3]["reasoning_content"]);
        Assert.Equal("need the tool", (string?)messages[5]["reasoning_content"]);

        // Строгие API (OpenAI) лишнее поле не получают
        var openai = new ChatClient((make, opt, ct) => http.SendAsync(make(), opt, ct), "http://127.0.0.1:1/v1", "k", ProviderPresets.Get("openai").Quirks, "openai");
        Assert.Null(((JArray)openai.BuildBody(req, stream: true)["messages"]!)[5]["reasoning_content"]);
    }

    [Theory]
    [InlineData(200, EndpointState.Online)]
    [InlineData(401, EndpointState.Unauthorized)]
    [InlineData(404, EndpointState.Online)]
    [InlineData(503, EndpointState.Error)]
    public async Task Probe_Reports_Endpoint_State(int status, EndpointState expected)
    {
        using var server = new FakeServer("{\"error\":{\"message\":\"nope\"}}", status);
        using var http = new HttpClient();
        var client = new ChatClient((make, opt, ct) => http.SendAsync(make(), opt, ct), server.Url, "k", new ProviderQuirks(), "x");
        var probe = await client.ProbeAsync(CancellationToken.None);
        Assert.Equal(expected, probe.State);
        Assert.Equal(status, probe.HttpStatus);
        Assert.NotNull(probe.LatencyMs);
        if (expected is EndpointState.Unauthorized or EndpointState.Error) Assert.Equal("nope", probe.Error);
    }

    [Fact]
    public async Task Probe_Reports_Unreachable_And_Offline_Without_Throwing()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        using var http = new HttpClient();
        var dead = new ChatClient((make, opt, ct) => http.SendAsync(make(), opt, ct), $"http://127.0.0.1:{port}/v1", null, new ProviderQuirks(), "x");
        var probe = await dead.ProbeAsync(CancellationToken.None);
        Assert.Equal(EndpointState.Unreachable, probe.State);
        Assert.False(string.IsNullOrEmpty(probe.Error));

        var offline = new ChatClient((_, _, _) => throw new OfflineChatter.Core.Net.OfflineModeException("api.example.com"), "https://api.example.com/v1", null, new ProviderQuirks(), "x");
        Assert.Equal(EndpointState.Offline, (await offline.ProbeAsync(CancellationToken.None)).State);
    }

    [Fact]
    public async Task Http_Error_Message_Is_Extracted()
    {
        using var server = new FakeServer("{\"error\":{\"message\":\"Invalid API key\"}}", 401);
        using var http = new HttpClient();
        var client = ChatClient.ForLocal(http, server.Url, null, "x");
        var req = new ChatRequest();
        req.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = "x" });
        var ex = await Assert.ThrowsAsync<OfflineChatter.Core.Net.HttpStatusException>(() => client.StreamAsync(req, _ => { }, CancellationToken.None));
        Assert.Equal("Invalid API key", ex.Body);
    }
}
