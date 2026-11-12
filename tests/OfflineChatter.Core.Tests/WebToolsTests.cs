using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using OfflineChatter.Core.Tools;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class PageExtractorTests
{
    private const string Page = @"<!DOCTYPE html>
<html><head><title>How llamas  sleep — Farm blog</title><style>nav{color:red}</style>
<script>var x = '</div><nav>fake</nav>';</script></head>
<body class=""home page"">
  <header class=""site-header""><a href=""/"">Farm blog</a>
    <nav><ul><li><a href=""/about"">About the farm</a><li><a href=""/shop"">Shop for wool</a></ul></nav>
  </header>
  <div id=""cookie-banner"">We use cookies to improve your experience</div>
  <div class=""layout"">
    <aside class=""sidebar""><h3>Popular</h3><a href=""/p/1"">Ten facts about alpacas</a></aside>
    <main>
      <article>
        <header><h1>How llamas sleep</h1><p class=""meta"">By Ann, 3 May</p></header>
        <p>Llamas sleep <b>lying down</b>, usually at night &amp; in short naps.
           They keep their <a href=""/wiki/neck"">necks</a> upright.</p>
        <h2>Why it matters</h2>
        <ul><li>Rest keeps the herd calm<li>Less stress means better wool</ul>
        <ol><li>Feed</li><li>Water</li></ol>
        <pre>line 1
  line 2</pre>
        <div class=""share-buttons""><button>Share</button> Tell your friends about this post</div>
        <div style=""display: none"">Hidden text for robots</div>
        <table><tr><th>Age</th><th>Hours</th></tr><tr><td>Young</td><td>8</td></tr></table>
        <section id=""comments""><h3>Comments</h3><p>First!!! Great article about llamas</p></section>
      </article>
    </main>
  </div>
  <footer><p>© Farm blog. All rights reserved worldwide.</p></footer>
</body></html>";

    private static readonly Uri Base = new("https://farm.example/blog/sleep");

    [Fact]
    public void Keeps_Article_And_Drops_Chrome()
    {
        var p = PageExtractor.Extract(Page, Base, new PageCleanOptions());

        Assert.Equal("How llamas sleep — Farm blog", p.Title);
        Assert.Equal(string.Join("\n",
            "# How llamas sleep",
            "By Ann, 3 May",
            "Llamas sleep lying down, usually at night & in short naps. They keep their necks upright.",
            "",
            "## Why it matters",
            "- Rest keeps the herd calm",
            "- Less stress means better wool",
            "1. Feed",
            "2. Water",
            "```",
            "line 1",
            "  line 2",
            "```",
            "Age | Hours",
            "Young | 8"), p.Text);
    }

    [Fact]
    public void Every_Rule_Can_Be_Turned_Off()
    {
        var raw = PageExtractor.Extract(Page, Base, PageCleanOptions.Raw).Text;
        foreach (var kept in new[] { "About the farm", "We use cookies", "Ten facts about alpacas", "Tell your friends", "Hidden text for robots", "First!!!", "All rights reserved" })
            Assert.Contains(kept, raw);
        Assert.DoesNotContain("#", raw);          // без разметки
        Assert.DoesNotContain("fake", raw);       // содержимое script — не текст
        Assert.DoesNotContain("color:red", raw);

        // По одному правилу
        var noNav = PageExtractor.Extract(Page, Base, With(o => o.StripNavigation = true)).Text;
        Assert.DoesNotContain("About the farm", noNav);
        Assert.DoesNotContain("Ten facts about alpacas", noNav);
        Assert.DoesNotContain("All rights reserved", noNav);
        Assert.Contains("We use cookies", noNav);
        Assert.Contains("How llamas sleep", noNav); // <header> статьи — не шапка сайта

        var noJunk = PageExtractor.Extract(Page, Base, With(o => o.StripBoilerplate = true)).Text;
        Assert.DoesNotContain("We use cookies", noJunk);
        Assert.DoesNotContain("Tell your friends", noJunk);
        Assert.DoesNotContain("Hidden text for robots", noJunk);
        Assert.Contains("First!!!", noJunk);

        var noComments = PageExtractor.Extract(Page, Base, With(o => o.StripComments = true)).Text;
        Assert.DoesNotContain("First!!!", noComments);
        Assert.Contains("About the farm", noComments);

        var mainOnly = PageExtractor.Extract(Page, Base, With(o => o.MainContentOnly = true)).Text;
        Assert.DoesNotContain("About the farm", mainOnly);
        Assert.Contains("First!!!", mainOnly); // комментарии внутри статьи остаются, пока не выключены отдельно
    }

    private static PageCleanOptions With(Action<PageCleanOptions> set)
    {
        var o = PageCleanOptions.Raw;
        set(o);
        return o;
    }

    [Fact]
    public void Links_Are_Kept_On_Request_And_Made_Absolute()
    {
        var o = new PageCleanOptions { KeepLinks = true };
        var text = PageExtractor.Extract(Page, Base, o).Text;
        Assert.Contains("They keep their [necks](https://farm.example/wiki/neck) upright.", text);

        var html = "<p>See <a href='#top'>top</a>, <a href='javascript:void(0)'>menu</a> and <a href='//cdn.example/x'>file</a></p>";
        Assert.Equal("See top, menu and [file](https://cdn.example/x)", PageExtractor.Extract(html, Base, o).Text);
    }

    [Fact]
    public void Repeated_Lines_Are_Dropped_But_Short_Ones_Stay()
    {
        var html = "<p>Subscribe to our weekly letter</p><p>Yes</p><p>Body text of the page goes here</p><p>Yes</p><p>Subscribe to our weekly letter</p>";
        Assert.Equal("Subscribe to our weekly letter\nYes\nBody text of the page goes here\nYes",
            PageExtractor.Extract(html, null, new PageCleanOptions()).Text);
        Assert.EndsWith("Yes\nSubscribe to our weekly letter",
            PageExtractor.Extract(html, null, new PageCleanOptions { DropDuplicateLines = false }).Text);
    }

    [Fact]
    public void Class_Names_Do_Not_Remove_The_Content_Itself()
    {
        // Вся страница лежит в блоке с «меню» в имени класса: это содержимое, а не навигация
        var body = string.Concat(Enumerable.Repeat("<p>Soup of the day with fresh bread and butter.</p>", 3).Select((p, i) => p.Replace("Soup", "Soup " + i)));
        var html = "<body><div class='menu-wrapper'>" + body + "</div><div class='footer-links'><a href='/a'>Contacts and delivery terms</a></div></body>";
        var text = PageExtractor.Extract(html, null, new PageCleanOptions()).Text;
        Assert.Contains("Soup 0 of the day", text);
        Assert.DoesNotContain("Contacts", text);

        // Блок с заголовком h1 не удаляется, даже если назван «banner»
        var hero = "<body><div class='hero-banner'><h1>Product name</h1></div><p>Long enough description of the product for the page.</p></body>";
        Assert.StartsWith("# Product name", PageExtractor.Extract(hero, null, new PageCleanOptions()).Text);
    }

    [Fact]
    public void Broken_Markup_Is_Tolerated()
    {
        var html = "<div><p>One<p>Two<br>Three</div><li>Lost item</li><p>a < b &amp;&amp; c > d</p><!-- <p>comment</p> --><p>Tail";
        Assert.Equal("One\nTwo\nThree\nLost item\na < b && c > d\nTail", PageExtractor.Extract(html, null, PageCleanOptions.Raw).Text);

        // Очень глубокая вложенность не роняет разбор
        var deep = string.Concat(Enumerable.Repeat("<div>", 5000)) + "bottom";
        Assert.Equal("bottom", PageExtractor.Extract(deep, null, new PageCleanOptions()).Text);
        Assert.Equal(string.Empty, PageExtractor.Extract(string.Empty, null, new PageCleanOptions()).Text);
    }

    [Fact]
    public void Token_Split()
    {
        var root = HtmlParser.Parse("<div class='site-footer mainNav' id='cookie_banner'></div>");
        Assert.Equal(new[] { "site", "footer", "main", "nav", "cookie", "banner" }, PageExtractor.Tokens(root.Children[0]));
    }
}

