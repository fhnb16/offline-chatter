using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace OfflineChatter.Core.Tools;

/// <summary>Что убирать со страницы перед тем, как отдать её текст модели. Каждый пункт отключается отдельно.</summary>
public sealed class PageCleanOptions
{
    public bool MainContentOnly { get; set; } = true;
    public bool StripNavigation { get; set; } = true;
    public bool StripBoilerplate { get; set; } = true;
    public bool StripComments { get; set; } = true;
    public bool DropDuplicateLines { get; set; } = true;
    public bool KeepLinks { get; set; }
    public bool Markdown { get; set; } = true;

    /// <summary>Весь текст страницы как есть, без разметки.</summary>
    public static PageCleanOptions Raw => new()
    {
        MainContentOnly = false, StripNavigation = false, StripBoilerplate = false, StripComments = false,
        DropDuplicateLines = false, KeepLinks = false, Markdown = false,
    };
}

public sealed class PageText
{
    public string Title { get; set; } = string.Empty;
    public string Text { get; set; } = string.Empty;
}

/// <summary>
/// Текст страницы для контекста модели: без меню, подвала, рекламы и прочего обрамления — оно съедает контекст
/// и сбивает модель. Правила — по семантике (nav, footer, role) и по распространённым именам классов;
/// от ошибки на необычной вёрстке защищают проверки доли текста и заголовка h1 внутри блока.
/// </summary>
public static class PageExtractor
{
    /// <summary>Никогда не текст страницы.</summary>
    private static readonly HashSet<string> AlwaysDropped = new(StringComparer.Ordinal)
    {
        "noscript", "svg", "template", "iframe", "canvas", "object", "embed", "map", "head", "link", "meta",
    };

    /// <summary>Поля форм и проигрыватели: подписи кнопок и списки вариантов — не содержание.</summary>
    private static readonly HashSet<string> FormControls = new(StringComparer.Ordinal)
    {
        "select", "button", "input", "textarea", "video", "audio", "dialog", "datalist",
    };

    private static readonly HashSet<string> NavigationRoles = new(StringComparer.Ordinal)
    {
        "navigation", "banner", "contentinfo", "complementary", "search", "menu", "menubar", "toolbar",
    };

    private static readonly HashSet<string> NavigationHints = new(StringComparer.Ordinal)
    {
        "nav", "navbar", "navigation", "menu", "menubar", "sidebar", "footer", "breadcrumb", "breadcrumbs",
        "masthead", "topbar", "pagination", "pager", "skiplink", "sitemap",
    };

    /// <summary>Слова, с которыми класс «header» означает шапку сайта, а не заголовок статьи.</summary>
    private static readonly HashSet<string> SiteHeaderHints = new(StringComparer.Ordinal)
    {
        "site", "page", "global", "top", "main", "app", "sticky", "fixed",
    };

    private static readonly HashSet<string> BoilerplateHints = new(StringComparer.Ordinal)
    {
        "cookie", "cookies", "consent", "gdpr", "advert", "advertisement", "ads", "ad", "adsbygoogle", "promo", "sponsor",
        "sponsored", "share", "sharing", "social", "subscribe", "newsletter", "popup", "modal", "overlay", "related",
        "recommended", "recommendations", "outbrain", "taboola", "banner",
    };

    private static readonly HashSet<string> CommentHints = new(StringComparer.Ordinal)
    {
        "comments", "comment", "disqus", "discussion", "replies",
    };

