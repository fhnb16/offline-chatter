using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Core.Net;

/// <summary>
/// Обратный прокси с проверкой ключа для серверов без собственной авторизации (sd-server).
/// Слушает публичный адрес и порт, принимает «Authorization: Bearer &lt;ключ&gt;» (или x-api-key / api-key),
/// пересылает запрос на 127.0.0.1:upstream. Каждое соединение — один запрос («Connection: close»): прокси разбирает
/// только заголовки, тело и ответ (в том числе потоковые) идут насквозь. Добавляет CORS-заголовки для браузерных клиентов.
/// </summary>
public sealed class AuthProxy : IDisposable
{
    private const int MaxHead = 64 * 1024;
    private readonly string _listen;
    private readonly int _port;
    private readonly int _upstreamPort;
    private readonly Func<string> _key;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<TcpListener> _listeners = new();

    public AuthProxy(string listenAddress, int port, int upstreamPort, Func<string> key)
    {
        _listen = string.IsNullOrWhiteSpace(listenAddress) ? "127.0.0.1" : listenAddress;
        _port = port;
        _upstreamPort = upstreamPort;
        _key = key;
    }

    public int Port => _port;
    public int UpstreamPort => _upstreamPort;
    public string ListenAddress => _listen;
    public bool IsRunning => _listeners.Count > 0;

    /// <summary>Начать слушать. Ошибка (порт занят, адрес не найден) — текстом.</summary>
    public string? Start()
    {
        try
        {
            var ip = IPAddress.Parse(_listen);
            var main = new TcpListener(ip, _port);
            main.Start();
            _listeners.Add(main);
            // На петле — ещё и [::1]: «localhost» в Windows сначала разрешается в IPv6
            if (IPAddress.IsLoopback(ip) && Socket.OSSupportsIPv6)
            {
                try
                {
                    var v6 = new TcpListener(IPAddress.IPv6Loopback, _port);
                    v6.Server.SetSocketOption(SocketOptionLevel.IPv6, SocketOptionName.IPv6Only, true);
                    v6.Start();
                    _listeners.Add(v6);
                }
                catch (SocketException) { }
            }
            foreach (var l in _listeners) _ = Task.Run(() => AcceptLoopAsync(l));
            Log.Info("Net", "Auth proxy started", new { listen = _listen, port = _port, upstream = _upstreamPort });
            return null;
        }
        catch (Exception ex) when (ex is SocketException or FormatException)
        {
            Dispose();
            Log.Warn("Net", "Auth proxy not started", ex);
            return ex.Message;
        }
    }

