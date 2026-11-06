using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Net;

public enum TlsLevel
{
    System = 1,
    PinnedRoots = 2,
    Managed = 3,
}

public sealed record NetCheckResult(string Host, bool Ok, TlsLevel? Level, TimeSpan Elapsed, string Message);

/// <summary>
/// Единая точка исходящих HTTP-запросов. Три уровня TLS с автоэскалацией:
/// 1) SChannel; 2) SChannel + вшитые корни; 3) BouncyCastle. Уровень, который сработал, запоминается для хоста.
/// </summary>
public sealed class HttpService : IDisposable
{
    private readonly NetworkSettings _settings;
    private readonly TrustedRoots _roots;
    private readonly ConcurrentDictionary<string, TlsLevel> _hostLevels = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _gate = new();
    private readonly Dictionary<TlsLevel, HttpClient> _clients = new();

    public HttpService(NetworkSettings settings, TrustedRoots roots, string appVersion)
    {
        _settings = settings;
        _roots = roots;
        UserAgent = "OfflineChatter/" + appVersion;
    }

    public string UserAgent { get; }

    public NetworkSettings Settings => _settings;

    public TrustedRoots Roots => _roots;

    /// <summary>
    /// TLS для SChannel. На Win10+ оставляем системные версии (включая TLS 1.3),
    /// на Win7/8 явно включаем TLS 1.2 — по умолчанию .NET там его не предлагает.
    /// </summary>
    public static void ConfigureGlobalTls(bool modernOs)
    {
        if (!modernOs)
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12 | SecurityProtocolType.Tls11 | SecurityProtocolType.Tls;
        ServicePointManager.DefaultConnectionLimit = Math.Max(ServicePointManager.DefaultConnectionLimit, 16);
        ServicePointManager.Expect100Continue = false;
    }

    /// <summary>Пересоздать клиентов после смены настроек прокси/TLS.</summary>
    public void Reconfigure()
    {
        lock (_gate)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
        _hostLevels.Clear();
    }

    public TlsLevel? GetRememberedLevel(string host) => _hostLevels.TryGetValue(host, out var l) ? l : null;

    private HttpClient GetClient(TlsLevel level)
    {
        lock (_gate)
        {
            if (_clients.TryGetValue(level, out var existing)) return existing;
            var client = new HttpClient(CreateHandler(level), disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
            client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
            _clients[level] = client;
            return client;
        }
    }

    private HttpMessageHandler CreateHandler(TlsLevel level)
    {
        var creds = ProxyCredentials();
        if (level == TlsLevel.Managed)
            return new ManagedTlsHandler(_roots, ResolveProxy, creds, TimeSpan.FromSeconds(_settings.TimeoutSeconds));

        var handler = new HttpClientHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate,
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 10,
        };
        switch (_settings.ProxyMode)
        {
            case ProxyMode.None:
                handler.UseProxy = false;
                break;
            case ProxyMode.Manual when Uri.TryCreate(_settings.ProxyAddress, UriKind.Absolute, out var proxyUri):
                handler.UseProxy = true;
                handler.Proxy = new WebProxy(proxyUri) { Credentials = creds };
                break;
            default:
                handler.UseProxy = true;
                handler.DefaultProxyCredentials = creds ?? CredentialCache.DefaultCredentials;
                break;
        }

        if (level == TlsLevel.PinnedRoots)
            handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
                _roots.Validate(cert, chain, errors, req.RequestUri?.Host ?? "?");
        return handler;
    }

    private NetworkCredential? ProxyCredentials()
    {
        if (string.IsNullOrEmpty(_settings.ProxyUser)) return null;
        return new NetworkCredential(_settings.ProxyUser, Secrets.UnprotectOrEmpty(_settings.ProxyPasswordProtected));
    }

    private Uri? ResolveProxy(Uri target)
    {
        switch (_settings.ProxyMode)
        {
            case ProxyMode.None:
                return null;
            case ProxyMode.Manual:
                return Uri.TryCreate(_settings.ProxyAddress, UriKind.Absolute, out var p) ? p : null;
            default:
                try
                {
                    var sys = WebRequest.GetSystemWebProxy().GetProxy(target);
                    return sys == null || sys == target ? null : sys;
                }
                catch (Exception ex) when (ex is NotSupportedException or InvalidOperationException or System.Security.SecurityException)
                {
                    return null;
                }
        }
    }

