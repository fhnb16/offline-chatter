using System;
using System.Collections.Generic;
using System.Net;
using System.Text;

namespace OfflineChatter.Core.Tools;

/// <summary>Узел разобранной страницы: элемент или текст.</summary>
internal sealed class HtmlNode
{
    public HtmlNode(string tag) => Tag = tag;

    /// <summary>Имя тега в нижнем регистре; у текста — "#text".</summary>
    public string Tag { get; }

    /// <summary>Текст узла (уже без сущностей); у элементов — null.</summary>
    public string? Text { get; set; }

    public Dictionary<string, string>? Attributes { get; set; }
    public HtmlNode? Parent { get; set; }
    public List<HtmlNode> Children { get; } = new();

    /// <summary>Число значимых символов текста в поддереве (см. <see cref="HtmlParser.Measure"/>).</summary>
    public int TextLength { get; set; }

    public bool IsText => Text != null;

    public string Attr(string name) => Attributes != null && Attributes.TryGetValue(name, out var v) ? v : string.Empty;

    public bool HasAttr(string name) => Attributes != null && Attributes.ContainsKey(name);

    public void Remove() => Parent?.Children.Remove(this);

    public IEnumerable<HtmlNode> Descendants()
    {
        var stack = new Stack<HtmlNode>();
        for (var i = Children.Count - 1; i >= 0; i--) stack.Push(Children[i]);
        while (stack.Count > 0)
        {
            var n = stack.Pop();
            yield return n;
            for (var i = n.Children.Count - 1; i >= 0; i--) stack.Push(n.Children[i]);
        }
    }

    public bool HasAncestor(Func<HtmlNode, bool> match)
    {
        for (var p = Parent; p != null; p = p.Parent)
            if (match(p)) return true;
        return false;
    }
}

/// <summary>
/// Небольшой терпимый разбор HTML в дерево — без внешних библиотек (вес exe).
/// Не проверяет вложенность по стандарту: задача — отделить блоки страницы друг от друга, а не построить точный DOM.
/// </summary>
internal static class HtmlParser
{
    private const int MaxDepth = 400;

    private static readonly HashSet<string> Void = new(StringComparer.Ordinal)
    {
        "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr",
    };

    /// <summary>Содержимое пропускается целиком: это не текст страницы.</summary>
    private static readonly HashSet<string> Skipped = new(StringComparer.Ordinal) { "script", "style" };

    /// <summary>Внутри — только текст, теги не разбираются.</summary>
    private static readonly HashSet<string> RawText = new(StringComparer.Ordinal) { "title", "textarea" };

    /// <summary>Открытие такого тега закрывает незакрытый абзац.</summary>
    private static readonly HashSet<string> ClosesParagraph = new(StringComparer.Ordinal)
    {
        "address", "article", "aside", "blockquote", "details", "div", "dl", "fieldset", "figcaption", "figure", "footer", "form",
        "h1", "h2", "h3", "h4", "h5", "h6", "header", "hr", "main", "nav", "ol", "p", "pre", "section", "table", "ul",
    };

    /// <summary>Границы, выше которых незакрытый элемент не ищется.</summary>
    private static readonly HashSet<string> Containers = new(StringComparer.Ordinal)
    {
        "html", "body", "div", "section", "article", "main", "aside", "header", "footer", "nav", "td", "th", "li", "blockquote",
        "table", "form", "figure", "details", "fieldset", "dd", "dt",
    };

