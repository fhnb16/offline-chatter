using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Core.Mcp;

public sealed record McpTool(string Name, string? Description, JObject InputSchema);

public sealed record McpNamedItem(string Name, string? Description);

public sealed class McpToolResult
{
    public string Text { get; set; } = string.Empty;
    public bool IsError { get; set; }
    public List<ChatAttachment> Images { get; } = new();
}

/// <summary>
/// Минимальный клиент MCP (JSON-RPC 2.0): initialize, tools/list (с пагинацией), tools/call,
/// resources/list, prompts/list, уведомления list_changed. Без зависимостей от официального SDK (вес).
/// </summary>
public sealed class McpClient : IDisposable
{
    public const string ProtocolVersion = "2025-06-18";

    private readonly IMcpTransport _transport;
    private readonly object _state = new();
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JObject>> _pending = new();
    private long _nextId;
    private bool _closed;
    private bool _disposed;

    public McpClient(string name, IMcpTransport transport)
    {
        Name = name;
        _transport = transport;
        _transport.MessageReceived += OnMessage;
        _transport.Closed += (_, reason) => Close(reason);
    }

    public string Name { get; }
    public bool IsConnected { get; private set; }
    public string? LastError { get; private set; }
    public string? ServerName { get; private set; }
    public string? ServerVersion { get; private set; }
    public string? Instructions { get; private set; }
    public IReadOnlyList<McpTool> Tools { get; private set; } = Array.Empty<McpTool>();
    public IReadOnlyList<McpNamedItem> Resources { get; private set; } = Array.Empty<McpNamedItem>();
    public IReadOnlyList<McpNamedItem> Prompts { get; private set; } = Array.Empty<McpNamedItem>();

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(60);

    public event EventHandler? StateChanged;

