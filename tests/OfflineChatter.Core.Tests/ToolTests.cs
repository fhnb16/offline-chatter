using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Mcp;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using OfflineChatter.Core.Tools;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class CalculatorTests
{
    [Theory]
    [InlineData("1/3 + 1/6", "1/2 ≈ 0.5")]
    [InlineData("0.1 + 0.2", "3/10 ≈ 0.3")]
    [InlineData("2^100", "1267650600228229401496703205376")]
    [InlineData("(2/3)^-2", "9/4 ≈ 2.25")]
    [InlineData("20!", "2432902008176640000")]
    [InlineData("sqrt(16/9)", "4/3 ≈ 1.33333333333333")]
    [InlineData("17 % 5", "2")]
    [InlineData("-3 * (4 - 10)", "18")]
    [InlineData("2 × 3 ÷ 4", "3/2 ≈ 1.5")]
    [InlineData("2^3^2", "512")]
    public void Exact_Results(string expr, string expected) => Assert.Equal(expected, Calculator.Evaluate(expr).ToString());

    [Fact]
    public void Functions_Are_Approximate()
    {
        Assert.Equal(Math.Sqrt(2), Calculator.Evaluate("sqrt(2)").Value, 12);
        Assert.Equal(1.0, Calculator.Evaluate("sin(pi/2)").Value, 12);
        Assert.Equal(2.0, Calculator.Evaluate("log(100)").Value, 12);
        Assert.Equal(3.0, Calculator.Evaluate("log(8, 2)").Value, 12);
    }

    [Fact]
    public void Errors()
    {
        Assert.Throws<DivideByZeroException>(() => Calculator.Evaluate("1/0"));
        Assert.Throws<FormatException>(() => Calculator.Evaluate("2 +"));
        Assert.Throws<FormatException>(() => Calculator.Evaluate("foo(1)"));
    }

    [Theory]
    [InlineData(10, "km", "mi", "6.21371192237 mi")]
    [InlineData(100, "C", "F", "212 f")]
    [InlineData(1, "GiB", "MB", "1073.741824 mb")]
    [InlineData(1, "atm", "kPa", "101.325 kpa")]
    public void Units(double v, string from, string to, string expected) => Assert.Equal(expected, UnitConverter.Convert(v, from, to));

    [Fact]
    public void Incompatible_Units() => Assert.Throws<ArgumentException>(() => UnitConverter.Convert(1, "kg", "m"));
}

public class McpTests
{
    /// <summary>Поддельный сервер MCP в памяти.</summary>
    private sealed class FakeTransport : IMcpTransport
    {
        public readonly List<JObject> Sent = new();
        /// <summary>Дополнительные инструменты на второй странице списка.</summary>
        public JArray Extra = new();
        public event EventHandler<JObject>? MessageReceived;
        public event EventHandler<string>? Closed;

        public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

