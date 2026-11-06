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
    private readonly Func<Uri, Uri?> _proxyResolver;
    private readonly NetworkCredential? _proxyCredentials;
    private readonly TimeSpan _connectTimeout;

    /// <param name="proxyResolver">Возвращает адрес HTTP-прокси для URL или null для прямого соединения.</param>
    public ManagedTlsHandler(TrustedRoots roots, Func<Uri, Uri?> proxyResolver, NetworkCredential? proxyCredentials, TimeSpan connectTimeout)
    {
        _roots = roots;
        _proxyResolver = proxyResolver;
        _proxyCredentials = proxyCredentials;
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
        var tcp = new TcpClient { NoDelay = true, ReceiveTimeout = 0 };
        var connection = new ConnectionOwner(tcp);
        using var cancelReg = ct.Register(() => connection.Dispose());
        try
        {
            var proxy = _proxyResolver(uri);
            var targetHost = proxy?.Host ?? uri.IdnHost;
            var targetPort = proxy?.Port ?? uri.Port;
            var connectTask = tcp.ConnectAsync(targetHost, targetPort);
            if (!connectTask.Wait(_connectTimeout))
                throw new HttpRequestException($"Connection to {targetHost}:{targetPort} timed out");
            if (connectTask.IsFaulted) throw new HttpRequestException($"Cannot connect to {targetHost}:{targetPort}", connectTask.Exception!.GetBaseException());

            Stream net = tcp.GetStream();
            if (proxy != null && uri.Scheme == Uri.UriSchemeHttps) EstablishTunnel(net, uri);

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

            WriteRequest(stream, original, method, uri, body, proxy != null && uri.Scheme == Uri.UriSchemeHttp);

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

    private void EstablishTunnel(Stream net, Uri uri)
    {
        var hostPort = uri.IdnHost + ":" + uri.Port.ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        sb.Append("CONNECT ").Append(hostPort).Append(" HTTP/1.1\r\n");
        sb.Append("Host: ").Append(hostPort).Append("\r\n");
        if (_proxyCredentials != null && !string.IsNullOrEmpty(_proxyCredentials.UserName))
        {
            var token = Convert.ToBase64String(Encoding.UTF8.GetBytes(_proxyCredentials.UserName + ":" + _proxyCredentials.Password));
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