public class SearchParsingTests
{
    private const string DuckDuckGo = @"<html><body><div id=""links"" class=""results"">
<div class=""result results_links results_links_deep result--ad"">
  <div class=""links_main links_deep result__body"">
    <h2 class=""result__title""><a rel=""nofollow"" class=""result__a"" href=""https://duckduckgo.com/y.js?ad_domain=shop.example&amp;ad_provider=bingv7aa&amp;u3=https%3A%2F%2Fwww.bing.com%2Faclick"">Buy llama wool online</a></h2>
    <a class=""result__snippet"" href=""https://duckduckgo.com/y.js?ad_domain=shop.example"">Best prices for wool</a>
  </div>
</div>
<div class=""result results_links results_links_deep web-result"">
  <div class=""links_main links_deep result__body"">
    <h2 class=""result__title""><a rel=""nofollow"" class=""result__a"" href=""//duckduckgo.com/l/?uddg=https%3A%2F%2Fen.wikipedia.org%2Fwiki%2FLlama&amp;rut=abc"">Llama - Wikipedia</a></h2>
    <div class=""result__extras""><div class=""result__extras__url"">
      <span class=""result__icon""><a rel=""nofollow"" href=""//duckduckgo.com/l/?uddg=https%3A%2F%2Fen.wikipedia.org%2Fwiki%2FLlama&amp;rut=abc""><img class=""result__icon__img"" src=""//external-content.duckduckgo.com/ip3/en.wikipedia.org.ico""></a></span>
      <a class=""result__url"" href=""//duckduckgo.com/l/?uddg=https%3A%2F%2Fen.wikipedia.org%2Fwiki%2FLlama&amp;rut=abc""> en.wikipedia.org/wiki/Llama </a>
    </div></div>
    <a class=""result__snippet"" href=""//duckduckgo.com/l/?uddg=https%3A%2F%2Fen.wikipedia.org%2Fwiki%2FLlama&amp;rut=abc"">The <b>llama</b> is a domesticated South American camelid.</a>
  </div>
</div>
<div class=""result results_links results_links_deep web-result"">
  <div class=""links_main links_deep result__body"">
    <h2 class=""result__title""><a rel=""nofollow"" class=""result__a"" href=""//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.britannica.com%2Fanimal%2Fllama%3Fa%3D1%26b%3D2&amp;rut=def"">Llama | Description &amp; Facts</a></h2>
    <a class=""result__snippet"" href=""//duckduckgo.com/l/?uddg=https%3A%2F%2Fwww.britannica.com%2Fanimal%2Fllama%3Fa%3D1%26b%3D2&amp;rut=def"">Llama, South American member of the camel family.</a>
  </div>
</div>
<div class=""result results_links results_links_deep web-result"">
  <div class=""links_main links_deep result__body"">
    <h2 class=""result__title""><a rel=""nofollow"" class=""result__a"" href=""//duckduckgo.com/l/?uddg=https%3A%2F%2Fllama.example%2F&amp;rut=ghi"">Llama farm</a></h2>
    <a class=""result__snippet"" href=""//duckduckgo.com/l/?uddg=https%3A%2F%2Fllama.example%2F&amp;rut=ghi"">Visit our farm.</a>
  </div>
</div>
<div class=""nav-link""><form action=""/html/"" method=""post""><input type=""submit"" value=""Next""></form></div>
</div><a href=""/html/?q=llama+care"">llama care</a></body></html>";

