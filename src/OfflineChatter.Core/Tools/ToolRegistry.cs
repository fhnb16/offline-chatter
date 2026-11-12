using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Mcp;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Tools;

public enum ConsentMode
{
    /// <summary>Спрашивать перед каждым вызовом.</summary>
    Ask,
    /// <summary>Разрешено без вопросов.</summary>
    Always,
    /// <summary>Инструмент выключен (не передаётся модели).</summary>
    Disabled,
}

public sealed class ToolsSettings
{
    /// <summary>Режим для каждого инструмента: "builtin:calculator", "mcp:server/tool".</summary>
    public Dictionary<string, ConsentMode> Consent { get; set; } = new();

    /// <summary>Папка, из которой инструмент read_file может читать файлы (пусто — инструмент выключен).</summary>
    public string? FileSandbox { get; set; }

    /// <summary>Адрес своего SearXNG (JSON API), например http://127.0.0.1:8888 — для поисковой системы «SearXNG».</summary>
    public string? SearxngUrl { get; set; }

    /// <summary>Запросы, очистка страниц и поиск для web_fetch и web_search.</summary>
    public WebToolsSettings Web { get; set; } = new();

    /// <summary>
    /// Город по умолчанию для инструментов погоды (встроенных и MCP). Пусто — ничего не передаётся:
    /// ни в описание инструмента, ни в аргументы вызова.
    /// </summary>
    public string? DefaultCity { get; set; }

    /// <summary>Максимум шагов «модель → инструменты → модель» на один ответ.</summary>
    public int MaxSteps { get; set; } = 6;

    /// <summary>Включены ли инструменты в чатах по умолчанию.</summary>
    public bool EnabledByDefault { get; set; } = true;

    /// <summary>Встроенный коннектор MCP уже добавлялся в mcp.json (если пользователь его удалил — не возвращаем).</summary>
    public bool BuiltinMcpSeeded { get; set; }
}

public sealed class ToolDefinition
{
    public string Id { get; set; } = string.Empty;          // builtin:calculator / mcp:server/tool
    public string Name { get; set; } = string.Empty;        // имя для модели (уникальное)
    public string Description { get; set; } = string.Empty;
    public JObject Schema { get; set; } = new();
    public string Source { get; set; } = "builtin";          // builtin или имя MCP-сервера
    public ConsentMode DefaultConsent { get; set; } = ConsentMode.Ask;

    /// <summary>Инструменту нужен интернет: в офлайн-режиме он модели не предлагается.</summary>
    public bool NeedsInternet { get; set; }

    /// <summary>Инструмент сам ограничивает длину ответа и умеет продолжать с места обрыва.</summary>
    internal bool OwnLimit { get; set; }

    internal Func<JObject, CancellationToken, Task<string>>? Execute { get; set; }
    internal McpClient? Client { get; set; }
    internal string? McpToolName { get; set; }
}

/// <summary>
/// Все инструменты для модели: встроенные (в процессе) + инструменты подключённых MCP-серверов.
/// Отдаёт описания в формате OpenAI tools и выполняет вызовы.
/// </summary>
public sealed class ToolRegistry : IDisposable
{
    private readonly ToolsSettings _settings;
    private readonly HttpService _http;
    private readonly ConcurrentDictionary<string, McpClient> _clients = new(StringComparer.OrdinalIgnoreCase);

    private readonly WebTools _web;

    public ToolRegistry(ToolsSettings settings, HttpService http)
    {
        _settings = settings;
        _http = http;
        _web = new WebTools(settings, http);
    }

    public ToolsSettings Settings => _settings;

    public event EventHandler? Changed;

    public IReadOnlyDictionary<string, McpClient> Clients => _clients;

    // ---------------- MCP ----------------

    public async Task<McpClient> ConnectAsync(McpServerConfig cfg, CancellationToken ct)
    {
        Disconnect(cfg.Name);
        IMcpTransport transport = cfg.Transport switch
        {
            "http" => new StreamableHttpTransport(cfg, _http),
            "sse" => new LegacySseTransport(cfg, _http),
            _ => new StdioTransport(cfg),
        };
        return await ConnectAsync(cfg, transport, ct).ConfigureAwait(false);
    }

