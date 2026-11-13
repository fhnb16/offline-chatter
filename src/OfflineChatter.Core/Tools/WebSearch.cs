using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json.Linq;

namespace OfflineChatter.Core.Tools;

public sealed record SearchHit(string Title, string Url, string Snippet);

/// <summary>Адреса выдачи поисковых систем: готовые шаблоны, SearXNG и свой адрес.</summary>
public static class SearchEngines
{
    public const string QueryMark = "{query}";

    public static IReadOnlyList<SearchEngine> All { get; } = Enum.GetValues(typeof(SearchEngine)).Cast<SearchEngine>().ToList();

    /// <summary>Шаблон адреса выдачи (для показа в настройках). У SearXNG и своего адреса шаблона нет.</summary>
    public static string Template(SearchEngine engine) => engine switch
    {
        SearchEngine.DuckDuckGo => "https://html.duckduckgo.com/html/?q={query}",
        SearchEngine.Bing => "https://www.bing.com/search?q={query}",
        SearchEngine.Google => "https://www.google.com/search?q={query}&gbv=1",
        SearchEngine.Yandex => "https://yandex.ru/search/?text={query}",
        SearchEngine.Brave => "https://search.brave.com/search?q={query}&source=web",
        SearchEngine.Mojeek => "https://www.mojeek.com/search?q={query}",
        SearchEngine.Startpage => "https://www.startpage.com/do/search?q={query}",
        SearchEngine.Yahoo => "https://search.yahoo.com/search?p={query}",
        _ => string.Empty,
    };

    /// <summary>Период из аргумента модели: day / week / month / year (и сокращения); остальное — без ограничения.</summary>
    public static string? NormalizePeriod(string? period) => (period ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "d" or "day" or "today" or "24h" => "day",
        "w" or "week" => "week",
        "m" or "month" => "month",
        "y" or "year" => "year",
        _ => null,
    };

    /// <summary>
    /// Адрес запроса. Регион, безопасный поиск и период добавляются теми параметрами, которые система понимает;
    /// в свой адрес ничего, кроме запроса, не дописывается.
    /// </summary>
    /// <exception cref="InvalidOperationException">Не задан адрес SearXNG или свой адрес.</exception>
    public static string BuildUrl(WebToolsSettings s, string? searxngUrl, string query, string? period)
    {
        var q = Uri.EscapeDataString(query);
        period = NormalizePeriod(period);
        var (country, lang) = SplitRegion(s.Region);
        var sb = new StringBuilder();

        switch (s.Engine)
        {
            case SearchEngine.Custom:
                var template = (s.CustomSearchUrl ?? string.Empty).Trim();
                if (template.Length == 0) throw new InvalidOperationException("The custom search address is not set (Tools settings).");
                if (template.IndexOf("://", StringComparison.Ordinal) < 0) template = "https://" + template;
                if (template.Contains(QueryMark)) return template.Replace(QueryMark, q);
                return template.Contains("%s") ? template.Replace("%s", q) : template + q;

            case SearchEngine.Searxng:
                if (string.IsNullOrWhiteSpace(searxngUrl)) throw new InvalidOperationException("The SearXNG address is not set (Tools settings).");
                sb.Append(searxngUrl!.Trim().TrimEnd('/')).Append("/search?format=json&q=").Append(q);
                if (lang != null) sb.Append("&language=").Append(lang).Append(country != null ? "-" + country.ToUpperInvariant() : string.Empty);
                sb.Append("&safesearch=").Append((int)s.SafeSearch);
                if (period != null) sb.Append("&time_range=").Append(period);
                return sb.ToString();
        }

        sb.Append(Template(s.Engine).Replace(QueryMark, q));
        switch (s.Engine)
        {
            case SearchEngine.DuckDuckGo:
                if (country != null && lang != null) sb.Append("&kl=").Append(country).Append('-').Append(lang);
                sb.Append("&kp=").Append(s.SafeSearch switch { SafeSearchLevel.Strict => "1", SafeSearchLevel.Off => "-2", _ => "-1" });
                if (period != null) sb.Append("&df=").Append(period[0]);
                break;
            case SearchEngine.Bing:
                if (country != null && lang != null) sb.Append("&mkt=").Append(lang).Append('-').Append(country.ToUpperInvariant());
                sb.Append("&adlt=").Append(s.SafeSearch.ToString().ToLowerInvariant());
                break;
            case SearchEngine.Google:
                if (lang != null) sb.Append("&hl=").Append(lang);
                if (country != null) sb.Append("&gl=").Append(country);
                sb.Append("&safe=").Append(s.SafeSearch == SafeSearchLevel.Off ? "off" : "active");
                if (period != null) sb.Append("&tbs=qdr:").Append(period[0]);
                break;
            case SearchEngine.Brave:
                sb.Append("&safesearch=").Append(s.SafeSearch.ToString().ToLowerInvariant());
                if (period != null) sb.Append("&tf=p").Append(period[0]);
                break;
            case SearchEngine.Mojeek:
                sb.Append("&safe=").Append(s.SafeSearch == SafeSearchLevel.Off ? "0" : "1");
                break;
            case SearchEngine.Startpage:
                if (period != null) sb.Append("&with_date=").Append(period[0]);
                break;
        }
        return sb.ToString();
    }