    private async Task AcceptLoopAsync(TcpListener listener)
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync().ConfigureAwait(false); }
            catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException) { return; }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var cs = client.GetStream();
                var (head, rest) = await ReadHeadAsync(cs, _cts.Token).ConfigureAwait(false);
                if (head == null) return;
                var req = ParseHead(head);
                if (req == null)
                {
                    await WriteSimpleAsync(cs, 400, "Bad Request", "{\"error\":{\"message\":\"Bad request\"}}").ConfigureAwait(false);
                    return;
                }
                if (req.Method == "OPTIONS")
                {
                    await WriteSimpleAsync(cs, 204, "No Content", null).ConfigureAwait(false);
                    return;
                }
                if (!IsAuthorized(req.Headers, _key()))
                {
                    await WriteSimpleAsync(cs, 401, "Unauthorized",
                        "{\"error\":{\"message\":\"Invalid API key\",\"type\":\"invalid_request_error\",\"code\":\"invalid_api_key\"}}").ConfigureAwait(false);
                    return;
                }

                using var upstream = new TcpClient(AddressFamily.InterNetwork) { NoDelay = true };
                await upstream.ConnectAsync(IPAddress.Loopback, _upstreamPort).ConfigureAwait(false);
                var us = upstream.GetStream();
                var outHead = Encoding.ASCII.GetBytes(RewriteRequest(req, _upstreamPort));
                await us.WriteAsync(outHead, 0, outHead.Length, _cts.Token).ConfigureAwait(false);
                if (rest.Length > 0) await us.WriteAsync(rest, 0, rest.Length, _cts.Token).ConfigureAwait(false);

                var toServer = CopyAsync(cs, us);
                var toClient = CopyResponseAsync(us, cs);
                if (await Task.WhenAny(toServer, toClient).ConfigureAwait(false) == toServer)
                {
                    try { upstream.Client.Shutdown(SocketShutdown.Send); }
                    catch (Exception ex) when (ex is SocketException or ObjectDisposedException) { }
                    await toClient.ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (ex is SocketException or IOException or ObjectDisposedException or OperationCanceledException)
            {
                // клиент отключился или сервер перезапускается
            }
        }
    }

    /// <summary>Ответ сервера: в заголовки добавляется CORS, остальное — как есть.</summary>
    private async Task CopyResponseAsync(Stream from, Stream to)
    {
        try
        {
            var (head, rest) = await ReadHeadAsync(from, _cts.Token).ConfigureAwait(false);
            if (head == null) return;
            var bytes = Encoding.ASCII.GetBytes(AddCors(head));
            await to.WriteAsync(bytes, 0, bytes.Length, _cts.Token).ConfigureAwait(false);
            if (rest.Length > 0) await to.WriteAsync(rest, 0, rest.Length, _cts.Token).ConfigureAwait(false);
            await CopyAsync(from, to).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
    }

    private async Task CopyAsync(Stream from, Stream to)
    {
        var buffer = new byte[32 * 1024];
        try
        {
            int n;
            while ((n = await from.ReadAsync(buffer, 0, buffer.Length, _cts.Token).ConfigureAwait(false)) > 0)
            {
                await to.WriteAsync(buffer, 0, n, _cts.Token).ConfigureAwait(false);
                await to.FlushAsync(_cts.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException or SocketException) { }
    }

    /// <summary>Чтение до пустой строки; возвращает заголовки и уже прочитанную часть тела.</summary>
    internal static async Task<(string? head, byte[] rest)> ReadHeadAsync(Stream s, CancellationToken ct)
    {
        var buf = new byte[8192];
        var ms = new MemoryStream();
        while (ms.Length < MaxHead)
        {
            var n = await s.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
            if (n <= 0) return (null, Array.Empty<byte>());
            ms.Write(buf, 0, n);
            var data = ms.GetBuffer();
            var len = (int)ms.Length;
            for (var i = Math.Max(0, len - n - 3); i + 3 < len; i++)
            {
                if (data[i] == '\r' && data[i + 1] == '\n' && data[i + 2] == '\r' && data[i + 3] == '\n')
                {
                    var head = Encoding.ASCII.GetString(data, 0, i + 4);
                    var rest = new byte[len - i - 4];
                    Array.Copy(data, i + 4, rest, 0, rest.Length);
                    return (head, rest);
                }
            }
        }
        return (null, Array.Empty<byte>());
    }

    internal sealed record RequestHead(string Method, string Target, string Version, List<KeyValuePair<string, string>> Headers);

    internal static RequestHead? ParseHead(string head)
    {
        var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/", StringComparison.Ordinal)) return null;
        var headers = new List<KeyValuePair<string, string>>();
        foreach (var l in lines.Skip(1))
        {
            if (l.Length == 0) break;
            var colon = l.IndexOf(':');
            if (colon <= 0) return null;
            headers.Add(new KeyValuePair<string, string>(l.Substring(0, colon).Trim(), l.Substring(colon + 1).Trim()));
        }
        return new RequestHead(first[0].ToUpperInvariant(), first[1], first[2], headers);
    }

    /// <summary>Ключ: Bearer в Authorization, либо x-api-key / api-key. Сравнение без утечки по времени.</summary>
    internal static bool IsAuthorized(IEnumerable<KeyValuePair<string, string>> headers, string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (var h in headers)
        {
            string? candidate = null;
            if (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && h.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
                candidate = h.Value.Substring(7).Trim();
            else if (h.Key.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("api-key", StringComparison.OrdinalIgnoreCase))
                candidate = h.Value.Trim();
            if (candidate != null && FixedTimeEquals(candidate, key)) return true;
        }
        return false;
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var x = Encoding.UTF8.GetBytes(a);
        var y = Encoding.UTF8.GetBytes(b);
        var diff = x.Length ^ y.Length;
        for (var i = 0; i < Math.Max(x.Length, y.Length); i++)
            diff |= (i < x.Length ? x[i] : 0) ^ (i < y.Length ? y[i] : 0);
        return diff == 0;
    }

    /// <summary>Заголовки для сервера: без ключа и keep-alive, Host — на петлю, «Connection: close».</summary>
    internal static string RewriteRequest(RequestHead r, int upstreamPort)
    {
        var drop = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Connection", "Keep-Alive", "Proxy-Connection", "Authorization", "x-api-key", "api-key", "Host" };
        var sb = new StringBuilder();
        sb.Append(r.Method).Append(' ').Append(r.Target).Append(' ').Append(r.Version).Append("\r\n");
        sb.Append("Host: 127.0.0.1:").Append(upstreamPort).Append("\r\n");
        foreach (var h in r.Headers.Where(h => !drop.Contains(h.Key)))
            sb.Append(h.Key).Append(": ").Append(h.Value).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");
        return sb.ToString();
    }

    private const string CorsHeaders =
        "Access-Control-Allow-Origin: *\r\nAccess-Control-Allow-Headers: Authorization, Content-Type, x-api-key, api-key\r\nAccess-Control-Allow-Methods: GET, POST, OPTIONS\r\n";

    internal static string AddCors(string responseHead)
    {
        var end = responseHead.IndexOf("\r\n", StringComparison.Ordinal);
        if (end < 0 || responseHead.IndexOf("Access-Control-Allow-Origin", StringComparison.OrdinalIgnoreCase) >= 0) return responseHead;
        return responseHead.Substring(0, end + 2) + CorsHeaders + responseHead.Substring(end + 2);
    }

    private static async Task WriteSimpleAsync(Stream s, int code, string reason, string? json)
    {
        var body = json == null ? Array.Empty<byte>() : Encoding.UTF8.GetBytes(json);
        var head = $"HTTP/1.1 {code} {reason}\r\n" + CorsHeaders +
                   (json != null ? "Content-Type: application/json\r\n" : string.Empty) +
                   $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        var h = Encoding.ASCII.GetBytes(head);
        await s.WriteAsync(h, 0, h.Length).ConfigureAwait(false);
        if (body.Length > 0) await s.WriteAsync(body, 0, body.Length).ConfigureAwait(false);
        await s.FlushAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        _cts.Cancel();
        foreach (var l in _listeners)
        {
            try { l.Stop(); } catch (SocketException) { }
        }
        _listeners.Clear();
    }
}