    internal async Task<McpClient> ConnectAsync(McpServerConfig cfg, IMcpTransport transport, CancellationToken ct)
    {
        var client = new McpClient(cfg.Name, transport);
        client.StateChanged += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            await client.ConnectAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            client.Dispose();
            throw;
        }
        _clients[cfg.Name] = client;
        foreach (var t in cfg.AlwaysAllow ?? new List<string>())
            if (!_settings.Consent.ContainsKey($"mcp:{cfg.Name}/{t}")) _settings.Consent[$"mcp:{cfg.Name}/{t}"] = ConsentMode.Always;
        Changed?.Invoke(this, EventArgs.Empty);
        return client;
    }

    public void Disconnect(string name)
    {
        if (_clients.TryRemove(name, out var c)) c.Dispose();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- Список инструментов ----------------

    public IReadOnlyList<ToolDefinition> All()
    {
        var list = Builtins().ToList();
        foreach (var c in _clients.Values.Where(c => c.IsConnected))
        {
            foreach (var t in c.Tools)
            {
                list.Add(new ToolDefinition
                {
                    Id = $"mcp:{c.Name}/{t.Name}",
                    Name = SafeName(c.Name + "__" + t.Name),
                    Description = (t.Description ?? t.Name) + $" (MCP: {c.Name})",
                    Schema = t.InputSchema,
                    Source = c.Name,
                    DefaultConsent = ConsentMode.Ask,
                    Client = c,
                    McpToolName = t.Name,
                });
            }
        }
        // Город по умолчанию: инструмент погоды узнаёт о нём из своего описания. Пустая настройка описаний не меняет
        if (DefaultLocation.Normalize(_settings.DefaultCity) is { } city)
            foreach (var t in list.Where(t => DefaultLocation.IsWeatherTool(t.Name, t.Description)))
                t.Description = DefaultLocation.Describe(t.Description, city);
        return list;
    }

    public ConsentMode ConsentFor(ToolDefinition t) => _settings.Consent.TryGetValue(t.Id, out var m) ? m : t.DefaultConsent;

    /// <summary>
    /// Описания для запроса к модели (OpenAI tools). Выключенные инструменты не передаются;
    /// в офлайн-режиме не передаются и те, которым нужен интернет, — вызов всё равно закончился бы ошибкой.
    /// </summary>
    public JArray ToOpenAiTools() => new(All()
        .Where(t => ConsentFor(t) != ConsentMode.Disabled && !(t.NeedsInternet && _http.Settings.OfflineMode))
        .Select(t => new JObject
        {
            ["type"] = "function",
            ["function"] = new JObject { ["name"] = t.Name, ["description"] = t.Description, ["parameters"] = t.Schema },
        }));

    public ToolDefinition? Find(string name) => All().FirstOrDefault(t => t.Name == name);

    /// <summary>
    /// Аргументы вызова с учётом настроек: инструменту погоды, которому модель не назвала место, подставляется
    /// город по умолчанию. Вызывается до запроса согласия, чтобы пользователь видел то, что действительно уйдёт.
    /// </summary>
    public string PrepareArguments(ToolDefinition tool, string argumentsJson)
    {
        if (DefaultLocation.Normalize(_settings.DefaultCity) is not { } city || !DefaultLocation.IsWeatherTool(tool.Name, tool.Description))
            return argumentsJson;
        try
        {
            var args = string.IsNullOrWhiteSpace(argumentsJson) ? new JObject() : JObject.Parse(argumentsJson);
            return DefaultLocation.Fill(tool.Schema, args, city) ? args.ToString(Newtonsoft.Json.Formatting.None) : argumentsJson;
        }
        catch (Newtonsoft.Json.JsonException)
        {
            return argumentsJson;
        }
    }

    public async Task<(string text, bool isError)> ExecuteAsync(ToolDefinition tool, string argumentsJson, CancellationToken ct)
    {
        JObject args;
        try
        {
            argumentsJson = PrepareArguments(tool, argumentsJson);
            args = string.IsNullOrWhiteSpace(argumentsJson) ? new JObject() : JObject.Parse(argumentsJson);
        }
        catch (Newtonsoft.Json.JsonException ex) { return ("Invalid JSON arguments: " + ex.Message, true); }

        try
        {
            if (tool.Client != null)
            {
                var r = await tool.Client.CallToolAsync(tool.McpToolName!, args, ct).ConfigureAwait(false);
                return (Truncate(r.Text), r.IsError);
            }
            var text = await tool.Execute!(args, ct).ConfigureAwait(false);
            return (tool.OwnLimit ? text : Truncate(text), false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Warn("Tools", "Tool failed: " + tool.Id, ex);
            return ("Error: " + ex.Message, true);
        }
    }

    private static string Truncate(string s) => s.Length > 20000 ? s.Substring(0, 20000) + "\n…[truncated]" : s;

    private static string SafeName(string s)
    {
        var n = Regex.Replace(s, "[^A-Za-z0-9_-]", "_");
        return n.Length > 64 ? n.Substring(0, 64) : n;
    }

    // ---------------- Встроенные ----------------

    private static JObject Schema(params (string name, string type, string desc, bool required)[] props)
    {
        var p = new JObject();
        foreach (var x in props) p[x.name] = new JObject { ["type"] = x.type, ["description"] = x.desc };
        return new JObject
        {
            ["type"] = "object",
            ["properties"] = p,
            ["required"] = new JArray(props.Where(x => x.required).Select(x => x.name)),
        };
    }

    private IEnumerable<ToolDefinition> Builtins()
    {
        yield return new ToolDefinition
        {
            Id = "builtin:calculator", Name = "calculator", DefaultConsent = ConsentMode.Always,
            Description = "Exact calculator. Supports + - * / ^ % ! parentheses, fractions (exact rational results), sqrt, sin, cos, tan, ln, log, exp, abs, floor, ceil, round, pi, e. Use it for ANY arithmetic instead of computing in your head.",
            Schema = Schema(("expression", "string", "Expression, e.g. (2/3)^10 * 1.5 or sqrt(2)*pi", true)),
            Execute = (a, _) => Task.FromResult(Calculator.Evaluate((string?)a["expression"] ?? throw new ArgumentException("expression is required")).ToString()),
        };
        yield return new ToolDefinition
        {
            Id = "builtin:convert_units", Name = "convert_units", DefaultConsent = ConsentMode.Always,
            Description = "Convert a value between units of length, mass, volume, area, speed, time, data size, pressure, energy and temperature (C/F/K). Units: " + UnitConverter.SupportedUnits,
            Schema = Schema(("value", "number", "Value to convert", true), ("from", "string", "Source unit, e.g. km", true), ("to", "string", "Target unit, e.g. mi", true)),
            Execute = (a, _) => Task.FromResult(UnitConverter.Convert((double?)a["value"] ?? 0, (string?)a["from"] ?? "", (string?)a["to"] ?? "")),
        };
        yield return new ToolDefinition
        {
            Id = "builtin:datetime", Name = "current_datetime", DefaultConsent = ConsentMode.Always,
            Description = "Current local date and time, weekday, time zone and UTC time. Optionally: days between today and a date (yyyy-mm-dd).",
            Schema = Schema(("date", "string", "Optional date yyyy-mm-dd to compute the difference in days", false)),
            Execute = (a, _) =>
            {
                var now = DateTime.Now;
                var sb = new StringBuilder();
                sb.AppendLine("Local: " + now.ToString("yyyy-MM-dd HH:mm:ss dddd", CultureInfo.InvariantCulture));
                sb.AppendLine("Time zone: " + TimeZoneInfo.Local.DisplayName);
                sb.AppendLine("UTC: " + DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture));
                if ((string?)a["date"] is { Length: > 0 } d && DateTime.TryParse(d, CultureInfo.InvariantCulture, DateTimeStyles.None, out var target))
                    sb.AppendLine($"Days from today to {target:yyyy-MM-dd}: {(target.Date - now.Date).TotalDays:0}");
                return Task.FromResult(sb.ToString().TrimEnd());
            },
        };
        if (!string.IsNullOrWhiteSpace(_settings.FileSandbox))
        {
            yield return new ToolDefinition
            {
                Id = "builtin:read_file", Name = "read_file", DefaultConsent = ConsentMode.Ask,
                Description = "Read a text file or list a folder inside the user's allowed folder: " + _settings.FileSandbox,
                Schema = Schema(("path", "string", "Relative path inside the allowed folder ('.' to list the root)", true)),
                Execute = (a, _) => Task.FromResult(ReadSandboxed((string?)a["path"] ?? ".")),
            };
        }
        yield return new ToolDefinition
        {
            Id = WebFetchId, Name = "web_fetch", DefaultConsent = ConsentMode.Ask, NeedsInternet = true, OwnLimit = true,
            Description = "Download a web page (http/https) and return its main text without menus, ads and footers. "
                          + "A long page is returned in parts: call again with start to read on.",
            Schema = Schema(
                ("url", "string", "Absolute URL of the page", true),
                ("start", "integer", "Character offset to continue a truncated page (default 0)", false)),
            Execute = _web.FetchAsync,
        };
        yield return new ToolDefinition
        {
            Id = WebSearchId, Name = "web_search", DefaultConsent = ConsentMode.Ask, NeedsInternet = true, OwnLimit = true,
            Description = "Search the web. Returns titles, URLs and short snippets of the results; use web_fetch to read a page from them.",
            Schema = Schema(
                ("query", "string", "Search query", true),
                ("period", "string", "Optional: only recent results - day, week, month or year", false)),
            Execute = _web.SearchAsync,
        };
    }

    public const string WebFetchId = "builtin:web_fetch";
    public const string WebSearchId = "builtin:web_search";

    /// <summary>Прежнее имя web_fetch: разрешение пользователя переносится на новое.</summary>
    public const string LegacyFetchId = "builtin:fetch_url";

    private string ReadSandboxed(string relative)
    {
        var root = Path.GetFullPath(_settings.FileSandbox!).TrimEnd('\\') + "\\";
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase) && !string.Equals(full + "\\", root, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException("Path is outside the allowed folder");
        if (Directory.Exists(full))
            return string.Join("\n", Directory.EnumerateFileSystemEntries(full).Take(500).Select(p => (Directory.Exists(p) ? "[dir] " : string.Empty) + p.Substring(root.Length)));
        var info = new FileInfo(full);
        if (!info.Exists) throw new FileNotFoundException("File not found: " + relative);
        if (info.Length > 1_000_000) throw new IOException("File is larger than 1 MB");
        if (Chat.AttachmentReader.IsDocx(full)) return Chat.AttachmentReader.ReadDocx(full);
        return File.ReadAllText(full, Encoding.UTF8);
    }

    public void Dispose()
    {
        foreach (var c in _clients.Values) c.Dispose();
        _clients.Clear();
    }
}
