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