    public static HtmlNode Parse(string html)
    {
        var root = new HtmlNode("#root");
        var open = new List<HtmlNode> { root };
        var text = new StringBuilder();
        var i = 0;
        var n = html.Length;

        HtmlNode Top() => open[open.Count - 1];

        void FlushText()
        {
            if (text.Length == 0) return;
            var node = new HtmlNode("#text") { Text = WebUtility.HtmlDecode(text.ToString()), Parent = Top() };
            Top().Children.Add(node);
            text.Clear();
        }

        // Закрыть ближайший открытый элемент из набора, не выходя за границу
        void CloseOpen(Func<string, bool> target, Func<string, bool> boundary)
        {
            for (var k = open.Count - 1; k > 0; k--)
            {
                var tag = open[k].Tag;
                if (target(tag))
                {
                    open.RemoveRange(k, open.Count - k);
                    return;
                }
                if (boundary(tag)) return;
            }
        }

        while (i < n)
        {
            var c = html[i];
            if (c != '<')
            {
                text.Append(c);
                i++;
                continue;
            }

            // Комментарий
            if (string.CompareOrdinal(html, i, "<!--", 0, 4) == 0)
            {
                var end = html.IndexOf("-->", i + 4, StringComparison.Ordinal);
                i = end < 0 ? n : end + 3;
                continue;
            }
            // <!DOCTYPE …>, <?xml …?>, <![CDATA[ … ]]>
            if (i + 1 < n && (html[i + 1] == '!' || html[i + 1] == '?'))
            {
                if (string.CompareOrdinal(html, i, "<![CDATA[", 0, 9) == 0)
                {
                    var endData = html.IndexOf("]]>", i + 9, StringComparison.Ordinal);
                    var stop = endData < 0 ? n : endData;
                    text.Append(html, i + 9, stop - i - 9);
                    i = endData < 0 ? n : endData + 3;
                    continue;
                }
                var end = html.IndexOf('>', i + 2);
                i = end < 0 ? n : end + 1;
                continue;
            }

            // Закрывающий тег
            if (i + 1 < n && html[i + 1] == '/')
            {
                var j = i + 2;
                while (j < n && IsNameChar(html[j])) j++;
                if (j == i + 2)
                {
                    text.Append(c);
                    i++;
                    continue;
                }
                var name = html.Substring(i + 2, j - i - 2).ToLowerInvariant();
                var gt = html.IndexOf('>', j);
                i = gt < 0 ? n : gt + 1;
                FlushText();
                for (var k = open.Count - 1; k > 0; k--)
                {
                    if (open[k].Tag != name) continue;
                    open.RemoveRange(k, open.Count - k);
                    break;
                }
                continue;
            }

            // Открывающий тег
            if (i + 1 >= n || !IsNameStart(html[i + 1]))
            {
                text.Append(c);
                i++;
                continue;
            }

            var p = i + 1;
            while (p < n && IsNameChar(html[p])) p++;
            var tagName = html.Substring(i + 1, p - i - 1).ToLowerInvariant();
            var attrs = ReadAttributes(html, ref p, out var selfClosing);
            i = p;
            FlushText();

            if (Skipped.Contains(tagName))
            {
                i = SkipTo(html, i, tagName, out _);
                continue;
            }

            // Неявное закрытие: <p> перед блоком, <li> перед следующим <li> и т. п.
            if (ClosesParagraph.Contains(tagName)) CloseOpen(t => t == "p", Containers.Contains);
            switch (tagName)
            {
                case "li": CloseOpen(t => t == "li", t => t is "ul" or "ol" or "body"); break;
                case "dt" or "dd": CloseOpen(t => t is "dt" or "dd", t => t is "dl" or "body"); break;
                case "tr": CloseOpen(t => t == "tr", t => t is "table" or "body"); break;
                case "td" or "th": CloseOpen(t => t is "td" or "th", t => t is "tr" or "table" or "body"); break;
                case "option": CloseOpen(t => t == "option", t => t is "select" or "body"); break;
            }

            var node = new HtmlNode(tagName) { Attributes = attrs, Parent = Top() };
            Top().Children.Add(node);

            if (RawText.Contains(tagName))
            {
                i = SkipTo(html, i, tagName, out var raw);
                if (raw.Length > 0) node.Children.Add(new HtmlNode("#text") { Text = WebUtility.HtmlDecode(raw), Parent = node });
                continue;
            }
            // Слишком глубокая вложенность: остальное остаётся на этом уровне, чтобы обход дерева не переполнил стек
            if (!selfClosing && !Void.Contains(tagName) && open.Count < MaxDepth) open.Add(node);
        }
        FlushText();
        return root;
    }