    private static readonly HashSet<string> Blocks = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "body", "caption", "center", "dd", "details", "div", "dl", "dt", "fieldset",
        "figcaption", "figure", "footer", "form", "h1", "h2", "h3", "h4", "h5", "h6", "header", "html", "li", "main", "nav", "ol",
        "p", "pre", "section", "summary", "table", "tbody", "tfoot", "thead", "tr", "ul",
    };

    /// <summary>Обёртки, в которые можно спускаться при поиске основного блока.</summary>
    private static readonly HashSet<string> Wrappers = new(StringComparer.Ordinal)
    {
        "#root", "html", "body", "div", "section", "center", "form", "main", "article", "span",
        "table", "tbody", "tr", "td", // старые сайты, свёрстанные таблицей
    };

    public static PageText Extract(string html, Uri? baseUri, PageCleanOptions options)
    {
        var root = HtmlParser.Parse(html);
        var title = Collapse(string.Concat((root.Descendants().FirstOrDefault(x => x.Tag == "title")?.Children ?? new List<HtmlNode>())
            .Select(x => x.Text)));
        var body = root.Descendants().FirstOrDefault(x => x.Tag == "body") ?? root;

        RemoveWhere(body, x => AlwaysDropped.Contains(x.Tag) || x.Tag == "title");
        var total = HtmlParser.Measure(body);

        if (options.StripBoilerplate)
        {
            RemoveWhere(body, x => FormControls.Contains(x.Tag) || IsHidden(x));
            RemoveWhere(body, x => HasHint(x, BoilerplateHints) && CanDrop(x, total));
        }
        if (options.StripComments)
            RemoveWhere(body, x => HasHint(x, CommentHints) && CanDrop(x, total));
        if (options.StripNavigation)
        {
            RemoveWhere(body, x => IsNavigation(x, total));
        }

        var content = body;
        if (options.MainContentOnly)
        {
            HtmlParser.Measure(body);
            content = FindMain(body);
        }

        var writer = new LineWriter(options, baseUri);
        writer.Walk(content);
        var text = writer.Finish();
        if (title.Length == 0)
            title = Collapse(TextOf(body.Descendants().FirstOrDefault(x => x.Tag == "h1")));
        return new PageText { Title = title, Text = text };
    }

    // ---------------- Удаление обрамления ----------------

    private static void RemoveWhere(HtmlNode root, Func<HtmlNode, bool> match)
    {
        // Сверху вниз: удалённый блок уносит всё вложенное, проверять его потомков уже не нужно
        var stack = new Stack<HtmlNode>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            for (var i = node.Children.Count - 1; i >= 0; i--)
            {
                var child = node.Children[i];
                if (child.IsText) continue;
                if (match(child)) node.Children.RemoveAt(i);
                else stack.Push(child);
            }
        }
    }

    private static bool IsHidden(HtmlNode x)
    {
        if (x.HasAttr("hidden") || x.Attr("aria-hidden") == "true") return true;
        var style = x.Attr("style");
        if (style.Length == 0) return false;
        var s = style.Replace(" ", string.Empty).ToLowerInvariant();
        return s.Contains("display:none") || s.Contains("visibility:hidden");
    }

    private static bool IsNavigation(HtmlNode x, int total)
    {
        switch (x.Tag)
        {
            case "nav" or "footer" or "aside":
                return CanDrop(x, int.MaxValue);
            case "header":
                // Шапка сайта; <header> внутри статьи — её заголовок, он остаётся
                return !x.HasAncestor(p => p.Tag is "article" or "main") && CanDrop(x, int.MaxValue);
        }
        if (NavigationRoles.Contains(x.Attr("role").ToLowerInvariant())) return CanDrop(x, int.MaxValue);

        var tokens = Tokens(x);
        if (tokens.Count == 0) return false;
        if (tokens.Any(NavigationHints.Contains)) return CanDrop(x, total);
        return tokens.Contains("header") && (x.Attr("id").Equals("header", StringComparison.OrdinalIgnoreCase) || tokens.Any(SiteHeaderHints.Contains))
               && CanDrop(x, total);
    }

    /// <summary>
    /// Можно ли выбросить блок: не корневой, без заголовка h1 и без основного содержимого внутри.
    /// <paramref name="total"/> — текст всей страницы: блок, в котором больше половины текста, по имени класса не удаляется
    /// (int.MaxValue — проверка доли не нужна, блок опознан по тегу или роли).
    /// </summary>
    private static bool CanDrop(HtmlNode x, int total)
    {
        if (x.Tag is "html" or "body" or "main" or "article") return false;
        if (total != int.MaxValue && total > 0 && x.TextLength * 2 > total) return false;
        foreach (var d in x.Descendants())
            if (d.Tag is "h1" or "main" or "article") return false;
        return true;
    }

    private static bool HasHint(HtmlNode x, HashSet<string> hints)
    {
        var tokens = Tokens(x);
        return tokens.Count > 0 && tokens.Any(hints.Contains);
    }

    /// <summary>Слова из class и id: "site-footer", "mainNav", "cookie_banner" → site, footer, main, nav, cookie, banner.</summary>
    internal static List<string> Tokens(HtmlNode x)
    {
        var list = new List<string>();
        Split(x.Attr("class"), list);
        Split(x.Attr("id"), list);
        return list;
    }

    private static void Split(string value, List<string> into)
    {
        if (value.Length == 0) return;
        var sb = new StringBuilder();
        void Flush()
        {
            if (sb.Length > 0) into.Add(sb.ToString().ToLowerInvariant());
            sb.Clear();
        }
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (!char.IsLetterOrDigit(c))
            {
                Flush();
                continue;
            }
            // Граница camelCase: «mainNav» → main, nav
            if (char.IsUpper(c) && i > 0 && char.IsLower(value[i - 1])) Flush();
            sb.Append(c);
        }
        Flush();
    }

    // ---------------- Основной блок ----------------

    private static HtmlNode FindMain(HtmlNode body)
    {
        var total = body.TextLength;
        if (total == 0) return body;
        bool Enough(HtmlNode x) => x.TextLength >= 200 && x.TextLength * 4 >= total;

        var main = body.Descendants().Where(x => x.Tag == "main" || x.Attr("role").Equals("main", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(x => x.TextLength).FirstOrDefault();
        var start = main != null && Enough(main) ? main : body;

        // Одна статья на странице — это и есть содержимое; несколько (лента) — берём общий блок
        var articles = start.Descendants().Where(x => x.Tag == "article" && !x.HasAncestor(p => p.Tag == "article")).ToList();
        if (articles.Count == 1 && Enough(articles[0])) start = articles[0];

        // Спуск по обёрткам: пока почти весь текст лежит в одном вложенном блоке, обрамление вокруг него не нужно
        var node = start;
        while (true)
        {
            var next = node.Children.FirstOrDefault(c => !c.IsText && Wrappers.Contains(c.Tag) && c.TextLength * 100 >= node.TextLength * 85);
            if (next == null) break;
            node = next;
        }
        return node;
    }

    // ---------------- Текст ----------------

    private static string TextOf(HtmlNode? node)
    {
        if (node == null) return string.Empty;
        if (node.IsText) return node.Text!;
        return string.Concat(node.Descendants().Where(x => x.IsText).Select(x => x.Text));
    }

    internal static string Collapse(string? s)
    {
        if (string.IsNullOrEmpty(s)) return string.Empty;
        var sb = new StringBuilder(s!.Length);
        var space = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c) || c == ' ' || c == '​')
            {
                space = sb.Length > 0;
                continue;
            }
            if (space) sb.Append(' ');
            space = false;
            sb.Append(c);
        }
        return sb.ToString();
    }

    /// <summary>Обход дерева с выводом строк: блоки — с новой строки, заголовки и списки — разметкой Markdown.</summary>
    private sealed class LineWriter
    {
        private readonly PageCleanOptions _o;
        private readonly Uri? _base;
        private readonly List<(string Text, bool Verbatim)> _lines = new();
        private readonly StringBuilder _line = new();
        private string? _prefix;   // маркер списка или заголовка для следующей строки
        private int _pre;
        private int _quote;
        private int _list;
        private bool _pendingSpace;

        public LineWriter(PageCleanOptions options, Uri? baseUri)
        {
            _o = options;
            _base = baseUri;
        }

        public void Walk(HtmlNode node)
        {
            if (node.IsText)
            {
                AppendText(node.Text!);
                return;
            }

            var tag = node.Tag;
            switch (tag)
            {
                case "br":
                    if (_pre > 0) EndLine(keepEmpty: true); else EndLine();
                    return;
                case "hr":
                    EndLine();
                    return;
                case "img":
                    return;
            }

            var block = Blocks.Contains(tag);
            if (block) EndLine();

            var heading = tag.Length == 2 && tag[0] == 'h' && tag[1] is >= '1' and <= '6';
            int? ordinal = null;
            if (heading)
            {
                Blank();
                if (_o.Markdown) _prefix = new string('#', tag[1] - '0') + " ";
            }
            else if (tag == "li")
            {
                if (_o.Markdown)
                {
                    var indent = new string(' ', Math.Max(0, _list - 1) * 2);
                    if (node.Parent?.Tag == "ol")
                        ordinal = node.Parent.Children.Where(c => c.Tag == "li").TakeWhile(c => !ReferenceEquals(c, node)).Count() + 1;
                    _prefix = indent + (ordinal != null ? ordinal + ". " : "- ");
                }
            }
            else if (tag is "ul" or "ol") _list++;
            else if (tag == "blockquote") _quote++;
            else if (tag == "pre")
            {
                if (_o.Markdown) _lines.Add(("```", true));
                _pre++;
            }
            else if (tag is "td" or "th")
            {
                if (_line.Length > 0) _line.Append(" | ");
                _pendingSpace = false;
            }

            var linkStart = -1;
            var linkLines = 0;
            if (tag == "a" && _o.KeepLinks && _pre == 0)
            {
                if (_pendingSpace && _line.Length > 0) _line.Append(' ');
                _pendingSpace = false;
                linkStart = _line.Length;
                linkLines = _lines.Count;
            }

            foreach (var child in node.Children) Walk(child);

            if (linkStart >= 0 && linkLines == _lines.Count && linkStart <= _line.Length)
            {
                var label = _line.ToString(linkStart, _line.Length - linkStart).Trim();
                var url = Resolve(node.Attr("href"));
                if (label.Length > 0 && url != null)
                {
                    _line.Length = linkStart;
                    _line.Append('[').Append(label).Append("](").Append(url).Append(')');
                }
            }

            if (block) EndLine();
            if (tag is "ul" or "ol") _list--;
            else if (tag == "blockquote") _quote--;
            else if (tag == "pre")
            {
                _pre--;
                if (_o.Markdown) _lines.Add(("```", true));
            }
            else if (heading || tag == "li") _prefix = null; // пустой пункт не должен отдать маркер чужой строке
        }

        private string? Resolve(string href)
        {
            href = href.Trim();
            if (href.Length == 0 || href[0] == '#') return null;
            if (!(_base != null ? Uri.TryCreate(_base, href, out var u) : Uri.TryCreate(href, UriKind.Absolute, out u))) return null;
            return u.Scheme is "http" or "https" ? u.AbsoluteUri : null;
        }

        private void AppendText(string text)
        {
            if (_pre > 0)
            {
                var parts = text.Replace("\r\n", "\n").Split('\n');
                for (var i = 0; i < parts.Length; i++)
                {
                    if (i > 0) EndLine(keepEmpty: true);
                    _line.Append(parts[i]);
                }
                return;
            }
            foreach (var c in text)
            {
                if (char.IsWhiteSpace(c) || c == ' ' || c == '​')
                {
                    _pendingSpace = true;
                    continue;
                }
                if (_pendingSpace && _line.Length > 0) _line.Append(' ');
                _pendingSpace = false;
                _line.Append(c);
            }
        }

        private void EndLine(bool keepEmpty = false)
        {
            _pendingSpace = false;
            if (_pre > 0)
            {
                if (_line.Length > 0 || keepEmpty) _lines.Add((_line.ToString().TrimEnd(), true));
                _line.Clear();
                return;
            }
            var text = _line.ToString().Trim();
            _line.Clear();
            if (text.Length == 0) return;
            if (_o.Markdown)
            {
                if (_prefix != null) text = _prefix + text;
                if (_quote > 0) text = "> " + text;
            }
            _prefix = null;
            _lines.Add((text, false));
        }

        /// <summary>Пустая строка перед заголовком — отделяет разделы.</summary>
        private void Blank()
        {
            if (_lines.Count > 0 && _lines[_lines.Count - 1].Text.Length > 0) _lines.Add((string.Empty, false));
        }

        public string Finish()
        {
            EndLine();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            var sb = new StringBuilder();
            var lastBlank = true;
            foreach (var (text, verbatim) in _lines)
            {
                if (!verbatim)
                {
                    if (text.Length == 0)
                    {
                        if (!lastBlank) sb.Append('\n');
                        lastBlank = true;
                        continue;
                    }
                    // Короткие строки («Да», «5 шт.») повторяются по делу; длинные повторы — это меню и врезки
                    if (_o.DropDuplicateLines && text.Length >= 16 && !seen.Add(text)) continue;
                }
                sb.Append(text).Append('\n');
                lastBlank = false;
            }
            return sb.ToString().Trim('\n', ' ');
        }
    }
}