    [Fact]
    public void DuckDuckGo_Results_Without_Ads()
    {
        var hits = SerpParser.Parse(DuckDuckGo, new Uri("https://html.duckduckgo.com/html/?q=llama"));

        Assert.Equal(new[]
        {
            new SearchHit("Llama - Wikipedia", "https://en.wikipedia.org/wiki/Llama", "The llama is a domesticated South American camelid."),
            new SearchHit("Llama | Description & Facts", "https://www.britannica.com/animal/llama?a=1&b=2", "Llama, South American member of the camel family."),
            new SearchHit("Llama farm", "https://llama.example/", "Visit our farm."),
        }, hits);
    }

    [Fact]
    public void Bing_Google_And_Yahoo_Wrappers()
    {
        var target = Convert.ToBase64String(Encoding.UTF8.GetBytes("https://learn.example/docs?x=1")).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var bing = $@"<body><ol id='b_results'>
<li class='b_algo'><div class='b_tpcn'><a class='tilk' href='https://www.bing.com/ck/a?!&&p=1&u=a1{target}&ntb=1'><div class='tptt'>Learn</div><cite>https://learn.example</cite></a></div>
  <h2><a href='https://www.bing.com/ck/a?!&&p=1&u=a1{target}&ntb=1'>Docs for beginners</a></h2><div class='b_caption'><p>Start here to learn the basics.</p></div></li>
<li class='b_algo'><h2><a href='https://plain.example/a'>Plain link</a></h2><div class='b_caption'><p>Second text.</p></div></li>
<li class='b_algo'><h2><a href='https://third.example/'>Third</a></h2><p>Third text.</p></li>
</ol><footer><a href='https://go.microsoft.com/fwlink'>Privacy and cookies policy</a></footer></body>";
        var hits = SerpParser.Parse(bing, new Uri("https://www.bing.com/search?q=docs"));
        Assert.Equal(3, hits.Count);
        Assert.Equal(new SearchHit("Docs for beginners", "https://learn.example/docs?x=1", "Learn Start here to learn the basics."), hits[0]);
        Assert.Equal("https://plain.example/a", hits[1].Url);

        // Google: заголовок h3 внутри ссылки, адрес — в /url?q=
        var google = @"<body><div><div><a href='/url?q=https://a.example/page&amp;sa=U&amp;ved=1'><div><h3><div>First result</div></h3><div>a.example › page</div></div></a>
<div><div>Text of the first result.</div></div></div>
<div><a href='/url?q=https://b.example/&amp;sa=U'><h3>Second result</h3></a><div>Text two.</div></div>
<div><a href='/url?q=https://c.example/&amp;sa=U'><h3>Third result</h3></a><div>Text three.</div></div>
<a href='/search?q=more&amp;start=10'>Next page of the results</a><a href='https://support.google.com/websearch'>Learn more about search</a></div></body>";
        hits = SerpParser.Parse(google, new Uri("https://www.google.com/search?q=x"));
        Assert.Equal(new[] { "https://a.example/page", "https://b.example/", "https://c.example/" }, hits.Select(h => h.Url));
        Assert.Equal("First result", hits[0].Title);
        Assert.Equal("Text of the first result.", hits[0].Snippet);

        Assert.Equal("https://site.example/path", SerpParser.Unwrap(new Uri("https://r.search.yahoo.com/_ylt=Awr/RV=2/RE=1/RO=10/RU=https%3a%2f%2fsite.example%2fpath/RK=2/RS=abc-")));
        Assert.Null(SerpParser.Unwrap(new Uri("https://duckduckgo.com/html/?q=llama+care")));
    }

