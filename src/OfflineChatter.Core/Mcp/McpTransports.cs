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
    private readonly object _lifetime = new();
    private Process? _p;
    private bool _disposed;
    private readonly SemaphoreSlim _write = new(1, 1);

    public StdioTransport(McpServerConfig cfg) => _cfg = cfg;
    internal string? WorkingDirectory => _cfg.Cwd;

    public event EventHandler<JObject>? MessageReceived;
    public event EventHandler<string>? Closed;

    public Task StartAsync(CancellationToken ct) => Task.Run(() =>
    {
        var psi = CreateStartInfo(_cfg);
        lock (_lifetime)
        {
            ct.ThrowIfCancellationRequested();
            if (_disposed) throw new ObjectDisposedException(nameof(StdioTransport));

            var process = new Process { StartInfo = psi };
            _p = process;
            process.OutputDataReceived += (_, e) =>
            {
                if (e.Data == null)
                {
                    var reason = "stdout closed";
                    try { if (process.HasExited) reason = $"process exited with code {process.ExitCode}"; }
                    catch (InvalidOperationException) { }
                    Closed?.Invoke(this, reason);
                    return;
                }
                if (string.IsNullOrWhiteSpace(e.Data)) return;
                try { MessageReceived?.Invoke(this, JObject.Parse(e.Data)); }
                catch (JsonException) { Log.Debug("MCP", $"[{_cfg.Name}] non-JSON stdout"); }
            };
            process.ErrorDataReceived += (_, e) => { if (e.Data != null) Log.Debug("MCP", $"[{_cfg.Name}] {e.Data}"); };
            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
        }
    }, ct);

    internal static ProcessStartInfo CreateStartInfo(McpServerConfig cfg)
    {
        var command = cfg.Command?.Trim();
        if (string.IsNullOrEmpty(command)) throw new InvalidOperationException("MCP command is empty");
        var psi = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = string.IsNullOrWhiteSpace(cfg.Cwd) ? AppDomain.CurrentDomain.BaseDirectory : cfg.Cwd!,
        };
        if (!Path.IsPathRooted(psi.WorkingDirectory) || !Directory.Exists(psi.WorkingDirectory))
            throw new DirectoryNotFoundException("MCP working directory must be an existing absolute path: " + psi.WorkingDirectory);
        if (cfg.Env != null)
            foreach (var kv in cfg.Env) psi.EnvironmentVariables[kv.Key] = kv.Value;

        var executable = FindCommand(command!, psi.WorkingDirectory, psi.EnvironmentVariables["PATH"]);
        var args = cfg.Args ?? new List<string>();
        if (Path.GetExtension(executable).ToLowerInvariant() is ".cmd" or ".bat")
        {
            var words = new[] { executable }.Concat(args);
            if (words.Any(word => word.IndexOfAny(new[] { '"', '%', '\r', '\n', '\0' }) >= 0))
                throw new ArgumentException("Batch commands do not support quotes, percent signs or line breaks inside arguments; use the executable directly");
            // cmd /s removes the outer quotes. Quote each word to protect shell metacharacters.
            psi.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            psi.Arguments = "/d /v:off /s /c \"" + string.Join(" ", words.Select(word => "\"" + word + "\"")) + "\"";
        }
        else
        {
            psi.FileName = executable;
            psi.Arguments = Engine.ServerLaunchOptions.JoinCommandLine(args);
        }
        return psi;
    }

    private static string FindCommand(string command, string cwd, string? path)
    {
        var hasDirectory = command.IndexOfAny(new[] { '\\', '/', ':' }) >= 0;
        var dirs = hasDirectory ? new[] { cwd } : (path ?? string.Empty).Split(';');
        var extensions = Path.HasExtension(command) ? new[] { "" } : new[] { ".exe", ".com", ".cmd", ".bat", "" };
        foreach (var dir in dirs)
        {
            var root = dir.Trim().Trim('"');
            if (!Path.IsPathRooted(root)) continue;
            foreach (var extension in extensions)
            {
                var file = Path.Combine(root, command + extension);
                if (File.Exists(file)) return Path.GetFullPath(file);
            }
        }
        throw new FileNotFoundException("MCP command not found. Add it to PATH or specify its full path: " + command);
    }

    public async Task SendAsync(JObject message, CancellationToken ct)
    {
        var line = message.ToString(Formatting.None);
        await _write.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var process = _p ?? throw new IOException("MCP server process is not running");
            // UTF-8 без BOM: пишем байты напрямую (StandardInput по умолчанию в кодировке консоли)
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            var pipe = process.StandardInput.BaseStream;
            await pipe.WriteAsync(bytes, 0, bytes.Length, ct).ConfigureAwait(false);
            await pipe.FlushAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _write.Release();
        }
    }

    public void Dispose()
    {
        Process? process;
        lock (_lifetime)
        {
            _disposed = true;
            process = _p;
            _p = null;
        }
        if (process == null) return;
        using (process)
        try
        {
            if (!process.HasExited)
            {
                var pid = process.Id;
                try { process.StandardInput.BaseStream.Close(); }
                finally { if (!process.WaitForExit(1500)) KillTree(pid); }
            }
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or System.ComponentModel.Win32Exception) { }
    }

    private static void KillTree(int pid)
    {
        try
        {
            using var kill = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "taskkill.exe"), $"/T /F /PID {pid}")
                { CreateNoWindow = true, UseShellExecute = false });
            kill?.WaitForExit(3000);
        }
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
