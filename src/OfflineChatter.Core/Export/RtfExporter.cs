using System;
using System.Linq;
using System.Text;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Formulas;
using OfflineChatter.Core.Text;

namespace OfflineChatter.Core.Export;

/// <summary>
/// RTF — им же сохраняется «.doc (Word 97–2003)»: Word 97+ и LibreOffice открывают такой файл как документ.
/// Формулы — картинками PNG (рендер XAML-Math из UI), LaTeX сохраняется рядом скрытым текстом;
/// без рендерера — читаемый плоский текст формулы.
/// </summary>
public static class RtfExporter
{
    public static string Export(ExportDocument doc, ExportOptions o)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0\uc1")
          .Append(@"{\fonttbl{\f0\fswiss\fcharset0 Calibri;}{\f1\fmodern\fcharset0 Consolas;}{\f2\fswiss\fcharset0 Segoe UI;}{\f3\froman\fcharset0 Cambria Math;}}")
          .Append(@"{\colortbl;\red0\green103\blue192;\red127\green127\blue127;\red243\green243\blue243;\red5\green99\blue193;}")
          .Append(@"{\info{\title ").Append(Esc(doc.Title)).Append(@"}{\author Offline Chatter}}")
          .Append(@"\paperw11906\paperh16838\margl1134\margr1134\margt1134\margb1134\fs22 ");

        sb.Append(@"\pard\sa60\b\fs40 ").Append(Esc(doc.Title)).Append(@"\b0\fs22\par");
        sb.Append(@"\pard\sa240\cf2 ").Append(Esc(doc.Created.ToString("f"))).Append(@"\cf0\par");