    [Fact]
    public void Page_Without_Headings_Falls_Back_To_Links()
    {
        var html = @"<body><div class='r'><a href='https://one.example/'>The first site about llamas</a><span>Text one.</span></div>
<div class='r'><a href='https://two.example/'>Another site about llamas</a><span>Text two.</span></div>
<a href='https://tiny.example/'>ok</a></body>";
        var hits = SerpParser.Parse(html, new Uri("https://search.example/?q=x"));
        Assert.Equal(2, hits.Count);
        Assert.Equal(new SearchHit("Another site about llamas", "https://two.example/", "Text two."), hits[1]);
    }

    [Fact]
    public void Robot_Check_And_Excluded_Domains()
    {
        Assert.True(SerpParser.LooksBlocked("<html><body><form id='captcha-form'>Our systems have detected unusual traffic</form></body></html>"));
        Assert.False(SerpParser.LooksBlocked(DuckDuckGo));

        var domains = WebToolsSettings.ParseDomains("Pinterest.com, https://www.quora.com/topic\nvk.com");
        Assert.Equal(new[] { "pinterest.com", "quora.com", "vk.com" }, domains);
        Assert.True(SerpParser.IsExcluded("https://ru.pinterest.com/pin/1", domains));
        Assert.True(SerpParser.IsExcluded("https://quora.com/q", domains));
        Assert.False(SerpParser.IsExcluded("https://notpinterest.com/", domains));
    }

    [Fact]
    public void Search_Urls()
    {
        var s = new WebToolsSettings();
        // По умолчанию — HTML-выдача DuckDuckGo
        Assert.Equal("https://html.duckduckgo.com/html/?q=llama%20%D0%B5%D0%B4%D0%B0&kp=-1", SearchEngines.BuildUrl(s, null, "llama еда", null));

        s.Region = "ru-ru";
        s.SafeSearch = SafeSearchLevel.Strict;
        Assert.Equal("https://html.duckduckgo.com/html/?q=a&kl=ru-ru&kp=1&df=w", SearchEngines.BuildUrl(s, null, "a", "week"));

        s.Engine = SearchEngine.Google;
        Assert.Equal("https://www.google.com/search?q=a&gbv=1&hl=ru&gl=ru&safe=active&tbs=qdr:d", SearchEngines.BuildUrl(s, null, "a", "day"));
        s.Engine = SearchEngine.Bing;
        Assert.Equal("https://www.bing.com/search?q=a&mkt=ru-RU&adlt=strict", SearchEngines.BuildUrl(s, null, "a", "nonsense"));
        s.Engine = SearchEngine.Yandex;
        Assert.Equal("https://yandex.ru/search/?text=a%26b", SearchEngines.BuildUrl(s, null, "a&b", null));

        s.Engine = SearchEngine.Searxng;
        Assert.Throws<InvalidOperationException>(() => SearchEngines.BuildUrl(s, " ", "a", null));
        Assert.Equal("http://127.0.0.1:8888/search?format=json&q=a&language=ru-RU&safesearch=2&time_range=month", SearchEngines.BuildUrl(s, "http://127.0.0.1:8888/", "a", "month"));

        // Свой адрес: метка {query}, %s или просто хвост; регион и прочее не дописываются
        s.Engine = SearchEngine.Custom;
        Assert.Throws<InvalidOperationException>(() => SearchEngines.BuildUrl(s, null, "a", null));
        s.CustomSearchUrl = "html.duckduckgo.com/html?q=";
        Assert.Equal("https://html.duckduckgo.com/html?q=a%20b", SearchEngines.BuildUrl(s, null, "a b", "day"));
        s.CustomSearchUrl = "https://find.example/?text={query}&lang=ru";
        Assert.Equal("https://find.example/?text=a%20b&lang=ru", SearchEngines.BuildUrl(s, null, "a b", null));
        s.CustomSearchUrl = "https://find.example/s/%s/";
        Assert.Equal("https://find.example/s/a/", SearchEngines.BuildUrl(s, null, "a", null));

        // У каждой готовой системы есть шаблон с меткой запроса
        foreach (var e in SearchEngines.All.Where(e => e is not (SearchEngine.Searxng or SearchEngine.Custom)))
            Assert.Contains(SearchEngines.QueryMark, SearchEngines.Template(e));
    }
}

public class WebToolsTests
{
    /// <summary>Мини-сайт на TcpListener: отдаёт заданные страницы и запоминает заголовки запросов.</summary>
    private sealed class Site : IDisposable
    {
        private readonly TcpListener _l;
        public readonly Dictionary<string, (string Type, byte[] Body)> Pages = new();
        public readonly List<string> Requests = new();
        public bool Silent;

        public Site(string address = "127.0.0.1")
        {
            _l = new TcpListener(IPAddress.Parse(address), 0);
            _l.Start();
            Url = $"http://{address}:{((IPEndPoint)_l.LocalEndpoint).Port}";
            _ = Task.Run(Loop);
        }

        public string Url { get; }

        public void Add(string path, string body, string type = "text/html; charset=utf-8") => Pages[path] = (type, Encoding.UTF8.GetBytes(body));

