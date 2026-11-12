using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Net;

namespace OfflineChatter.Core.Tools;

/// <summary>Адреса, которые модель по умолчанию открывать не должна: этот компьютер и локальная сеть.</summary>
public static class WebGuard
{
    private static readonly string[] LocalSuffixes = { ".localhost", ".local", ".lan", ".internal", ".intranet", ".home", ".home.arpa", ".corp" };

    /// <summary>
    /// Локальный ли адрес. Проверяется то, что написано в ссылке: имя не разрешается через DNS
    /// (запрос может идти через прокси, который разрешает имена сам).
    /// </summary>
    public static bool IsLocal(Uri uri)
    {
        var host = uri.DnsSafeHost.Trim('[', ']').ToLowerInvariant();
        if (host.Length == 0) return true;
        if (IPAddress.TryParse(host, out var ip)) return IsLocal(ip);
        return host == "localhost" || host.IndexOf('.') < 0 || LocalSuffixes.Any(s => host.EndsWith(s, StringComparison.Ordinal));
    }

    public static bool IsLocal(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        if (IPAddress.IsLoopback(ip)) return true;
        var b = ip.GetAddressBytes();
        if (ip.AddressFamily == AddressFamily.InterNetwork)
            return b[0] is 0 or 10 or 127 || b[0] >= 224
                   || (b[0] == 169 && b[1] == 254)
                   || (b[0] == 172 && b[1] is >= 16 and <= 31)
                   || (b[0] == 192 && b[1] == 168)
                   || (b[0] == 100 && b[1] is >= 64 and <= 127);
        return ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || (b[0] & 0xFE) == 0xFC || ip.Equals(IPAddress.IPv6Any);
    }
}

/// <summary>Встроенные web_fetch и web_search: загрузка страницы, очистка, поиск.</summary>
public sealed class WebTools
{
    private const string LocalRefusal = "Local and private network addresses are turned off in the Tools settings";
    private const string HtmlAccept ="text/html,application/xhtml+xml,application/xml;q=0.9,text/plain;q=0.8,*/*;q=0.5";

    private readonly HttpService _http;
    private readonly ToolsSettings _tools;
    private readonly object _cacheGate = new();
    private readonly List<CachedPage> _cache = new();

    public WebTools(ToolsSettings tools, HttpService http)
    {
        _tools = tools;
        _http = http;
    }

    private WebToolsSettings S => _tools.Web;

    private sealed record Download(Uri Final, string MediaType, string Text, bool Cut);

    private sealed record CachedPage(string Key, DateTime At, Uri Final, PageText Page, bool Cut);

    // ---------------- web_fetch ----------------

    public async Task<string> FetchAsync(JObject args, CancellationToken ct)
    {
        var uri = ParseUrl((string?)args["url"] ?? throw new ArgumentException("url is required"));
        var start = Math.Max(0, ReadInt(args["start"]) ?? 0);
        var s = S;
        // До кэша: запрет действует и на страницу, скачанную до того, как локальные адреса закрыли
        if (!s.AllowLocalAddresses && WebGuard.IsLocal(uri)) throw new UnauthorizedAccessException(LocalRefusal);
        var options = s.CleanOptions;
        var key = uri.AbsoluteUri + "|" + Signature(options);

        CachedPage? cached;
        lock (_cacheGate)
        {
            _cache.RemoveAll(c => (DateTime.UtcNow - c.At).TotalMinutes > 5);
            cached = _cache.FirstOrDefault(c => c.Key == key);
        }
        if (cached == null)
        {
            var d = await DownloadAsync(uri, HtmlAccept, guard: true, ct).ConfigureAwait(false);
            cached = new CachedPage(key, DateTime.UtcNow, d.Final, ToPage(d, options), d.Cut);
            // Продолжение длинной страницы (start) не должно скачивать её заново
            lock (_cacheGate)
            {
                _cache.RemoveAll(c => c.Key == key);
                _cache.Add(cached);
                if (_cache.Count > 4) _cache.RemoveAt(0);
            }
        }
        return FormatPage(cached.Final, cached.Page, cached.Cut, start, s.MaxChars);
    }

    internal static string FormatPage(Uri final, PageText page, bool cut, int start, int maxChars)
    {
        var sb = new StringBuilder();
        if (page.Title.Length > 0) sb.Append("Title: ").AppendLine(page.Title);
        sb.Append("URL: ").AppendLine(final.AbsoluteUri).AppendLine();

        var text = page.Text;
        if (text.Length == 0) return sb.Append("The page has no readable text (it may be built by JavaScript).").ToString();
        if (start >= text.Length) return sb.Append($"No more text: the page has {text.Length} characters.").ToString();

        var end = Math.Min(text.Length, start + maxChars);
        if (end < text.Length)
        {
            // Обрезаем по границе строки, если она недалеко
            var nl = text.LastIndexOf('\n', end - 1, Math.Min(400, end - start));
            if (nl > start) end = nl;
        }
        sb.Append(text, start, end - start);
        if (end < text.Length)
            sb.AppendLine().AppendLine().Append($"[Truncated: characters {start}-{end} of {text.Length}. Call web_fetch with start={end} to read on.]");
        else if (cut)
            sb.AppendLine().AppendLine().Append("[The page is larger than the download limit: its end is missing.]");
        return sb.ToString();
    }