        foreach (var m in doc.Messages)
        {
            sb.Append(@"\pard\sb280\sa60\keepn\brdrt\brdrs\brdrw10\brsp80\b\fs20\cf").Append(m.Role == ChatRole.User ? "1" : "2").Append(' ')
              .Append(Esc(ExportDocument.Label(m, o).ToUpperInvariant())).Append(@"\cf0\b0\fs22\par");
            foreach (var a in m.Attachments)
            {
                if (a.Kind == AttachmentKind.Image && o.IncludeImages) sb.Append(@"\pard\sa120 ").Append(Picture(a)).Append(@"\par");
                else if (a.Kind == AttachmentKind.Text) sb.Append(@"\pard\i\cf2 ").Append(Esc("📎 " + a.Name)).Append(@"\i0\cf0\par");
            }
            if (o.IncludeReasoning && !string.IsNullOrWhiteSpace(m.Reasoning))
            {
                sb.Append(@"\pard\li360\sa0\fs18\cf2\i ").Append(Esc(o.ReasoningLabel)).Append(@"\i0\par");
                foreach (var line in m.Reasoning!.Trim().Split('\n')) sb.Append(@"\pard\li360\sa0\fs18\cf2 ").Append(Esc(line.TrimEnd('\r'))).Append(@"\par");
                sb.Append(@"\cf0\fs22");
            }
            var md = Markdig.Markdown.Parse(MarkdownText.NormalizeMath(m.Content), Exporter.Pipeline);
            foreach (var b in md) Block(b, sb, o, 0, null);
        }
        sb.Append('}');
        return sb.ToString();
    }

    private static void Block(Block block, StringBuilder sb, ExportOptions o, int indent, string? marker)
    {
        var li = indent > 0 ? $@"\li{indent + 360}\fi-360" : string.Empty;
        switch (block)
        {
            case HeadingBlock h:
                sb.Append(@"\pard\sb200\sa80\keepn\b\fs").Append(h.Level switch { 1 => 32, 2 => 28, _ => 24 }).Append(' ');
                Inlines(h.Inline, sb, o);
                sb.Append(@"\b0\fs22\par");
                break;
            case MathBlock mb:
                sb.Append(@"\pard\qc\sa120 ");
                WriteMath(mb.Lines.ToString().Trim(), true, sb, o);
                sb.Append(@"\par\ql");
                break;
            case FencedCodeBlock or CodeBlock:
                foreach (var line in ((LeafBlock)block).Lines.ToString().TrimEnd('\r', '\n').Split('\n'))
                    sb.Append(@"\pard\sa0\cbpat3\f1\fs19 ").Append(Esc(line.TrimEnd('\r'))).Append(@"\f0\fs22\par");
                sb.Append(@"\pard\sa120\par");
                break;
            case ParagraphBlock p:
                sb.Append(@"\pard\sa120").Append(li).Append(' ');
                if (marker != null) sb.Append(Esc(marker)).Append(@"\tab ");
                Inlines(p.Inline, sb, o);
                sb.Append(@"\par");
                break;
            case QuoteBlock q:
                foreach (var child in q)
                {
                    if (child is ParagraphBlock qp)
                    {
                        sb.Append(@"\pard\li360\sa120\brdrl\brdrs\brdrw30\brsp120\cf2 ");
                        Inlines(qp.Inline, sb, o);
                        sb.Append(@"\cf0\par");
                    }
                    else Block(child, sb, o, indent, null);
                }
                break;
            case ListBlock lb:
            {
                var n = int.TryParse(lb.OrderedStart, out var start) ? start : 1;
                foreach (var item in lb.OfType<ListItemBlock>())
                {
                    var mk = lb.IsOrdered ? n++ + "." : "•";
                    var first = true;
                    foreach (var child in item)
                    {
                        Block(child, sb, o, indent + 360, first ? mk : null);
                        first = false;
                    }
                }
                break;
            }
            case Table t:
            {
                var rows = t.OfType<TableRow>().ToList();
                var cols = rows.Count == 0 ? 1 : rows.Max(r => r.Count);
                var width = 9600 / Math.Max(1, cols);
                foreach (var row in rows)
                {
                    sb.Append(@"\trowd\trgaph100");
                    for (var c = 1; c <= cols; c++)
                        sb.Append(@"\clbrdrt\brdrs\clbrdrl\brdrs\clbrdrb\brdrs\clbrdrr\brdrs").Append(row.IsHeader ? @"\clcbpat3" : string.Empty).Append(@"\cellx").Append(c * width);
                    foreach (var cell in row.OfType<TableCell>())
                    {
                        sb.Append(@"\pard\intbl ").Append(row.IsHeader ? @"\b " : string.Empty);
                        foreach (var child in cell.OfType<ParagraphBlock>()) Inlines(child.Inline, sb, o);
                        sb.Append(row.IsHeader ? @"\b0" : string.Empty).Append(@"\cell ");
                    }
                    for (var c = row.Count; c < cols; c++) sb.Append(@"\pard\intbl\cell ");
                    sb.Append(@"\row ");
                }
                sb.Append(@"\pard\sa120\par");
                break;
            }
            case ThematicBreakBlock:
                sb.Append(@"\pard\brdrb\brdrs\brdrw10\brsp20\par\pard");
                break;
            case HtmlBlock html:
                sb.Append(@"\pard ").Append(Esc(html.Lines.ToString())).Append(@"\par");
                break;
            case ContainerBlock c:
                foreach (var child in c) Block(child, sb, o, indent, null);
                break;
        }
    }

    private static void Inlines(ContainerInline? container, StringBuilder sb, ExportOptions o)
    {
        if (container == null) return;
        foreach (var inline in container)
        {
            switch (inline)
            {
                case LiteralInline lit: sb.Append(Esc(lit.Content.ToString())); break;
                case MathInline mi: WriteMath(mi.Content.ToString().Trim(), false, sb, o); break;
                case CodeInline code: sb.Append(@"{\f1\fs20\highlight3 ").Append(Esc(code.Content)).Append('}'); break;
                case EmphasisInline em:
                {
                    var tag = em.DelimiterChar == '~' && em.DelimiterCount == 2 ? @"\strike" : em.DelimiterCount >= 2 ? @"\b" : @"\i";
                    sb.Append('{').Append(tag).Append(' ');
                    Inlines(em, sb, o);
                    sb.Append('}');
                    break;
                }
                case LinkInline link when !link.IsImage && !string.IsNullOrEmpty(link.Url):
                    sb.Append(@"{\field{\*\fldinst{HYPERLINK """).Append(Esc(link.Url!)).Append(@"""}}{\fldrslt{\ul\cf4 ");
                    Inlines(link, sb, o);
                    if (link.FirstChild == null) sb.Append(Esc(link.Url!));
                    sb.Append("}}}");
                    break;
                case AutolinkInline auto:
                    sb.Append(@"{\field{\*\fldinst{HYPERLINK """).Append(Esc(auto.Url)).Append(@"""}}{\fldrslt{\ul\cf4 ").Append(Esc(auto.Url)).Append("}}}");
                    break;
                case LineBreakInline lb: sb.Append(lb.IsHard ? @"\line " : " "); break;
                case HtmlEntityInline he: sb.Append(Esc(he.Transcoded.ToString())); break;
                case HtmlInline html: sb.Append(html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase) ? @"\line " : Esc(html.Tag)); break;
                case TaskList task: sb.Append(Esc(task.Checked ? "☑ " : "☐ ")); break;
                case ContainerInline c: Inlines(c, sb, o); break;
            }
        }
    }

    private static void WriteMath(string latex, bool display, StringBuilder sb, ExportOptions o)
    {
        var png = o.RenderMath?.Invoke(latex, display);
        if (png is { } p)
        {
            sb.Append(PictureHex(p.png, p.width, p.height, "pngblip"));
            sb.Append(@"{\v ").Append(Esc(" $" + latex + "$ ")).Append('}'); // исходный LaTeX скрытым текстом
            return;
        }
        sb.Append(@"{\f3\i ").Append(Esc(LatexMath.ToPlainText(latex))).Append('}');
    }

    private static string Picture(ChatAttachment a)
    {
        byte[] data;
        try { data = Convert.FromBase64String(a.Data); }
        catch (FormatException) { return Esc("[" + a.Name + "]"); }
        var (w, h) = ImageSize.Read(data) ?? (640, 480);
        return PictureHex(data, w, h, a.MimeType.Contains("png") ? "pngblip" : "jpegblip");
    }

    private static string PictureHex(byte[] data, int w, int h, string kind)
    {
        // Размер в твипах (1 px = 15 twips при 96 dpi), не шире 6.5"
        var gw = w * 15;
        var gh = h * 15;
        if (gw > 9360)
        {
            gh = gh * 9360 / gw;
            gw = 9360;
        }
        var sb = new StringBuilder(data.Length * 2 + 128);
        sb.Append(@"{\*\shppict{\pict\").Append(kind).Append(@"\picw").Append(w).Append(@"\pich").Append(h)
          .Append(@"\picwgoal").Append(gw).Append(@"\pichgoal").Append(gh).Append(' ');
        const string hex = "0123456789abcdef";
        for (var i = 0; i < data.Length; i++)
        {
            sb.Append(hex[data[i] >> 4]).Append(hex[data[i] & 15]);
            if (i % 64 == 63) sb.Append('\n');
        }
        sb.Append("}}");
        return sb.ToString();
    }

    /// <summary>Экранирование RTF: служебные символы и всё вне ASCII через \uN? (UTF-16).</summary>
    public static string Esc(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\t': sb.Append(@"\tab "); break;
                case '\n': sb.Append(@"\line "); break;
                case '\r': break;
                default:
                    if (ch < 0x20) break;
                    if (ch < 0x80) sb.Append(ch);
                    else sb.Append(@"\u").Append((short)ch).Append('?');
                    break;
            }
        }
        return sb.ToString();
    }
}