        private async Task Loop()
        {
            while (true)
            {
                TcpClient c;
                try { c = await _l.AcceptTcpClientAsync(); }
                catch (Exception ex) when (ex is ObjectDisposedException or SocketException or InvalidOperationException) { return; }
                _ = Task.Run(() => Serve(c));
            }
        }

        private async Task Serve(TcpClient c)
        {
            try
            {
                using (c)
                {
                    var s = c.GetStream();
                    var head = new StringBuilder();
                    var buf = new byte[4096];
                    while (head.ToString().IndexOf("\r\n\r\n", StringComparison.Ordinal) < 0)
                    {
                        var n = await s.ReadAsync(buf, 0, buf.Length);
                        if (n <= 0) return;
                        head.Append(Encoding.ASCII.GetString(buf, 0, n));
                    }
                    lock (Requests) Requests.Add(head.ToString());
                    if (Silent)
                    {
                        await Task.Delay(15000);
                        return;
                    }
                    var path = head.ToString().Split(' ')[1].Split('?')[0];
                    var found = Pages.TryGetValue(path, out var page);
                    var body = found ? page.Body : Encoding.ASCII.GetBytes("not found");
                    var header = $"HTTP/1.1 {(found ? "200 OK" : "404 Not Found")}\r\nContent-Type: {(found ? page.Type : "text/plain")}\r\nContent-Length: {body.Length}\r\nConnection: close\r\n\r\n";
                    var hb = Encoding.ASCII.GetBytes(header);
                    await s.WriteAsync(hb, 0, hb.Length);
                    await s.WriteAsync(body, 0, body.Length);
                }
            }
            catch (Exception ex) when (ex is System.IO.IOException or ObjectDisposedException or SocketException)
            {
                // клиент закрыл соединение
            }
        }

        public void Dispose() => _l.Stop();
    }

    private static (ToolRegistry reg, HttpService http, ToolsSettings settings, NetworkSettings net) Registry()
    {
        var net = new NetworkSettings { ProxyMode = ProxyMode.None };
        var settings = new ToolsSettings();
        settings.Web.AllowLocalAddresses = true;
        var http = new HttpService(net, new TrustedRoots(), "9.9");
        return (new ToolRegistry(settings, http), http, settings, net);
    }

    private static Task<(string text, bool isError)> Call(ToolRegistry reg, string tool, object args) =>
        reg.ExecuteAsync(reg.Find(tool)!, JObject.FromObject(args).ToString(), CancellationToken.None);

    [Fact]
    public async Task Fetch_Returns_Clean_Text_With_Chosen_Headers()
    {
        using var site = new Site();
        site.Add("/post", "<html><head><title>Post</title></head><body><nav><a href='/'>Home page of the site</a></nav><article><h1>Hello</h1><p>Body of the post.</p></article><footer>Footer text of the site</footer></body></html>");
        var (reg, http, settings, _) = Registry();
        using var _h = http;
        using var _r = reg;
        settings.Web.UserAgent = UserAgentPreset.ChromeAndroid;
        settings.Web.AcceptLanguage = "ru-RU,ru;q=0.9";

        var (text, isError) = await Call(reg, "web_fetch", new { url = site.Url + "/post" });

        Assert.False(isError, text);
        Assert.Equal($"Title: Post\nURL: {site.Url}/post\n\n# Hello\nBody of the post.", text.Replace("\r", ""));
        var request = Assert.Single(site.Requests);
        Assert.Contains("Android", request);
        Assert.Contains("Mobile Safari", request);
        Assert.DoesNotContain("OfflineChatter", request);
        Assert.Contains("Accept-Language: ru-RU,ru;q=0.9", request);

        // Имя программы и своя строка
        settings.Web.UserAgent = UserAgentPreset.App;
        settings.Web.AcceptLanguage = null;
        settings.Web.StripNavigation = false;
        settings.Web.MainContentOnly = false;
        (text, _) = await Call(reg, "web_fetch", new { url = site.Url + "/post" });
        Assert.Contains("Home page of the site", text);
        Assert.Contains("User-Agent: OfflineChatter/9.9", site.Requests[1]);
        Assert.DoesNotContain("Accept-Language", site.Requests[1]);

        Assert.Equal("My Bot/1.0", UserAgents.Resolve(UserAgentPreset.Custom, " My Bot/1.0\r\n", "app"));
        Assert.Equal("app", UserAgents.Resolve(UserAgentPreset.Custom, "  ", "app"));
        foreach (var p in UserAgents.Presets.Where(p => p is not (UserAgentPreset.App or UserAgentPreset.Custom)))
            Assert.StartsWith("Mozilla/5.0", UserAgents.Resolve(p, null, "app"));
    }

