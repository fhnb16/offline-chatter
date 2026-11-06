using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Net;

namespace OfflineChatter.Core.Mcp;

/// <summary>Транспорт JSON-RPC сообщений MCP.</summary>
public interface IMcpTransport : IDisposable
{
    Task StartAsync(CancellationToken ct);
    Task SendAsync(JObject message, CancellationToken ct);
    event EventHandler<JObject>? MessageReceived;
    event EventHandler<string>? Closed;
}

/// <summary>stdio: процесс сервера, сообщения — JSON по строке в stdin/stdout, stderr — в журнал.</summary>
public sealed class StdioTransport : IMcpTransport
{
    private readonly McpServerConfig _cfg;
    private Process? _p;
    private readonly SemaphoreSlim _write = new(1, 1);

    public StdioTransport(McpServerConfig cfg) => _cfg = cfg;

    public event EventHandler<JObject>? MessageReceived;
    public event EventHandler<string>? Closed;

    public Task StartAsync(CancellationToken ct)
    {
        var command = _cfg.Command ?? throw new InvalidOperationException("command is empty");
        var args = _cfg.Args ?? new List<string>();
        // npx/uvx и пр. на Windows — .cmd-скрипты: запускаем через cmd /c, иначе CreateProcess их не найдёт
        var needsCmd = !command.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) && !File.Exists(command);
        var psi = needsCmd
            ? new ProcessStartInfo("cmd.exe", "/d /s /c \"" + Engine.ServerLaunchOptions.JoinCommandLine(new[] { command }.Concat(args)) + "\"")
            : new ProcessStartInfo(command, Engine.ServerLaunchOptions.JoinCommandLine(args));
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false);
        psi.StandardErrorEncoding = new UTF8Encoding(false);
        if (!string.IsNullOrEmpty(_cfg.Cwd)) psi.WorkingDirectory = _cfg.Cwd;
        if (_cfg.Env != null) foreach (var kv in _cfg.Env) psi.EnvironmentVariables[kv.Key] = kv.Value;

        _p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _p.OutputDataReceived += (_, e) =>
        {
            if (string.IsNullOrWhiteSpace(e.Data)) return;
            try { MessageReceived?.Invoke(this, JObject.Parse(e.Data)); }
            catch (JsonException) { Log.Debug("MCP", $"[{_cfg.Name}] non-JSON stdout: {e.Data}"); }
        };
        _p.ErrorDataReceived += (_, e) => { if (e.Data != null) Log.Debug("MCP", $"[{_cfg.Name}] {e.Data}"); };
        _p.Exited += (_, _) => Closed?.Invoke(this, "process exited with code " + SafeExit());
        _p.Start();
        _p.BeginOutputReadLine();
        _p.BeginErrorReadLine();
        return Task.CompletedTask;
    }

    private string SafeExit()
    {
        try { return _p?.ExitCode.ToString() ?? "?"; }
        catch (InvalidOperationException) { return "?"; }
    }

    public async Task SendAsync(JObject message, CancellationToken ct)
    {
        if (_p == null || _p.HasExited) throw new IOException("MCP server process is not running");
        var line = message.ToString(Formatting.None);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // UTF-8 без BOM: пишем байты напрямую (StandardInput по умолчанию в кодировке консоли)
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _p.StandardInput.BaseStream.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await _p.StandardInput.BaseStream.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose()
    {
        try
        {
            if (_p is { HasExited: false })
            {
                _p.StandardInput.Close();
                if (!_p.WaitForExit(1500)) KillTree(_p.Id);
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
        _p?.Dispose();
    }

    private static void KillTree(int pid)
    {
        try { Process.Start(new ProcessStartInfo("taskkill", $"/T /F /PID {pid}") { CreateNoWindow = true, UseShellExecute = false })?.WaitForExit(3000); }
        catch (System.ComponentModel.Win32Exception) { }
    }
}

/// <summary>
/// Streamable HTTP (MCP 2025-03+): каждое сообщение — POST; ответ — JSON или SSE-поток сообщений.
/// Mcp-Session-Id из ответа на initialize передаётся во всех следующих запросах.
/// </summary>
public sealed class StreamableHttpTransport : IMcpTransport
{
    private readonly McpServerConfig _cfg;
    private readonly HttpService _http;
    private string? _session;

    public StreamableHttpTransport(McpServerConfig cfg, HttpService http)
    {
        _cfg = cfg;
        _http = http;
    }

    public event EventHandler<JObject>? MessageReceived;
#pragma warning disable CS0067 // у HTTP нет постоянного соединения — событие закрытия не используется
    public event EventHandler<string>? Closed;
#pragma warning restore CS0067

    public Task StartAsync(CancellationToken ct) => Task.CompletedTask;

    public async Task SendAsync(JObject message, CancellationToken ct)
    {
        var body = message.ToString(Formatting.None);
        using var resp = await _http.SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Post, _cfg.Url) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            if (_session != null) r.Headers.TryAddWithoutValidation("Mcp-Session-Id", _session);
            r.Headers.TryAddWithoutValidation("MCP-Protocol-Version", McpClient.ProtocolVersion);
            if (_cfg.Headers != null) foreach (var h in _cfg.Headers) r.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return r;
        }, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (resp.Headers.TryGetValues("Mcp-Session-Id", out var ids)) foreach (var id in ids) _session = id;
        if ((int)resp.StatusCode == 202) return; // уведомление принято
        if (!resp.IsSuccessStatusCode)
            throw new HttpStatusException(resp.StatusCode, _cfg.Url ?? "", await resp.Content.ReadAsStringAsync().ConfigureAwait(false));

        var type = resp.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (type.Contains("event-stream"))
        {
            using var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var sse = new SseReader(s);
            while (await sse.ReadAsync(ct).ConfigureAwait(false) is { } ev)
                Dispatch(ev.Data);
        }
        else
        {
            Dispatch(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
        }
    }

    private void Dispatch(string json)
    {
        if (string.IsNullOrWhiteSpace(json)) return;
        var token = JToken.Parse(json);
        if (token is JArray batch) foreach (var m in batch.OfType<JObject>()) MessageReceived?.Invoke(this, m);
        else if (token is JObject o) MessageReceived?.Invoke(this, o);
    }

    public void Dispose() { }
}

/// <summary>Старый HTTP+SSE (MCP 2024-11): GET открывает поток, первое событие endpoint — адрес для POST.</summary>
public sealed class LegacySseTransport : IMcpTransport
{
    private readonly McpServerConfig _cfg;
    private readonly HttpService _http;
    private readonly CancellationTokenSource _cts = new();
    private readonly TaskCompletionSource<string> _endpoint = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public LegacySseTransport(McpServerConfig cfg, HttpService http)
    {
        _cfg = cfg;
        _http = http;
    }

    public event EventHandler<JObject>? MessageReceived;
    public event EventHandler<string>? Closed;

    public async Task StartAsync(CancellationToken ct)
    {
        _ = Task.Run(() => ListenAsync(_cts.Token));
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(30));
        using (linked.Token.Register(() => _endpoint.TrySetCanceled()))
            await _endpoint.Task.ConfigureAwait(false);
    }

    private async Task ListenAsync(CancellationToken ct)
    {
        try
        {
            using var resp = await _http.SendAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, _cfg.Url);
                r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
                if (_cfg.Headers != null) foreach (var h in _cfg.Headers) r.Headers.TryAddWithoutValidation(h.Key, h.Value);
                return r;
            }, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            using var s = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false);
            var sse = new SseReader(s);
            while (await sse.ReadAsync(ct).ConfigureAwait(false) is { } ev)
            {
                if (ev.Event == "endpoint")
                {
                    _endpoint.TrySetResult(new Uri(new Uri(_cfg.Url!), ev.Data.Trim()).ToString());
                    continue;
                }
                try { MessageReceived?.Invoke(this, JObject.Parse(ev.Data)); }
                catch (JsonException) { }
            }
            Closed?.Invoke(this, "stream ended");
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException)
        {
            _endpoint.TrySetException(ex);
            if (!ct.IsCancellationRequested) Closed?.Invoke(this, ex.Message);
        }
    }

    public async Task SendAsync(JObject message, CancellationToken ct)
    {
        var endpoint = await _endpoint.Task.ConfigureAwait(false);
        var body = message.ToString(Formatting.None);
        using var resp = await _http.SendAsync(() => new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json"),
        }, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode && (int)resp.StatusCode != 202)
            throw new HttpStatusException(resp.StatusCode, endpoint, await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
    }

    public void Dispose() => _cts.Cancel();
}
