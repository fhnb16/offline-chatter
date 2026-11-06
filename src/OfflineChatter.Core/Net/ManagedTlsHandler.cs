using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;
using Org.BouncyCastle.Security;
using Org.BouncyCastle.Tls;
using Org.BouncyCastle.Tls.Crypto;
using Org.BouncyCastle.Tls.Crypto.Impl.BC;

namespace OfflineChatter.Core.Net;

/// <summary>
/// Уровень 3: HTTP/1.1 поверх собственного TLS 1.2/1.3 (BouncyCastle).
/// Нужен на Win7, где SChannel не умеет современные наборы шифров (ECDHE-GCM, TLS 1.3) и CDN отказываются от рукопожатия.
/// Одно соединение на запрос (Connection: close), редиректы обрабатываются здесь же.
/// Цепочка сертификатов проверяется по вшитым корням (<see cref="TrustedRoots"/>) + имя хоста по SAN.
/// </summary>
public sealed class ManagedTlsHandler : HttpMessageHandler
{
    private const int MaxRedirects = 10;
    private readonly TrustedRoots _roots;
    private readonly Func<Uri, ProxyEndpoint?> _proxyResolver;
    private readonly Func<IPAddress?> _localAddress;
    private readonly TimeSpan _connectTimeout;

    /// <param name="proxyResolver">Прокси (HTTP, SOCKS5, SOCKS5h) для URL или null — напрямую.</param>
    /// <param name="localAddress">Локальный адрес выбранного сетевого интерфейса или null — маршрут Windows по умолчанию.</param>
    public ManagedTlsHandler(TrustedRoots roots, Func<Uri, ProxyEndpoint?> proxyResolver, Func<IPAddress?> localAddress, TimeSpan connectTimeout)
    {
        _roots = roots;
        _proxyResolver = proxyResolver;
        _localAddress = localAddress;
        _connectTimeout = connectTimeout;
    }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var method = request.Method;
        var uri = request.RequestUri ?? throw new InvalidOperationException("RequestUri is null");
        var body = request.Content != null ? await request.Content.ReadAsByteArrayAsync().ConfigureAwait(false) : null;