    [Fact]
    public async Task Long_Page_Continues_From_Offset_Without_Second_Download()
    {
        using var site = new Site();
        var paragraphs = string.Concat(Enumerable.Range(1, 60).Select(i => $"<p>Paragraph number {i} with some filler text to make it longer.</p>"));
        site.Add("/long", "<body><article>" + paragraphs + "</article></body>");
        var (reg, http, settings, _) = Registry();
        using var _h = http;
        using var _r = reg;
        settings.Web.MaxChars = 1000;

        var (first, _) = await Call(reg, "web_fetch", new { url = site.Url + "/long" });
        Assert.Contains("Paragraph number 1 ", first);
        var m = System.Text.RegularExpressions.Regex.Match(first, @"\[Truncated: characters 0-(\d+) of (\d+)\. Call web_fetch with start=(\d+) to read on\.\]$");
        Assert.True(m.Success, first);
        Assert.Equal(m.Groups[1].Value, m.Groups[3].Value);
        var next = int.Parse(m.Groups[1].Value);
        Assert.InRange(next, 600, 1000);

        // Модели передают числа и строкой
        var (second, _) = await Call(reg, "web_fetch", new { url = site.Url + "/long", start = next.ToString() });
        var lastOfFirst = System.Text.RegularExpressions.Regex.Matches(first, @"Paragraph number (\d+) ").Cast<System.Text.RegularExpressions.Match>().Last().Groups[1].Value;
        Assert.Contains($"Paragraph number {int.Parse(lastOfFirst) + 1} ", second);
        Assert.DoesNotContain($"Paragraph number {lastOfFirst} ", second);
        Assert.Single(site.Requests);

        var (past, _) = await Call(reg, "web_fetch", new { url = site.Url + "/long", start = 999999 });
        Assert.Contains("No more text", past);
    }

    [Fact]
    public async Task Fetch_Limits_And_Refusals()
    {
        using var site = new Site();
        site.Pages["/cp1251"] = ("text/html", Encoding.GetEncoding(1251).GetBytes("<html><head><meta http-equiv='Content-Type' content='text/html; charset=windows-1251'></head><body><p>Привет, мир</p></body></html>"));
        site.Pages["/file.pdf"] = ("application/pdf", new byte[] { 1, 2, 3 });
        site.Add("/data.json", "{\"a\": 1}", "application/json");
        site.Add("/big", "<body><p>" + new string('x', 200_000) + "</p></body>");
        var (reg, http, settings, net) = Registry();
        using var _h = http;
        using var _r = reg;

        Assert.EndsWith("Привет, мир", (await Call(reg, "web_fetch", new { url = site.Url + "/cp1251" })).text);
        Assert.EndsWith("{\"a\": 1}", (await Call(reg, "web_fetch", new { url = site.Url + "/data.json" })).text);

        var pdf = await Call(reg, "web_fetch", new { url = site.Url + "/file.pdf" });
        Assert.True(pdf.isError);
        Assert.Contains("application/pdf", pdf.text);

        var missing = await Call(reg, "web_fetch", new { url = site.Url + "/nope" });
        Assert.True(missing.isError);
        Assert.Contains("404", missing.text);

        // Лимит скачивания: остаток страницы отбрасывается, об этом сказано в конце последней части
        settings.Web.MaxDownloadKb = 64;
        settings.Web.MaxChars = 60000;
        var big = await Call(reg, "web_fetch", new { url = site.Url + "/big" });
        Assert.False(big.isError);
        Assert.Contains("[Truncated: characters 0-60000 of 655", big.text);
        big = await Call(reg, "web_fetch", new { url = site.Url + "/big", start = 60000 });
        Assert.EndsWith("[The page is larger than the download limit: its end is missing.]", big.text);

        // Локальные адреса закрыты, пока пользователь их не разрешил
        settings.Web.AllowLocalAddresses = false;
        var local = await Call(reg, "web_fetch", new { url = site.Url + "/data.json" });
        Assert.True(local.isError);
        Assert.Contains("Local", local.text);
        var requests = site.Requests.Count;
        Assert.True((await Call(reg, "web_fetch", new { url = "ftp://example.com/x" })).isError);
        Assert.Equal(requests, site.Requests.Count);

        // Офлайн-режим: вызов отклоняется, а модели инструменты не предлагаются вовсе
        settings.Web.AllowLocalAddresses = true;
        net.OfflineMode = true;
        Assert.True((await Call(reg, "web_fetch", new { url = site.Url + "/data.json?fresh=1" })).isError);
        var offered = reg.ToOpenAiTools().Select(t => (string?)t["function"]!["name"]).ToList();
        Assert.DoesNotContain("web_fetch", offered);
        Assert.DoesNotContain("web_search", offered);
        Assert.Contains("calculator", offered);
        Assert.NotNull(reg.Find("web_fetch")); // в настройках инструмент остаётся виден
    }

