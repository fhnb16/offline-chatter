using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Mcp;

/// <summary>Сервер MCP в формате Claude Desktop (mcpServers) + наши поля disabled/alwaysAllow.</summary>
public sealed class McpServerConfig
{
    [JsonIgnore] public string Name { get; set; } = string.Empty;

    [JsonProperty("command", NullValueHandling = NullValueHandling.Ignore)] public string? Command { get; set; }
    [JsonProperty("args", NullValueHandling = NullValueHandling.Ignore)] public List<string>? Args { get; set; }
    [JsonProperty("env", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string>? Env { get; set; }
    [JsonProperty("cwd", NullValueHandling = NullValueHandling.Ignore)] public string? Cwd { get; set; }

    /// <summary>Для удалённых: адрес Streamable HTTP или SSE.</summary>
    [JsonProperty("url", NullValueHandling = NullValueHandling.Ignore)] public string? Url { get; set; }

    /// <summary>"stdio", "http" (Streamable HTTP) или "sse" (старый протокол). Пусто — по наличию command/url.</summary>
    [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)] public string? Type { get; set; }

    [JsonProperty("headers", NullValueHandling = NullValueHandling.Ignore)] public Dictionary<string, string>? Headers { get; set; }

    [JsonProperty("disabled", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Disabled { get; set; }

    /// <summary>Инструменты, которые можно вызывать без подтверждения.</summary>
    [JsonProperty("alwaysAllow", NullValueHandling = NullValueHandling.Ignore)] public List<string>? AlwaysAllow { get; set; }

    [JsonIgnore]
    public string Transport => !string.IsNullOrEmpty(Type) ? Type!.ToLowerInvariant() switch
    {
        "streamable-http" or "streamablehttp" or "http" => "http",
        "sse" => "sse",
        _ => "stdio",
    } : !string.IsNullOrEmpty(Url) ? "http" : "stdio";
}

/// <summary>config/mcp.json. Совместим с claude_desktop_config.json — можно просто скопировать оттуда секцию mcpServers.</summary>
public sealed class McpConfigStore
{
    private readonly string _file;

    public McpConfigStore(string file)
    {
        _file = file;
        Load();
    }

    public List<McpServerConfig> Servers { get; private set; } = new();

    public string FilePath => _file;

    public void Load()
    {
        Servers = new List<McpServerConfig>();
        if (!File.Exists(_file)) return;
        try
        {
            Servers = Parse(File.ReadAllText(_file));
        }
        catch (Exception ex) when (ex is JsonException or IOException)
        {
            Log.Error("MCP", "mcp.json is invalid", ex);
        }
    }

    public static List<McpServerConfig> Parse(string json)
    {
        var root = JObject.Parse(json);
        var section = root["mcpServers"] as JObject ?? root["servers"] as JObject ?? new JObject();
        var list = new List<McpServerConfig>();
        foreach (var p in section.Properties())
        {
            var cfg = p.Value.ToObject<McpServerConfig>() ?? new McpServerConfig();
            cfg.Name = p.Name;
            list.Add(cfg);
        }
        return list;
    }

    public void Save()
    {
        var servers = new JObject();
        foreach (var s in Servers) servers[s.Name] = JObject.FromObject(s);
        JsonFile.WriteAllTextAtomic(_file, new JObject { ["mcpServers"] = servers }.ToString(Formatting.Indented));
    }

    /// <summary>Импорт из Claude Desktop (%APPDATA%\Claude\claude_desktop_config.json). Возвращает число добавленных.</summary>
    public int ImportFrom(string path)
    {
        var added = 0;
        foreach (var s in Parse(File.ReadAllText(path)))
        {
            if (Servers.Any(x => x.Name.Equals(s.Name, StringComparison.OrdinalIgnoreCase))) continue;
            Servers.Add(s);
            added++;
        }
        if (added > 0) Save();
        return added;
    }

    public static string ClaudeDesktopConfigPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json");
}