        public Task SendAsync(JObject m, CancellationToken ct)
        {
            Sent.Add(m);
            var method = (string?)m["method"];
            var id = m["id"];
            if (id == null) return Task.CompletedTask; // уведомления и наши ответы
            JObject result = method switch
            {
                "initialize" => new JObject
                {
                    ["protocolVersion"] = McpClient.ProtocolVersion,
                    ["serverInfo"] = new JObject { ["name"] = "fake", ["version"] = "1.0" },
                    ["capabilities"] = new JObject { ["tools"] = new JObject(), ["prompts"] = new JObject() },
                },
                "tools/list" when m["params"]?["cursor"] == null => new JObject
                {
                    ["tools"] = new JArray(new JObject { ["name"] = "echo", ["description"] = "Echo", ["inputSchema"] = new JObject { ["type"] = "object" } }),
                    ["nextCursor"] = "page2",
                },
                "tools/list" => new JObject { ["tools"] = new JArray(new[] { new JObject { ["name"] = "shout", ["inputSchema"] = new JObject() } }.Concat(Extra.Cast<JObject>())) },
                "prompts/list" => new JObject { ["prompts"] = new JArray(new JObject { ["name"] = "summarize" }) },
                "tools/call" when m["params"]!["arguments"]!["text"] == null => new JObject
                {
                    ["content"] = new JArray(new JObject { ["type"] = "text", ["text"] = "args " + m["params"]!["arguments"]!.ToString(Newtonsoft.Json.Formatting.None) }),
                },
                "tools/call" => new JObject
                {
                    ["content"] = new JArray(
                        new JObject { ["type"] = "text", ["text"] = "you said " + m["params"]!["arguments"]!["text"] },
                        new JObject { ["type"] = "image", ["mimeType"] = "image/png", ["data"] = "AAA" }),
                },
                _ => new JObject(),
            };
            if (method == "boom")
            {
                Respond(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["error"] = new JObject { ["code"] = -32000, ["message"] = "kaboom" } });
                return Task.CompletedTask;
            }
            Respond(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["result"] = result });
            return Task.CompletedTask;
        }

        public void Respond(JObject m) => Task.Run(() => MessageReceived?.Invoke(this, m));
        public void Close() => Closed?.Invoke(this, "bye");
        public void Dispose() { }
    }

    [Fact]
    public async Task Handshake_Paging_Call_Ping_And_Errors()
    {
        var t = new FakeTransport();
        using var c = new McpClient("fake", t);
        await c.ConnectAsync(CancellationToken.None);

        Assert.True(c.IsConnected);
        Assert.Equal("fake", c.ServerName);
        Assert.Equal(new[] { "echo", "shout" }, c.Tools.Select(x => x.Name));
        Assert.Single(c.Prompts);
        Assert.Contains(t.Sent, m => (string?)m["method"] == "notifications/initialized" && m["id"] == null);

        var r = await c.CallToolAsync("echo", new JObject { ["text"] = "hi" }, CancellationToken.None);
        Assert.Equal("you said hi\n[image]", r.Text.Replace("\r", ""));
        Assert.Single(r.Images);

        // Сервер прислал ping — клиент должен ответить
        t.Respond(new JObject { ["jsonrpc"] = "2.0", ["id"] = "srv-1", ["method"] = "ping" });
        await Task.Delay(200);
        Assert.Contains(t.Sent, m => (string?)m["id"] == "srv-1" && m["result"] != null);

        await Assert.ThrowsAsync<McpException>(() => c.RequestAsync("boom", new JObject(), CancellationToken.None));
    }

    [Fact]
    public void Parses_Claude_Desktop_Config()
    {
        var json = "{\"mcpServers\":{\"fs\":{\"command\":\"npx\",\"args\":[\"-y\",\"@modelcontextprotocol/server-filesystem\",\"C:\\\\data\"]},\"remote\":{\"url\":\"https://example.com/mcp\"},\"old\":{\"url\":\"https://x/sse\",\"type\":\"sse\",\"disabled\":true}}}";
        var list = McpConfigStore.Parse(json);
        Assert.Equal(3, list.Count);
        Assert.Equal("stdio", list[0].Transport);
        Assert.Equal("C:\\data", list[0].Args![2]);
        Assert.Equal("http", list[1].Transport);
        Assert.Equal("sse", list[2].Transport);
        Assert.True(list[2].Disabled);

        using var tmp = new TempDir();
        var store = new McpConfigStore(tmp.Combine("mcp.json"));
        File.WriteAllText(tmp.Combine("claude.json"), json);
        Assert.Equal(3, store.ImportFrom(tmp.Combine("claude.json")));
        Assert.Equal(0, store.ImportFrom(tmp.Combine("claude.json"))); // без дублей
        Assert.Equal(3, new McpConfigStore(tmp.Combine("mcp.json")).Servers.Count);
    }

    [Fact]
    public async Task Registry_Builtins_And_Sandbox()
    {
        using var tmp = new TempDir();
        Directory.CreateDirectory(tmp.Combine("box"));
        File.WriteAllText(tmp.Combine("box", "note.txt"), "secret plan");
        File.WriteAllText(tmp.Combine("outside.txt"), "nope");
        var settings = new ToolsSettings { FileSandbox = tmp.Combine("box") };
        using var http = new HttpService(new NetworkSettings { ProxyMode = ProxyMode.None }, new TrustedRoots(), "t");
        using var reg = new ToolRegistry(settings, http);

        var tools = reg.ToOpenAiTools();
        Assert.Contains(tools, t => (string?)t["function"]!["name"] == "calculator");
        Assert.Contains(tools, t => (string?)t["function"]!["name"] == "read_file");
        // Веб-инструменты есть всегда: поиск больше не требует своего SearXNG; fetch_url переименован
        Assert.Contains(tools, t => (string?)t["function"]!["name"] == "web_search");
        Assert.Contains(tools, t => (string?)t["function"]!["name"] == "web_fetch");
        Assert.DoesNotContain(tools, t => (string?)t["function"]!["name"] == "fetch_url");
        Assert.Equal(ConsentMode.Ask, reg.ConsentFor(reg.Find("web_fetch")!));
        Assert.Equal(ConsentMode.Ask, reg.ConsentFor(reg.Find("web_search")!));

        var calc = reg.Find("calculator")!;
        Assert.Equal(ConsentMode.Always, reg.ConsentFor(calc));
        Assert.Equal(("1/2 ≈ 0.5", false), await reg.ExecuteAsync(calc, "{\"expression\":\"1/4+1/4\"}", CancellationToken.None));

        var read = reg.Find("read_file")!;
        Assert.Equal(ConsentMode.Ask, reg.ConsentFor(read));
        Assert.Equal(("secret plan", false), await reg.ExecuteAsync(read, "{\"path\":\"note.txt\"}", CancellationToken.None));
        var escape = await reg.ExecuteAsync(read, "{\"path\":\"..\\\\outside.txt\"}", CancellationToken.None);
        Assert.True(escape.isError);
        Assert.DoesNotContain("nope", escape.text);

        settings.Consent["builtin:calculator"] = ConsentMode.Disabled;
        Assert.DoesNotContain(reg.ToOpenAiTools(), t => (string?)t["function"]!["name"] == "calculator");

        var bad = await reg.ExecuteAsync(reg.Find("convert_units")!, "{not json", CancellationToken.None);
        Assert.True(bad.isError);
    }

    [Fact]
    public void Html_To_Text() =>
        Assert.Equal("Title\nHello & bye", PageExtractor.Extract("<html><head><title>x</title><style>p{}</style></head><body><h1>Title</h1><p>Hello &amp; <b>bye</b></p><script>alert(1)</script></body></html>", null, PageCleanOptions.Raw).Text);

    [Fact]
    public async Task Default_City_Reaches_Weather_Tools_Only_When_Set()
    {
        var transport = new FakeTransport
        {
            Extra = new JArray(new JObject
            {
                ["name"] = "get_weather",
                ["description"] = "Current weather for a place",
                ["inputSchema"] = new JObject
                {
                    ["type"] = "object",
                    ["properties"] = new JObject { ["city"] = new JObject { ["type"] = "string" }, ["units"] = new JObject { ["type"] = "string" } },
                },
            }),
        };
        var settings = new ToolsSettings();
        using var http = new HttpService(new NetworkSettings { ProxyMode = ProxyMode.None }, new TrustedRoots(), "t");
        using var reg = new ToolRegistry(settings, http);
        await reg.ConnectAsync(new McpServerConfig { Name = "srv" }, transport, CancellationToken.None);

        // Город не задан: ни в описании, ни в аргументах его нет
        var weather = reg.Find("srv__get_weather")!;
        Assert.Equal("Current weather for a place (MCP: srv)", weather.Description);
        Assert.Equal("{}", reg.PrepareArguments(weather, "{}"));
        Assert.Equal(("args {}", false), await reg.ExecuteAsync(weather, "{}", CancellationToken.None));
        Assert.DoesNotContain("Default location", reg.ToOpenAiTools().ToString());

        settings.DefaultCity = " Казань ";
        weather = reg.Find("srv__get_weather")!;
        Assert.Equal("Current weather for a place (MCP: srv) Default location: Казань - use it when the user does not name another place.", weather.Description);
        Assert.Equal("{\"units\":\"metric\",\"city\":\"Казань\"}", reg.PrepareArguments(weather, "{\"units\":\"metric\"}"));
        Assert.Equal(("args {\"city\":\"Казань\"}", false), await reg.ExecuteAsync(weather, "", CancellationToken.None));
        // Пользователь спросил про другой город — модель его передала, настройка не мешает
        Assert.Equal(("args {\"city\":\"Omsk\"}", false), await reg.ExecuteAsync(weather, "{\"city\":\"Omsk\"}", CancellationToken.None));

        // Остальные инструменты о городе не знают
        var tools = reg.ToOpenAiTools();
        Assert.Single(tools, t => t["function"]!["description"]!.ToString().Contains("Казань"));
        Assert.Equal("{\"text\":\"hi\"}", reg.PrepareArguments(reg.Find("srv__echo")!, "{\"text\":\"hi\"}"));
    }
}