    [Fact]
    public async Task Fetch_Times_Out_Instead_Of_Hanging()
    {
        using var site = new Site { Silent = true };
        var (reg, http, settings, _) = Registry();
        using var _h = http;
        using var _r = reg;
        settings.Web.TimeoutSeconds = 1;

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var r = await Call(reg, "web_fetch", new { url = site.Url + "/slow" });
        Assert.True(r.isError);
        Assert.Contains("No answer", r.text);
        Assert.InRange(sw.Elapsed.TotalSeconds, 0.5, 8);

        // Остановка пользователем — отмена, а не ошибка инструмента
        using var cts = new CancellationTokenSource(300);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            reg.ExecuteAsync(reg.Find("web_fetch")!, "{\"url\":\"" + site.Url + "/slow2\"}", cts.Token));
    }

    [Fact]
    public async Task Search_With_Custom_Address_Reads_Pages_And_Skips_Excluded()
    {
        using var pages = new Site("127.0.0.2");
        pages.Add("/a", "<body><article><h1>Page A</h1><p>Full text of page A about llamas.</p></article></body>");
        using var engine = new Site();
        engine.Add("/find", $@"<body>
<div class='r'><h3><a href='{pages.Url}/a'>Result A</a></h3><p>Snippet A.</p></div>
<div class='r'><h3><a href='https://blocked.example/x'>Blocked result</a></h3><p>Snippet blocked.</p></div>
<div class='r'><h3><a href='{pages.Url}/missing'>Result B</a></h3><p>Snippet B.</p></div>
<div class='r'><h3><a href='https://c.example/'>Result C</a></h3><p>Snippet C.</p></div>
<div class='r'><h3><a href='https://d.example/'>Result D</a></h3><p>Snippet D.</p></div></body>");
        var (reg, http, settings, _) = Registry();
        using var _h = http;
        using var _r = reg;
        var w = settings.Web;
        w.Engine = SearchEngine.Custom;
        w.CustomSearchUrl = engine.Url + "/find?q=";
        w.ExcludeDomains = new List<string> { "blocked.example" };
        w.MaxResults = 3;
        w.ReadPages = 2;

        var (text, isError) = await Call(reg, "web_search", new { query = "llama food" });

        Assert.False(isError, text);
        Assert.Equal(string.Join("\n",
            "[1] Result A",
            pages.Url + "/a",
            "Snippet A.",
            "Page text:",
            "# Page A",
            "Full text of page A about llamas.",
            "",
            "[2] Result B",   // страница не открылась — результат остаётся без текста
            pages.Url + "/missing",
            "Snippet B.",
            "",
            "[3] Result C",
            "https://c.example/",
            "Snippet C."), text.Replace("\r", ""));
        Assert.Contains("GET /find?q=llama%20food ", Assert.Single(engine.Requests));

        w.Snippets = false;
        w.ReadPages = 0;
        (text, _) = await Call(reg, "web_search", new { query = "x" });
        Assert.DoesNotContain("Snippet", text);
        Assert.DoesNotContain("Page text", text);

        // Вместо выдачи — проверка «я не робот»: понятная ошибка, а не «ничего не найдено»
        engine.Add("/find", "<body><div class='anomaly-modal'>Please complete the captcha</div></body>");
        var blocked = await Call(reg, "web_search", new { query = "x" });
        Assert.True(blocked.isError);
        Assert.Contains("robot check", blocked.text);

        engine.Add("/find", "<body><p>Nothing here</p></body>");
        Assert.Equal(("No results", false), await Call(reg, "web_search", new { query = "x" }));
    }

    [Fact]
    public async Task Search_Through_Searxng()
    {
        using var searx = new Site();
        searx.Add("/search", "{\"results\":[{\"title\":\"One\",\"url\":\"https://one.example/\",\"content\":\"First  text\"},{\"title\":\"No url\"},{\"title\":\"Two\",\"url\":\"https://two.example/\"}]}", "application/json");
        var (reg, http, settings, _) = Registry();
        using var _h = http;
        using var _r = reg;
        settings.Web.Engine = SearchEngine.Searxng;

        var unset = await Call(reg, "web_search", new { query = "x" });
        Assert.True(unset.isError);
        Assert.Contains("SearXNG", unset.text);

        settings.SearxngUrl = searx.Url;
        settings.Web.AllowLocalAddresses = false; // свой SearXNG на этом компьютере работает и при закрытых локальных адресах
        var (text, isError) = await Call(reg, "web_search", new { query = "x", period = "week" });
        Assert.False(isError, text);
        Assert.Equal("[1] One\nhttps://one.example/\nFirst text\n\n[2] Two\nhttps://two.example/", text.Replace("\r", ""));
        Assert.Contains("format=json", searx.Requests[0]);
        Assert.Contains("time_range=week", searx.Requests[0]);
    }

    [Theory]
    [InlineData("http://localhost/", true)]
    [InlineData("http://127.0.0.1:8080/", true)]
    [InlineData("http://2130706433/", true)]
    [InlineData("http://192.168.1.10/", true)]
    [InlineData("http://10.0.0.5/", true)]
    [InlineData("http://172.20.1.1/", true)]
    [InlineData("http://169.254.169.254/latest/meta-data", true)]
    [InlineData("http://[::1]/", true)]
    [InlineData("http://[fd00::1]/", true)]
    [InlineData("http://[::ffff:10.0.0.1]/", true)]
    [InlineData("http://router/", true)]
    [InlineData("http://nas.local/", true)]
    [InlineData("https://example.com/", false)]
    [InlineData("https://172.32.0.1/", false)]
    [InlineData("https://8.8.8.8/", false)]
    [InlineData("https://[2606:4700::1111]/", false)]
    public void Local_Addresses(string url, bool local) => Assert.Equal(local, WebGuard.IsLocal(new Uri(url)));

    [Fact]
    public void Url_From_The_Model_Is_Tidied()
    {
        Assert.Equal("https://example.com/a", WebTools.ParseUrl(" <example.com/a> ").AbsoluteUri);
        Assert.Equal("http://example.com/", WebTools.ParseUrl("http://example.com").AbsoluteUri);
        Assert.Throws<ArgumentException>(() => WebTools.ParseUrl("file:///c:/windows/win.ini"));
        Assert.Throws<ArgumentException>(() => WebTools.ParseUrl("just words"));
    }

    [Fact]
    public void Settings_Are_Migrated_And_Clamped()
    {
        // Разрешение fetch_url переезжает на web_fetch; у кого был свой SearXNG — остаётся на нём
        var old = new AppSettings { Version = 2 };
        old.Tools.Consent["builtin:fetch_url"] = ConsentMode.Always;
        old.Tools.SearxngUrl = "http://127.0.0.1:8888";
        old.Tools.Web = null!;
        SettingsStore.Normalize(old);
        Assert.Equal(ConsentMode.Always, old.Tools.Consent["builtin:web_fetch"]);
        Assert.False(old.Tools.Consent.ContainsKey("builtin:fetch_url"));
        Assert.Equal(SearchEngine.Searxng, old.Tools.Web.Engine);

        // Новая установка и уже перенесённые настройки: выбор пользователя не трогаем
        var current = new AppSettings();
        current.Tools.SearxngUrl = "http://127.0.0.1:8888";
        current.Tools.DefaultCity = "   ";
        current.Tools.Web.TimeoutSeconds = 100000;
        current.Tools.Web.MaxChars = 5;
        current.Tools.Web.ReadPages = 99;
        current.Tools.Web.ExcludeDomains = new List<string> { "https://WWW.Example.com/path", "example.com" };
        SettingsStore.Normalize(current);
        Assert.Equal(SearchEngine.DuckDuckGo, current.Tools.Web.Engine);
        Assert.Null(current.Tools.DefaultCity);
        Assert.Equal(20, current.Tools.Web.TimeoutSeconds);
        Assert.Equal(12000, current.Tools.Web.MaxChars);
        Assert.Equal(0, current.Tools.Web.ReadPages);
        Assert.Equal(new[] { "example.com" }, current.Tools.Web.ExcludeDomains);
    }
}

