using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using OfflineChatter.Core.Tools;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class ToolBudgetTests
{
    [Fact]
    public void Schema_Loses_Only_Service_Fields()
    {
        var schema = JObject.Parse(@"{
          ""$schema"": ""http://json-schema.org/draft-07/schema#"",
          ""title"": ""GetWeatherArgs"",
          ""type"": ""object"",
          ""additionalProperties"": false,
          ""properties"": {
            ""title"": { ""type"": ""string"", ""title"": ""Title"", ""description"": ""Page title"" },
            ""city"": { ""type"": ""string"", ""description"": ""City name. Long explanation stays."", ""examples"": [""Kazan""], ""default"": ""Moscow"" },
            ""filter"": { ""type"": ""object"", ""additionalProperties"": { ""type"": ""string"" }, ""properties"": { ""x"": { ""type"": ""integer"", ""title"": ""X"" } } },
            ""tags"": { ""type"": ""array"", ""items"": { ""type"": ""string"", ""title"": ""Tag"" } }
          },
          ""required"": [""city""]
        }");
        var original = schema.ToString();

        var c = ToolSchemaCompactor.CompactSchema(schema);

        Assert.Equal(original, schema.ToString()); // исходная схема (у MCP — из tools/list) не меняется
        Assert.Null(c["$schema"]);
        Assert.Null(c["title"]);
        Assert.Null(c["additionalProperties"]);
        // Параметр с именем «title» остаётся, теряет только своё служебное поле
        Assert.Equal(new JObject { ["type"] = "string", ["description"] = "Page title" }, c["properties"]!["title"]);
        // Описания, примеры и значения по умолчанию не трогаются: по ним маленькие модели заполняют аргументы
        Assert.Equal("City name. Long explanation stays.", (string?)c["properties"]!["city"]!["description"]);
        Assert.Equal("Kazan", (string?)c["properties"]!["city"]!["examples"]![0]);
        Assert.Equal("Moscow", (string?)c["properties"]!["city"]!["default"]);
        Assert.Null(c["properties"]!["filter"]!["additionalProperties"]);
        Assert.Null(c["properties"]!["filter"]!["properties"]!["x"]!["title"]);
        Assert.Null(c["properties"]!["tags"]!["items"]!["title"]);
        Assert.Equal(new[] { "city" }, c["required"]!.Select(t => (string?)t));
    }

    [Fact]
    public void Registry_Compacts_And_Estimates()
    {
        var settings = new ToolsSettings();
        using var http = new HttpService(new NetworkSettings { ProxyMode = ProxyMode.None }, new TrustedRoots(), "t");
        using var reg = new ToolRegistry(settings, http);
        var calc = reg.Find("calculator")!;
        calc.Schema["$schema"] = "x";

        Assert.Null(reg.ToOpenAi(calc)["function"]!["parameters"]!["$schema"]);
        var compact = reg.EstimateTokens(calc);
        settings.CompactSchemas = false;
        Assert.Equal("x", (string?)reg.ToOpenAi(calc)["function"]!["parameters"]!["$schema"]);
        Assert.True(reg.EstimateTokens(calc) > compact);
        Assert.InRange(compact, 50, 400);

        // Фильтр чата: в запрос уходят только включённые
        var names = reg.ToOpenAiTools(t => t.Name is "calculator" or "web_search").Select(t => (string?)t["function"]!["name"]).ToList();
        Assert.Equal(new[] { "calculator", "web_search" }, names);
    }

    [Fact]
    public void Presets()
    {
        var settings = new ToolsSettings { FileSandbox = "C:\\Docs" };
        using var http = new HttpService(new NetworkSettings { ProxyMode = ProxyMode.None }, new TrustedRoots(), "t");
        using var reg = new ToolRegistry(settings, http);
        var all = reg.All();
        IEnumerable<string> In(ToolPreset p) => all.Where(t => ToolPresets.Contains(p, t)).Select(t => t.Name);

        Assert.Equal(new[] { "calculator", "convert_units", "current_datetime" }, In(ToolPreset.Math));
        Assert.Equal(new[] { "current_datetime", "web_fetch", "web_search" }, In(ToolPreset.Web));
        Assert.Equal(all.Select(t => t.Name), In(ToolPreset.All));
        Assert.Empty(In(ToolPreset.None));

        // Инструменты MCP попадают в наборы по имени и описанию
        var weather = new ToolDefinition { Id = "mcp:s/get_weather", Name = "s__get_weather", Description = "Current weather for a place" };
        var rates = new ToolDefinition { Id = "mcp:s/rates", Name = "s__rates", Description = "Currency exchange rates" };
        Assert.True(ToolPresets.Contains(ToolPreset.Web, weather));
        Assert.True(ToolPresets.Contains(ToolPreset.Web, rates));
        Assert.False(ToolPresets.Contains(ToolPreset.Math, weather));

        // Свой набор — по списку id
        var calc = reg.Find("calculator")!;
        Assert.True(ToolPresets.IsEnabled(calc, ToolPreset.Custom, new[] { "builtin:calculator" }));
        Assert.False(ToolPresets.IsEnabled(calc, ToolPreset.Custom, null));
    }

    [Theory]
    [InlineData(null, null, true, ToolPreset.All)]
    [InlineData(null, null, false, ToolPreset.None)]
    [InlineData(null, false, true, ToolPreset.None)]   // старый чат с выключенным флагом
    [InlineData(null, true, false, ToolPreset.All)]
    [InlineData(ToolPreset.Web, false, true, ToolPreset.Web)]
    public void Old_Chats_Keep_Their_Tool_Switch(ToolPreset? preset, bool? tools, bool enabledByDefault, ToolPreset expected) =>
        Assert.Equal(expected, new ChatSession { ToolPreset = preset, Tools = tools }.EffectiveToolPreset(enabledByDefault));

    [Fact]
    public void Old_Tool_Results_Are_Shortened_Only_Before_The_Last_Question()
    {
        var page = string.Join("\n", Enumerable.Range(1, 200).Select(i => $"Line {i} of a long fetched page"));
        var history = new List<ChatMessage>
        {
            new() { Role = ChatRole.User, Content = "Find llama facts" },
            new() { Role = ChatRole.Assistant, ToolCalls = { new ToolCall { Id = "c1", Name = "web_fetch", Arguments = "{}" } } },
            new() { Role = ChatRole.Tool, ToolCallId = "c1", Content = page },
            new() { Role = ChatRole.Tool, ToolCallId = "c2", Content = "short result" },
            new() { Role = ChatRole.Assistant, Content = "Llamas are camelids." },
            new() { Role = ChatRole.User, Content = "And alpacas?" },
            new() { Role = ChatRole.Assistant, ToolCalls = { new ToolCall { Id = "c3", Name = "web_fetch", Arguments = "{}" } } },
            new() { Role = ChatRole.Tool, ToolCallId = "c3", Content = page },
        };

        var sent = ToolHistory.CompactOldResults(history, 600);

        Assert.Equal(history.Count, sent.Count);
        var old = sent[2];
        Assert.NotSame(history[2], old);
        Assert.Equal("c1", old.ToolCallId);
        Assert.StartsWith("Line 1 of a long fetched page\n", old.Content);
        Assert.Matches(@"\n\[Earlier tool result shortened: \d+ of " + page.Length + @" characters omitted\. Call the tool again if you need the full text\.\]$", old.Content);
        Assert.True(old.Content.Length < 800);
        Assert.Equal(page, history[2].Content);   // в чате результат полный
        Assert.Same(history[3], sent[3]);         // короткий не трогаем
        Assert.Same(history[7], sent[7]);         // текущий вопрос — целиком
        // Обрезка по границе строки
        Assert.EndsWith("of a long fetched page", old.Content.Substring(0, old.Content.IndexOf("\n[Earlier", System.StringComparison.Ordinal)));
    }

    [Fact]
    public void Old_Results_Settings_Are_Clamped()
    {
        var s = new AppSettings();
        s.Tools.OldResultChars = 5;
        SettingsStore.Normalize(s);
        Assert.Equal(600, s.Tools.OldResultChars);
        Assert.False(new ToolsSettings().CompactOldResults); // по умолчанию выключено
        Assert.True(new ToolsSettings().CompactSchemas);
    }
}