    private static PageText ToPage(Download d, PageCleanOptions options) =>
        IsHtml(d) ? PageExtractor.Extract(d.Text, d.Final, options) : new PageText { Text = d.Text.Trim() };

    private static bool IsHtml(Download d) =>
        d.MediaType is "text/html" or "application/xhtml+xml"
        || (d.MediaType.Length == 0 && d.Text.TrimStart().StartsWith("<", StringComparison.Ordinal));

    private static string Signature(PageCleanOptions o) =>
        string.Concat(new[] { o.MainContentOnly, o.StripNavigation, o.StripBoilerplate, o.StripComments, o.DropDuplicateLines, o.KeepLinks, o.Markdown }
            .Select(b => b ? '1' : '0'));

    /// <summary>Адрес из аргумента модели: маленькие модели часто пишут его без схемы.</summary>
    internal static Uri ParseUrl(string url)
    {
        url = url.Trim().Trim('<', '>', '"', '\'');
        if (url.IndexOf("://", StringComparison.Ordinal) < 0 && url.IndexOf('.') > 0 && url.IndexOf(' ') < 0) url = "https://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var u) || u.Scheme is not ("http" or "https"))
            throw new ArgumentException("Only absolute http/https URLs are supported");
        return u;
    }

    private static int? ReadInt(JToken? t) =>
        t == null || t.Type == JTokenType.Null ? null
        : int.TryParse(t.ToString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : null;

    // ---------------- web_search ----------------

    public async Task<string> SearchAsync(JObject args, CancellationToken ct)
    {
        var query = ((string?)args["query"] ?? string.Empty).Trim();
        if (query.Length == 0) throw new ArgumentException("query is required");
        var s = S;
        var url = new Uri(SearchEngines.BuildUrl(s, _tools.SearxngUrl, query, (string?)args["period"]));

        // Адрес поиска задаёт пользователь (в том числе свой SearXNG на этом компьютере) — проверка локальных адресов не нужна
        List<SearchHit> hits;
        if (s.Engine == SearchEngine.Searxng)
        {
            var d = await DownloadAsync(url, "application/json", guard: false, ct).ConfigureAwait(false);
            try { hits = SerpParser.ParseSearxng(JToken.Parse(d.Text)); }
            catch (Newtonsoft.Json.JsonException) { throw new InvalidOperationException("SearXNG did not answer with JSON: enable the json format in its settings (search.formats)."); }
        }
        else
        {
            var d = await DownloadAsync(url, HtmlAccept, guard: false, ct).ConfigureAwait(false);
            hits = SerpParser.Parse(d.Text, d.Final);
            if (hits.Count == 0 && SerpParser.LooksBlocked(d.Text))
                throw new InvalidOperationException("The search engine answered with a robot check or needs JavaScript. The user can pick another search engine or user agent in the Tools settings.");
        }

        hits = hits.Where(h => !SerpParser.IsExcluded(h.Url, s.ExcludeDomains)).Take(s.MaxResults).ToList();
        if (hits.Count == 0) return "No results";

        var pages = new string?[hits.Count];
        if (s.ReadPages > 0)
        {
            var tasks = hits.Take(s.ReadPages).Select(h => ReadExcerptAsync(h.Url, s, ct)).ToArray();
            var texts = await Task.WhenAll(tasks).ConfigureAwait(false);
            Array.Copy(texts, pages, texts.Length);
        }
        return FormatHits(hits, pages, s.Snippets);
    }

    internal static string FormatHits(IReadOnlyList<SearchHit> hits, IReadOnlyList<string?> pages, bool snippets)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            if (i > 0) sb.AppendLine();
            sb.Append('[').Append(i + 1).Append("] ").AppendLine(h.Title);
            sb.AppendLine(h.Url);
            if (snippets && h.Snippet.Length > 0) sb.AppendLine(h.Snippet);
            if (i < pages.Count && !string.IsNullOrEmpty(pages[i])) sb.AppendLine("Page text:").AppendLine(pages[i]);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>Начало текста страницы из выдачи; страница, которую не удалось прочитать, просто пропускается.</summary>
    private async Task<string?> ReadExcerptAsync(string url, WebToolsSettings s, CancellationToken ct)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return null;
            var options = s.CleanOptions;
            options.KeepLinks = false;
            var d = await DownloadAsync(u, HtmlAccept, guard: true, ct).ConfigureAwait(false);
            var text = ToPage(d, options).Text;
            return text.Length > s.ReadPageChars ? text.Substring(0, s.ReadPageChars).TrimEnd() + "…" : text;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TimeoutException or NotSupportedException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    // ---------------- Загрузка ----------------

    private async Task<Download> DownloadAsync(Uri uri, string accept, bool guard, CancellationToken ct)
    {
        var s = S;
        if (guard && !s.AllowLocalAddresses && WebGuard.IsLocal(uri)) throw new UnauthorizedAccessException(LocalRefusal);

        var userAgent = UserAgents.Resolve(s.UserAgent, s.CustomUserAgent, _http.UserAgent);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(s.TimeoutSeconds));
        try
        {
            using var response = await _http.SendAsync(() =>
            {
                var r = new HttpRequestMessage(HttpMethod.Get, uri);
                r.Headers.TryAddWithoutValidation("User-Agent", userAgent);
                r.Headers.TryAddWithoutValidation("Accept", accept);
                if (!string.IsNullOrWhiteSpace(s.AcceptLanguage)) r.Headers.TryAddWithoutValidation("Accept-Language", s.AcceptLanguage!.Trim());
                return r;
            }, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            // Чтение тела на net48 отмену не слушает — по таймауту закрываем ответ
            using var abort = cts.Token.Register(response.Dispose);

            var final = response.RequestMessage?.RequestUri ?? uri;
            // Переадресация могла увести на локальный адрес: содержимое в этом случае модели не отдаём
            if (guard && !s.AllowLocalAddresses && WebGuard.IsLocal(final))
                throw new UnauthorizedAccessException("The address redirects to a local network address");
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)response.StatusCode} {response.ReasonPhrase} for {final.Host}");

            var type = response.Content.Headers.ContentType?.MediaType?.ToLowerInvariant() ?? string.Empty;
            if (!IsTextual(type)) throw new NotSupportedException($"Not a text page: {type}");

            var limit = s.MaxDownloadKb * 1024;
            var cut = false;
            using var buffer = new MemoryStream();
            using (var stream = await response.Content.ReadAsStreamAsync().ConfigureAwait(false))
            {
                var chunk = new byte[16384];
                int n;
                while ((n = await stream.ReadAsync(chunk, 0, chunk.Length, cts.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + n > limit)
                    {
                        buffer.Write(chunk, 0, (int)(limit - buffer.Length));
                        cut = true;
                        break;
                    }
                    buffer.Write(chunk, 0, n);
                }
            }
            var bytes = buffer.ToArray();
            return new Download(final, type, DetectEncoding(bytes, response.Content.Headers.ContentType?.CharSet).GetString(bytes).TrimStart('﻿'), cut);
        }
        catch (Exception ex) when (ex is OperationCanceledException or IOException or ObjectDisposedException or HttpRequestException
                                   && cts.IsCancellationRequested)
        {
            // Остановил пользователь — это отмена; иначе сработал наш таймаут
            ct.ThrowIfCancellationRequested();
            throw new TimeoutException($"No answer from {uri.Host} in {s.TimeoutSeconds} s");
        }
    }