public class DefaultLocationTests
{
    private static JObject Schema(params string[] names)
    {
        var props = new JObject();
        foreach (var n in names) props[n.TrimEnd('#')] = new JObject { ["type"] = n.EndsWith("#") ? "number" : "string" };
        return new JObject { ["type"] = "object", ["properties"] = props };
    }

    [Theory]
    [InlineData("get_weather", null, true)]
    [InlineData("forecast", "", true)]
    [InlineData("pogoda", "Прогноз", true)]
    [InlineData("city_info", "Returns current weather and air quality", true)]
    [InlineData("info", "Текущая погода в городе", true)]
    [InlineData("web_search", "Search the web. Returns titles, URLs and short snippets", false)]
    [InlineData("calculator", "Exact calculator", false)]
    public void Recognizes_Weather_Tools(string name, string? description, bool expected) =>
        Assert.Equal(expected, DefaultLocation.IsWeatherTool(name, description));

    [Fact]
    public void Fills_Only_A_Missing_Place()
    {
        var schema = Schema("units", "city", "days#");

        var args = new JObject { ["units"] = "metric" };
        Assert.True(DefaultLocation.Fill(schema, args, "Казань"));
        Assert.Equal("Казань", (string?)args["city"]);

        args = new JObject { ["city"] = "  " };
        Assert.True(DefaultLocation.Fill(schema, args, "Казань"));
        Assert.Equal("Казань", (string?)args["city"]);

        // Модель назвала место сама — настройка не вмешивается
        args = new JObject { ["city"] = "Omsk" };
        Assert.False(DefaultLocation.Fill(schema, args, "Казань"));
        Assert.Equal("Omsk", (string?)args["city"]);

        // Координаты — тоже место
        args = new JObject { ["latitude"] = 55.7, ["longitude"] = 37.6 };
        Assert.False(DefaultLocation.Fill(Schema("location", "latitude#", "longitude#"), args, "Казань"));

        Assert.Equal("location", DefaultLocation.FindLocationProperty(Schema("lang", "query", "location")));
        Assert.Equal("cityName", DefaultLocation.FindLocationProperty(Schema("lang", "cityName")));
        Assert.Equal("target_place", DefaultLocation.FindLocationProperty(Schema("lang", "target_place")));
        Assert.Null(DefaultLocation.FindLocationProperty(Schema("lang", "days#")));
        Assert.Null(DefaultLocation.FindLocationProperty(Schema("city#")));
        Assert.False(DefaultLocation.Fill(null, new JObject(), "Казань"));
    }

    [Fact]
    public void Empty_City_Changes_Nothing()
    {
        Assert.Null(DefaultLocation.Normalize(null));
        Assert.Null(DefaultLocation.Normalize(" \r\n "));
        Assert.Equal("Нижний Новгород", DefaultLocation.Normalize("  Нижний \n Новгород "));
    }
}