    public async Task ConnectAsync(CancellationToken ct)
    {
        await _transport.StartAsync(ct).ConfigureAwait(false);
        var init = await RequestAsync("initialize", new JObject
        {
            ["protocolVersion"] = ProtocolVersion,
            ["capabilities"] = new JObject { ["roots"] = new JObject { ["listChanged"] = false } },
            ["clientInfo"] = new JObject { ["name"] = "OfflineChatter", ["version"] = "0.1" },
        }, ct).ConfigureAwait(false);
        ServerName = (string?)init["serverInfo"]?["name"];
        ServerVersion = (string?)init["serverInfo"]?["version"];
        Instructions = (string?)init["instructions"];
        await NotifyAsync("notifications/initialized", ct).ConfigureAwait(false);
        var caps = init["capabilities"] as JObject;
        if (caps?["tools"] != null) await RefreshToolsAsync(ct).ConfigureAwait(false);
        if (caps?["resources"] != null) Resources = await ListNamedAsync("resources/list", "resources", ct).ConfigureAwait(false);
        if (caps?["prompts"] != null) Prompts = await ListNamedAsync("prompts/list", "prompts", ct).ConfigureAwait(false);
        lock (_state)
        {
            if (_closed) throw new IOException("MCP server closed: " + LastError);
            ct.ThrowIfCancellationRequested();
            IsConnected = true;
        }
        Log.Info("MCP", $"Connected {Name}", new { ServerName, ServerVersion, tools = Tools.Count, resources = Resources.Count, prompts = Prompts.Count });
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public async Task RefreshToolsAsync(CancellationToken ct)
    {
        var tools = new List<McpTool>();
        string? cursor = null;
        do
        {
            var p = new JObject();
            if (cursor != null) p["cursor"] = cursor;
            var r = await RequestAsync("tools/list", p, ct).ConfigureAwait(false);
            foreach (var t in r["tools"] as JArray ?? new JArray())
                tools.Add(new McpTool((string)t["name"]!, (string?)t["description"], t["inputSchema"] as JObject ?? new JObject { ["type"] = "object" }));
            cursor = (string?)r["nextCursor"];
        } while (!string.IsNullOrEmpty(cursor) && tools.Count < 1000);
        Tools = tools;
    }

    private async Task<IReadOnlyList<McpNamedItem>> ListNamedAsync(string method, string field, CancellationToken ct)
    {
        try
        {
            var r = await RequestAsync(method, new JObject(), ct).ConfigureAwait(false);
            return (r[field] as JArray ?? new JArray()).Select(x => new McpNamedItem((string?)x["name"] ?? (string?)x["uri"] ?? "?", (string?)x["description"])).ToList();
        }
        catch (McpException)
        {
            return Array.Empty<McpNamedItem>();
        }
    }

    public async Task<McpToolResult> CallToolAsync(string name, JObject arguments, CancellationToken ct)
    {
        var response = await RequestAsync("tools/call", new JObject { ["name"] = name, ["arguments"] = arguments }, ct).ConfigureAwait(false);
        var local = _transport as StdioTransport;
        return await McpResultReader.ReadAsync(response, local != null, local?.WorkingDirectory, ct).ConfigureAwait(false);
    }

    public async Task<JObject> RequestAsync(string method, JObject @params, CancellationToken ct)
    {
        var id = Interlocked.Increment(ref _nextId);
        var tcs = new TaskCompletionSource<JObject>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_state)
        {
            if (_closed) throw new IOException("MCP server closed: " + LastError);
            _pending[id] = tcs;
        }
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(RequestTimeout);
            using (timeout.Token.Register(() => tcs.TrySetCanceled()))
            {
                await _transport.SendAsync(new JObject { ["jsonrpc"] = "2.0", ["id"] = id, ["method"] = method, ["params"] = @params }, timeout.Token).ConfigureAwait(false);
                var msg = await tcs.Task.ConfigureAwait(false);
                if (msg["error"] is JObject err)
                    throw new McpException((int?)err["code"] ?? 0, (string?)err["message"] ?? "MCP error");
                return msg["result"] as JObject ?? new JObject();
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    private Task NotifyAsync(string method, CancellationToken ct) =>
        _transport.SendAsync(new JObject { ["jsonrpc"] = "2.0", ["method"] = method }, ct);

    private void OnMessage(object? sender, JObject msg)
    {
        // Ответ на наш запрос
        if (msg["id"] is JValue idv && (msg["result"] != null || msg["error"] != null))
        {
            if (long.TryParse(idv.ToString(), out var id) && _pending.TryGetValue(id, out var tcs)) tcs.TrySetResult(msg);
            return;
        }

        var method = (string?)msg["method"];
        if (method == null) return;

        // Запросы от сервера: ping обязателен, остальное (sampling, roots) не поддерживаем
        if (msg["id"] != null)
        {
            var reply = method == "ping"
                ? new JObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"], ["result"] = new JObject() }
                : new JObject { ["jsonrpc"] = "2.0", ["id"] = msg["id"], ["error"] = new JObject { ["code"] = -32601, ["message"] = "Method not supported: " + method } };
            _ = SendReplyAsync(reply);
            return;
        }

        if (method == "notifications/tools/list_changed")
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await RefreshToolsAsync(CancellationToken.None).ConfigureAwait(false);
                    StateChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (Exception ex) when (ex is McpException or System.IO.IOException or OperationCanceledException)
                {
                    Log.Warn("MCP", "Tool list refresh failed", ex);
                }
            });
        }
        else if (method == "notifications/message")
        {
            Log.Debug("MCP", $"[{Name}] {msg["params"]?["data"]}");
        }
    }

    private async Task SendReplyAsync(JObject reply)
    {
        try
        {
            using var timeout = new CancellationTokenSource(RequestTimeout);
            await _transport.SendAsync(reply, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.Net.Http.HttpRequestException or OperationCanceledException)
        {
            Log.Debug("MCP", $"[{Name}] Could not reply to server");
        }
    }

    private void Close(string reason)
    {
        lock (_state)
        {
            if (_closed) return;
            _closed = true;
            LastError = reason;
            IsConnected = false;
            foreach (var pending in _pending.Values) pending.TrySetException(new IOException("MCP server closed: " + reason));
        }
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Dispose()
    {
        lock (_state)
        {
            if (_disposed) return;
            _disposed = true;
        }
        Close("disconnected");
        _transport.Dispose();
    }
}

public sealed class McpException : Exception
{
    public McpException(int code, string message) : base(message) => Code = code;
    public int Code { get; }
}