    private static bool IsTextual(string type) =>
        type.Length == 0 || type.StartsWith("text/", StringComparison.Ordinal)
        || type is "application/xhtml+xml" or "application/xml" or "application/json" or "application/javascript" or "application/rss+xml" or "application/atom+xml"
        || type.EndsWith("+json", StringComparison.Ordinal) || type.EndsWith("+xml", StringComparison.Ordinal);

    private static readonly Regex MetaCharset = new(@"charset\s*=\s*[""']?\s*([A-Za-z0-9_\-]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>
    /// Кодировка страницы: заголовок ответа → BOM → meta charset в начале документа → UTF-8.
    /// Старые русские сайты отдают windows-1251 только в meta, без заголовка.
    /// </summary>
    internal static Encoding DetectEncoding(byte[] bytes, string? headerCharset)
    {
        if (TryEncoding(headerCharset) is { } fromHeader) return fromHeader;
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8;
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode;
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode;
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var m = MetaCharset.Match(head);
        return (m.Success ? TryEncoding(m.Groups[1].Value) : null) ?? Encoding.UTF8;
    }

    private static Encoding? TryEncoding(string? name)
    {
        name = name?.Trim().Trim('"', '\'');
        if (string.IsNullOrEmpty(name)) return null;
        try { return Encoding.GetEncoding(name); }
        catch (ArgumentException) { return null; }
    }
}