    private IEnumerable<TlsLevel> LevelsFor(string host)
    {
        switch (_settings.TlsMode)
        {
            case TlsMode.System: yield return TlsLevel.System; yield break;
            case TlsMode.PinnedRoots: yield return TlsLevel.PinnedRoots; yield break;
            case TlsMode.Managed: yield return TlsLevel.Managed; yield break;
        }

        var start = GetRememberedLevel(host) ?? TlsLevel.System;
        for (var l = start; l <= TlsLevel.Managed; l++) yield return l;
    }

    /// <summary>
    /// Отправить запрос. <paramref name="makeRequest"/> вызывается заново при эскалации уровня TLS
    /// (HttpRequestMessage нельзя отправить дважды).
    /// </summary>
    public async Task<HttpResponseMessage> SendAsync(Func<HttpRequestMessage> makeRequest, HttpCompletionOption completion, CancellationToken ct)
    {
        Exception? last = null;
        string host = "?";
        foreach (var level in LevelsFor(PeekHost(makeRequest)))
        {
            var request = makeRequest();
            host = request.RequestUri?.Host ?? "?";
            try
            {
                var response = await GetClient(level).SendAsync(request, completion, ct).ConfigureAwait(false);
                if (GetRememberedLevel(host) != level)
                {
                    _hostLevels[host] = level;
                    if (level != TlsLevel.System) Log.Info("Net", $"Using TLS level {level} for {host}");
                }
                return response;
            }
            catch (Exception ex) when (_settings.TlsMode == TlsMode.Auto && level < TlsLevel.Managed && IsTlsFailure(ex) && !ct.IsCancellationRequested)
            {
                last = ex;
                Log.Warn("Net", $"TLS level {level} failed for {host}, escalating", ex);
            }
        }
        throw last ?? new HttpRequestException("Request failed: " + host);
    }

    private static string PeekHost(Func<HttpRequestMessage> make)
    {
        using var probe = make();
        return probe.RequestUri?.Host ?? string.Empty;
    }

    /// <summary>Ошибка рукопожатия или доверия, которую может исправить следующий уровень TLS.</summary>
    public static bool IsTlsFailure(Exception ex)
    {
        for (var e = ex; e != null; e = e.InnerException)
        {
            switch (e)
            {
                case WebException we when we.Status is WebExceptionStatus.TrustFailure or WebExceptionStatus.SecureChannelFailure:
                case AuthenticationException:
                    return true;
                case IOException io when io.Message.IndexOf("handshake", StringComparison.OrdinalIgnoreCase) >= 0:
                    return true;
            }
        }
        return false;
    }

    // ---------------- удобные обёртки ----------------

    public async Task<string> GetStringAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
        using var response = await SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, url);
            configure?.Invoke(r);
            return r;
        }, HttpCompletionOption.ResponseContentRead, cts.Token).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
            throw new HttpStatusException(response.StatusCode, url, text);
        return text;
    }

    public async Task<JToken> GetJsonAsync(string url, CancellationToken ct, Action<HttpRequestMessage>? configure = null)
    {
        var text = await GetStringAsync(url, ct, r =>
        {
            r.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            configure?.Invoke(r);
        }).ConfigureAwait(false);
        return JToken.Parse(text);
    }

    /// <summary>Проверка доступности хоста: пробует уровни по очереди и сообщает, какой сработал.</summary>
    public async Task<NetCheckResult> CheckAsync(string url, CancellationToken ct)
    {
        var host = new Uri(url).Host;
        var sw = Stopwatch.StartNew();
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(_settings.TimeoutSeconds));
            using var response = await SendAsync(() => new HttpRequestMessage(HttpMethod.Head, url), HttpCompletionOption.ResponseHeadersRead, cts.Token)
                .ConfigureAwait(false);
            return new NetCheckResult(host, true, GetRememberedLevel(host), sw.Elapsed, $"HTTP {(int)response.StatusCode}");
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or IOException)
        {
            return new NetCheckResult(host, false, null, sw.Elapsed, ex.GetBaseException().Message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            foreach (var c in _clients.Values) c.Dispose();
            _clients.Clear();
        }
    }
}

public sealed class HttpStatusException : HttpRequestException
{
    public HttpStatusException(HttpStatusCode status, string url, string body)
        : base($"HTTP {(int)status} {status} for {url}")
    {
        Status = status;
        Body = body.Length > 2000 ? body.Substring(0, 2000) : body;
    }

    public HttpStatusCode Status { get; }
    public string Body { get; }
}