    /// <summary>"ru-ru" → (страна, язык). Одно слово считается и страной, и языком; мусор — без региона.</summary>
    internal static (string? country, string? lang) SplitRegion(string? region)
    {
        var parts = (region ?? string.Empty).Trim().ToLowerInvariant().Split(new[] { '-', '_' }, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(p => p.Length is < 2 or > 3 || !p.All(c => c is >= 'a' and <= 'z'))) return (null, null);
        return parts.Length == 1 ? (parts[0], parts[0]) : (parts[0], parts[1]);
    }
}

/// <summary>
/// Результаты из страницы выдачи любой поисковой системы. Общий разбор вместо отдельного под каждую вёрстку:
/// результат — это ссылка на другой сайт в заголовке (h1–h4), описание — текст ближайшего блока,
/// в котором других результатов нет. Обёртки-переадресации (DuckDuckGo, Google, Bing, Yahoo) раскрываются.
/// </summary>
public static class SerpParser
{
    public static List<SearchHit> Parse(string html, Uri page)
    {
        var root = HtmlParser.Parse(html);
        var body = root.Descendants().FirstOrDefault(x => x.Tag == "body") ?? root;
        var engine = SiteName(page.Host);

        var links = new List<(HtmlNode Node, string Url, bool Heading)>();
        foreach (var a in body.Descendants().Where(x => x.Tag == "a" && x.HasAttr("href")))
        {
            if (!Uri.TryCreate(page, a.Attr("href").Trim(), out var u) || u.Scheme is not ("http" or "https")) continue;
            if (SiteName(u.Host) == engine)
            {
                // Своя ссылка системы: либо обёртка над результатом, либо навигация по выдаче
                var target = Unwrap(u);
                if (target == null || !Uri.TryCreate(target, UriKind.Absolute, out u) || u.Scheme is not ("http" or "https") || SiteName(u.Host) == engine)
                    continue;
            }
            if (a.HasAncestor(x => x.Tag is "nav" or "footer" or "noscript")) continue;
            links.Add((a, u.AbsoluteUri, a.HasAncestor(IsHeading) || a.Descendants().Any(IsHeading)));
        }

        // Заголовков хватает — берём только их; иначе (вёрстка без h2/h3) все ссылки с осмысленным текстом
        var picked = links.Count(l => l.Heading) >= 3
            ? links.Where(l => l.Heading).ToList()
            : links.Where(l => l.Heading || PageExtractor.Collapse(TextOf(l.Node, null)).Length >= 15).ToList();
        // Один адрес — один результат: на него же ведут значок, строка с адресом и описание. Главная ссылка — та, что в заголовке
        picked = picked.Where(l => HasText(l.Node))
            .GroupBy(l => l.Url.TrimEnd('/'), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(l => l.Heading).First())
            .ToList();
        var nodes = new HashSet<HtmlNode>(picked.Select(l => l.Node));

        var hits = new List<SearchHit>();
        foreach (var (node, url, _) in picked)
        {
            var heading = node.Descendants().FirstOrDefault(IsHeading);
            var title = PageExtractor.Collapse(TextOf(heading ?? node, null));
            if (title.Length == 0) continue;

            // Блок результата: поднимаемся, пока в нём нет другого результата
            var box = node;
            while (box.Parent is { } p && p.Tag is not ("body" or "html" or "#root") && p.Descendants().Count(nodes.Contains) == 1) box = p;
            var titleRoot = Ancestor(node, IsHeading) ?? node;
            var snippet = ReferenceEquals(box, node) ? string.Empty : PageExtractor.Collapse(TextOf(box, titleRoot));
            if (snippet.Length > 320) snippet = snippet.Substring(0, 320).TrimEnd() + "…";
            hits.Add(new SearchHit(title, url, snippet));
        }
        return hits;
    }

    public static List<SearchHit> ParseSearxng(JToken json)
    {
        var hits = new List<SearchHit>();
        foreach (var r in json["results"] as JArray ?? new JArray())
        {
            var url = (string?)r["url"];
            if (string.IsNullOrWhiteSpace(url)) continue;
            hits.Add(new SearchHit(PageExtractor.Collapse((string?)r["title"]), url!, PageExtractor.Collapse((string?)r["content"])));
        }
        return hits;
    }

    /// <summary>Вместо выдачи пришла проверка «я не робот» или страница, которой нужен JavaScript.</summary>
    public static bool LooksBlocked(string html)
    {
        var head = (html.Length > 60000 ? html.Substring(0, 60000) : html).ToLowerInvariant();
        return Markers.Any(head.Contains);
    }