    /// <summary>Пропустить содержимое до закрывающего тега; возвращает позицию после него.</summary>
    private static int SkipTo(string html, int from, string tag, out string content)
    {
        var search = from;
        while (true)
        {
            var close = html.IndexOf("</", search, StringComparison.Ordinal);
            if (close < 0)
            {
                content = html.Substring(from);
                return html.Length;
            }
            if (string.Compare(html, close + 2, tag, 0, tag.Length, StringComparison.OrdinalIgnoreCase) == 0)
            {
                content = html.Substring(from, close - from);
                var gt = html.IndexOf('>', close);
                return gt < 0 ? html.Length : gt + 1;
            }
            search = close + 2;
        }
    }

    private static Dictionary<string, string>? ReadAttributes(string html, ref int p, out bool selfClosing)
    {
        Dictionary<string, string>? attrs = null;
        selfClosing = false;
        var n = html.Length;
        while (p < n)
        {
            while (p < n && char.IsWhiteSpace(html[p])) p++;
            if (p >= n) break;
            if (html[p] == '>')
            {
                p++;
                break;
            }
            if (html[p] == '/')
            {
                selfClosing = p + 1 < n && html[p + 1] == '>';
                p++;
                continue;
            }

            var start = p;
            while (p < n && !char.IsWhiteSpace(html[p]) && html[p] != '=' && html[p] != '>' && html[p] != '/') p++;
            if (p == start)
            {
                p++; // одиночный «=» или другой мусор
                continue;
            }
            var name = html.Substring(start, p - start).ToLowerInvariant();
            while (p < n && char.IsWhiteSpace(html[p])) p++;

            var value = string.Empty;
            if (p < n && html[p] == '=')
            {
                p++;
                while (p < n && char.IsWhiteSpace(html[p])) p++;
                if (p < n && (html[p] == '"' || html[p] == '\''))
                {
                    var quote = html[p];
                    var end = html.IndexOf(quote, p + 1);
                    if (end < 0) end = n;
                    value = html.Substring(p + 1, end - p - 1);
                    p = Math.Min(n, end + 1);
                }
                else
                {
                    var vs = p;
                    while (p < n && !char.IsWhiteSpace(html[p]) && html[p] != '>') p++;
                    value = html.Substring(vs, p - vs);
                }
            }
            attrs ??= new Dictionary<string, string>(StringComparer.Ordinal);
            if (!attrs.ContainsKey(name)) attrs[name] = value.IndexOf('&') >= 0 ? WebUtility.HtmlDecode(value) : value;
        }
        return attrs;
    }

    private static bool IsNameStart(char c) => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z';

    private static bool IsNameChar(char c) => IsNameStart(c) || c is >= '0' and <= '9' or '-' or ':' or '_';

    /// <summary>Пересчитать <see cref="HtmlNode.TextLength"/> во всём поддереве: символы без пробельных.</summary>
    public static int Measure(HtmlNode node)
    {
        // Обход без рекурсии по потомкам в обратном порядке: дети считаются раньше родителей
        var order = new List<HtmlNode> { node };
        for (var k = 0; k < order.Count; k++) order.AddRange(order[k].Children);
        for (var k = order.Count - 1; k >= 0; k--)
        {
            var x = order[k];
            if (x.IsText)
            {
                var count = 0;
                foreach (var ch in x.Text!)
                    if (!char.IsWhiteSpace(ch)) count++;
                x.TextLength = count;
            }
            else
            {
                var sum = 0;
                foreach (var ch in x.Children) sum += ch.TextLength;
                x.TextLength = sum;
            }
        }
        return node.TextLength;
    }
}