        for (var hop = 0; ; hop++)
        {
            var response = await Task.Run(() => SendOnce(request, method, uri, body, ct), ct).ConfigureAwait(false);
            var code = (int)response.StatusCode;
            if (code is not (301 or 302 or 303 or 307 or 308) || response.Headers.Location == null || hop >= MaxRedirects)
                return response;

            var next = response.Headers.Location.IsAbsoluteUri ? response.Headers.Location : new Uri(uri, response.Headers.Location);
            response.Dispose();
            if (code == 303 || ((code is 301 or 302) && method == HttpMethod.Post))
            {
                method = HttpMethod.Get;
                body = null;
            }
            uri = next;
        }
    }

    private HttpResponseMessage SendOnce(HttpRequestMessage original, HttpMethod method, Uri uri, byte[]? body, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var proxy = _proxyResolver(uri);
        var local = _localAddress();
        var tcp = local != null ? new TcpClient(new IPEndPoint(local, 0)) : new TcpClient(AddressFamily.InterNetwork);
        tcp.NoDelay = true;
        var connection = new ConnectionOwner(tcp);
        using var cancelReg = ct.Register(() => connection.Dispose());
        try
        {
            var targetHost = proxy?.Host ?? uri.IdnHost;
            var targetPort = proxy?.Port ?? uri.Port;
            // При привязке к интерфейсу адрес должен быть того же семейства (IPv4), поэтому разрешаем имя сами
            var addresses = Dns.GetHostAddresses(targetHost).Where(a => a.AddressFamily == AddressFamily.InterNetwork).ToArray();
            if (addresses.Length == 0) throw new HttpRequestException($"No IPv4 address for {targetHost}");
            var connectTask = tcp.ConnectAsync(addresses, targetPort);
            if (!connectTask.Wait(_connectTimeout))
                throw new HttpRequestException($"Connection to {targetHost}:{targetPort} timed out");
            if (connectTask.IsFaulted) throw new HttpRequestException($"Cannot connect to {targetHost}:{targetPort}", connectTask.Exception!.GetBaseException());

            Stream net = tcp.GetStream();
            if (proxy is { Kind: ProxyKind.Socks5 or ProxyKind.Socks5h }) Socks5Connect(net, proxy, uri);
            else if (proxy != null && uri.Scheme == Uri.UriSchemeHttps) EstablishTunnel(net, uri, proxy.Credentials);
            Stream stream;
            if (uri.Scheme == Uri.UriSchemeHttps)
            {
                var protocol = new TlsClientProtocol(net);
                protocol.Connect(new Client(new BcTlsCrypto(new SecureRandom()), uri.IdnHost, _roots));
                connection.Tls = protocol;
                stream = protocol.Stream;
            }
            else
            {
                stream = net;
            }

            WriteRequest(stream, original, method, uri, body, proxy is { Kind: ProxyKind.Http } && uri.Scheme == Uri.UriSchemeHttp);

            var reader = new BufferedStream(stream, 64 * 1024);
            var head = HttpWire.ReadResponseHead(reader);
            var bodyStream = HttpWire.CreateBodyStream(reader, head, method == HttpMethod.Head, connection);

            var response = new HttpResponseMessage(HttpWire.Status(head.StatusCode))
            {
                ReasonPhrase = head.Reason,
                RequestMessage = original,
                Version = new Version(1, 1),
                Content = new StreamContent(bodyStream, 64 * 1024),
            };
            foreach (var h in head.Headers)
            {
                if (!response.Headers.TryAddWithoutValidation(h.Key, h.Value))
                    response.Content.Headers.TryAddWithoutValidation(h.Key, h.Value);
            }
            return response;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // При отмене соединение закрывается из другого потока, и BouncyCastle может бросить что угодно
            // (NullReference, InvalidOperation…) — приводим к ожидаемым типам.
            connection.Dispose();
            if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
            if (ex is HttpRequestException) throw;
            throw new HttpRequestException($"Managed TLS request to {uri.Host} failed: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// SOCKS5 (RFC 1928/1929): приветствие, при необходимости логин/пароль, CONNECT.
    /// SOCKS5h передаёт имя хоста прокси (DNS на стороне прокси), SOCKS5 — уже разрешённый IPv4.
    /// </summary>
    internal static void Socks5Connect(Stream s, ProxyEndpoint proxy, Uri target)
    {
        var auth = proxy.Credentials != null && !string.IsNullOrEmpty(proxy.Credentials.UserName);
        s.Write(auth ? new byte[] { 5, 2, 0, 2 } : new byte[] { 5, 1, 0 }, 0, auth ? 4 : 3);
        var reply = ReadExact(s, 2);
        if (reply[0] != 5) throw new HttpRequestException("Not a SOCKS5 proxy");
        if (reply[1] == 2)
        {
            if (!auth) throw new HttpRequestException("SOCKS5 proxy requires a login and password");
            var u = Encoding.UTF8.GetBytes(proxy.Credentials!.UserName);
            var p = Encoding.UTF8.GetBytes(proxy.Credentials.Password ?? string.Empty);
            var msg = new List<byte> { 1, (byte)u.Length };
            msg.AddRange(u);
            msg.Add((byte)p.Length);
            msg.AddRange(p);
            s.Write(msg.ToArray(), 0, msg.Count);
            if (ReadExact(s, 2)[1] != 0) throw new HttpRequestException("SOCKS5 authentication failed");
        }
        else if (reply[1] != 0)
        {
            throw new HttpRequestException("SOCKS5 proxy rejected all authentication methods");
        }

        var req = new List<byte> { 5, 1, 0 };
        if (proxy.Kind == ProxyKind.Socks5h)
        {
            var host = Encoding.ASCII.GetBytes(target.IdnHost);
            req.Add(3);
            req.Add((byte)host.Length);
            req.AddRange(host);
        }
        else
        {
            var ip = Dns.GetHostAddresses(target.IdnHost).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
                     ?? throw new HttpRequestException("No IPv4 address for " + target.Host);
            req.Add(1);
            req.AddRange(ip.GetAddressBytes());
        }
        req.Add((byte)(target.Port >> 8));
        req.Add((byte)(target.Port & 0xFF));
        s.Write(req.ToArray(), 0, req.Count);

        var head = ReadExact(s, 4);
        if (head[1] != 0)
        {
            var reason = head[1] switch { 1 => "general failure", 2 => "not allowed", 3 => "network unreachable", 4 => "host unreachable", 5 => "connection refused", 6 => "TTL expired", 7 => "command not supported", 8 => "address type not supported", _ => "error " + head[1] };
            throw new HttpRequestException("SOCKS5 CONNECT failed: " + reason);
        }
        var skip = head[3] switch { 1 => 4, 4 => 16, 3 => ReadExact(s, 1)[0], _ => 0 };
        ReadExact(s, skip + 2);
    }

    private static byte[] ReadExact(Stream s, int n)
    {
        var buf = new byte[n];
        var read = 0;
        while (read < n)
        {
            var r = s.Read(buf, read, n - read);
            if (r <= 0) throw new IOException("Proxy closed the connection");
            read += r;
        }
        return buf;
    }

    private static void EstablishTunnel(Stream net, Uri uri, NetworkCredential? credentials)
    {
        var hostPort = uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("CONNECT ").Append(hostPort).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(hostPort).Append("\r\n");
        if (credentials != null && !string.IsNullOrEmpty(credentials.UserName))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(credentials.UserName + ":" + credentials.Password));
            sb.Append("Proxy-Authorization: Basic ").Append(token).Append("\r\n");
        }
        sb.Append("\r\n");
        var bytes = Encoding.ASCII.GetBytes(sb.ToString());
        net.Write(bytes, 0, bytes.Length);
        net.Flush();

        // Не используем буфер: после ответа прокси поток принадлежит TLS.
        var head = HttpWire.ReadResponseHead(net);
        if (head.StatusCode != 200)
            throw new HttpRequestException($"Proxy CONNECT failed: {head.StatusCode} {head.Reason}");
    }

    private static void WriteRequest(Stream stream, HttpRequestMessage original, HttpMethod method, Uri uri, byte[]? body, bool absoluteForm)
    {
        var sb = new StringBuilder(512);
        sb.Append(method.Method).Append(' ').Append(absoluteForm ? uri.AbsoluteUri : uri.PathAndQuery).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(HttpWire.FormatHostHeader(uri)).Append("\r\n");

        foreach (var h in original.Headers)
        {
            if (h.Key.Equals("Host", StringComparison.OrdinalIgnoreCase) || h.Key.Equals("Connection", StringComparison.OrdinalIgnoreCase)
                || h.Key.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase))
                continue;
            // Authorization не переносим на чужой хост после редиректа (HF → CDN с подписанным URL)
            if (h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase) && original.RequestUri != null &&
                !string.Equals(original.RequestUri.Host, uri.Host, StringComparison.OrdinalIgnoreCase))
                continue;
            sb.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append("\r\n");
        }

        if (body != null && original.Content != null)
        {
            foreach (var h in original.Content.Headers)
            {
                if (h.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)) continue;
                sb.Append(h.Key).Append(": ").Append(string.Join(", ", h.Value)).Append("\r\n");
            }
        }
        if (body != null) sb.Append("Content-Length: ").Append(body.Length.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        sb.Append("Connection: close\r\n\r\n");

        var headBytes = Encoding.UTF8.GetBytes(sb.ToString());
        stream.Write(headBytes, 0, headBytes.Length);
        if (body is { Length: > 0 }) stream.Write(body, 0, body.Length);
        stream.Flush();
    }

    /// <summary>Владелец соединения: закрывает TLS и сокет вместе с потоком тела ответа.</summary>
    private sealed class ConnectionOwner : IDisposable
    {
        private readonly TcpClient _tcp;
        private int _disposed;

        public ConnectionOwner(TcpClient tcp) => _tcp = tcp;

        public TlsClientProtocol? Tls { get; set; }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            try { Tls?.Close(); } catch (Exception ex) when (ex is IOException or TlsException or ObjectDisposedException) { }
            try { _tcp.Close(); } catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException) { }
        }
    }

    private sealed class Client : DefaultTlsClient
    {
        private readonly string _host;
        private readonly TrustedRoots _roots;

        public Client(TlsCrypto crypto, string host, TrustedRoots roots) : base(crypto)
        {
            _host = host;
            _roots = roots;
        }

        protected override ProtocolVersion[] GetSupportedVersions() => ProtocolVersion.TLSv13.DownTo(ProtocolVersion.TLSv12);

        protected override IList<ServerName> GetSniServerNames() =>
            new List<ServerName> { new(NameType.host_name, Encoding.ASCII.GetBytes(_host)) };

        protected override IList<ProtocolName> GetProtocolNames() => new List<ProtocolName> { ProtocolName.Http_1_1 };

        public override TlsAuthentication GetAuthentication() => new Auth(_host, _roots);
    }

    private sealed class Auth : TlsAuthentication
    {
        private readonly string _host;
        private readonly TrustedRoots _roots;

        public Auth(string host, TrustedRoots roots)
        {
            _host = host;
            _roots = roots;
        }

        public void NotifyServerCertificate(TlsServerCertificate serverCertificate)
        {
            var chain = serverCertificate.Certificate;
            if (chain == null || chain.IsEmpty) throw new TlsFatalAlert(AlertDescription.bad_certificate);

            var certs = Enumerable.Range(0, chain.Length).Select(i => new X509Certificate2(chain.GetCertificateAt(i).GetEncoded())).ToList();
            var leaf = certs[0];

            if (!HostnameMatcher.Matches(leaf, _host))
            {
                Log.Warn("Net", $"Managed TLS: certificate does not match host {_host}");
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }

            // Сначала доверие системы (если корень есть в хранилище), иначе — наши корни
            using (var sys = new X509Chain())
            {
                sys.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                foreach (var c in certs.Skip(1)) sys.ChainPolicy.ExtraStore.Add(c);
                if (sys.Build(leaf)) return;
            }

            if (!_roots.ValidateChain(leaf, certs.Skip(1), out var reason))
            {
                Log.Warn("Net", $"Managed TLS: chain validation failed for {_host}: {reason}");
                throw new TlsFatalAlert(AlertDescription.bad_certificate);
            }
        }

        public TlsCredentials GetClientCredentials(Org.BouncyCastle.Tls.CertificateRequest certificateRequest) => null!;
    }
}