    private static readonly string[] Markers =
    {
        "captcha", "unusual traffic", "not a robot", "are you a robot", "я не робот", "вы не робот", "enable javascript",
        "anomaly-modal", "verify you are human", "automated queries", "automated requests",
        "enablejs", "/httpservice/retry/", "anubis", "verifying your request", "checking your browser", "cf-challenge",
    };

    public static bool IsExcluded(string url, IReadOnlyCollection<string> domains)
    {
        if (domains.Count == 0 || !Uri.TryCreate(url, UriKind.Absolute, out var u)) return false;
        var host = u.Host.ToLowerInvariant();
        return domains.Any(d => host == d || host.EndsWith("." + d, StringComparison.Ordinal));
    }

    // ---------------- Вспомогательное ----------------

    private static bool IsHeading(HtmlNode x) => x.Tag is "h1" or "h2" or "h3" or "h4";

    private static bool HasText(HtmlNode x) => PageExtractor.Collapse(TextOf(x, null)).Length > 0;

    private static HtmlNode? Ancestor(HtmlNode node, Func<HtmlNode, bool> match)
    {
        for (var p = node.Parent; p != null; p = p.Parent)
            if (match(p)) return p;
        return null;
    }

    /// <summary>Текст блока без заголовка результата, без адреса сайта (cite, ссылка-адрес) и без полей форм.</summary>
    private static string TextOf(HtmlNode node, HtmlNode? skip)
    {
        var sb = new StringBuilder();
        void Walk(HtmlNode x)
        {
            if (ReferenceEquals(x, skip)) return;
            if (x.IsText)
            {
                sb.Append(x.Text);
                return;
            }
            if (x.Tag is "cite" or "button" or "select" or "input" or "noscript" or "svg") return;
            if (skip != null && x.Tag == "a")
            {
                var label = PageExtractor.Collapse(TextOf(x, null));
                if (label.IndexOf(' ') < 0 && (label.IndexOf('.') >= 0 || label.IndexOf('/') >= 0)) return;
            }
            // Выделение внутри фразы (<b>слово</b>.) пробелов не добавляет; блоки отделяются друг от друга
            var inline = Inline.Contains(x.Tag);
            if (!inline) sb.Append(' ');
            foreach (var c in x.Children) Walk(c);
            if (!inline) sb.Append(' ');
        }
        Walk(node);
        return sb.ToString();
    }

    private static readonly HashSet<string> Inline = new(StringComparer.Ordinal)
    {
        "b", "strong", "i", "em", "u", "mark", "span", "a", "small", "sub", "sup", "code", "font", "wbr", "abbr",
    };

    /// <summary>Имя сайта без поддоменов и зоны: www.google.co.uk → google, html.duckduckgo.com → duckduckgo.</summary>
    internal static string SiteName(string host)
    {
        var labels = host.ToLowerInvariant().Split('.');
        if (labels.Length < 2 || System.Net.IPAddress.TryParse(host, out _)) return host.ToLowerInvariant();
        var i = labels.Length - 2;
        // Двухуровневые зоны: co.uk, com.tr
        if (i > 0 && labels[labels.Length - 1].Length == 2 && labels[i] is "co" or "com" or "org" or "net") i--;
        return labels[i];
    }

    /// <summary>Настоящий адрес из ссылки-обёртки поисковой системы; null — это не обёртка.</summary>
    internal static string? Unwrap(Uri link)
    {
        var full = link.AbsoluteUri;
        // Yahoo: …/RU=https%3a%2f%2fsite%2f/RK=2/RS=…
        var ru = full.IndexOf("/RU=", StringComparison.Ordinal);
        if (ru >= 0)
        {
            var end = full.IndexOf("/RK=", ru, StringComparison.Ordinal);
            var value = Decode(end > ru ? full.Substring(ru + 4, end - ru - 4) : full.Substring(ru + 4));
            if (IsHttp(value)) return value;
        }

        foreach (var pair in link.Query.TrimStart('?').Split(new[] { '&' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            var name = pair.Substring(0, eq);
            if (name is not ("uddg" or "q" or "url" or "u" or "target" or "redirect")) continue;
            var value = Decode(pair.Substring(eq + 1));
            if (IsHttp(value)) return value;
            // Bing: u=a1<адрес в base64url>
            if (name == "u" && value.StartsWith("a1", StringComparison.Ordinal) && FromBase64Url(value.Substring(2)) is { } decoded && IsHttp(decoded))
                return decoded;
        }
        return null;
    }

    private static bool IsHttp(string s) =>
        s.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    private static string Decode(string s)
    {
        try { return Uri.UnescapeDataString(s.Replace('+', ' ')); }
        catch (UriFormatException) { return s; }
    }

    private static string? FromBase64Url(string s)
    {
        try
        {
            s = s.Replace('-', '+').Replace('_', '/');
            s = s.PadRight(s.Length + (4 - s.Length % 4) % 4, '=');
            return Encoding.UTF8.GetString(Convert.FromBase64String(s));
        }
        catch (FormatException)
        {
            return null;
        }
    }
}
