using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Security;
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
/// DOCX (Word 2007+) без OpenXML SDK: собираем пакет OOXML вручную (System.IO.Compression).
/// Стили, заголовки, списки, таблицы, код, ссылки, изображения; формулы — нативные OMML (редактируются в Word).
/// </summary>
public static class DocxExporter
{
    private const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    public static void Export(ExportDocument doc, ExportOptions o, string path)
    {
        var ctx = new Context();
        var body = new StringBuilder();
        Para(body, "Title", Esc(doc.Title));
        Para(body, "Subtitle", Esc(doc.Created.ToString("f")));

        foreach (var m in doc.Messages)
        {
            Para(body, m.Role == ChatRole.User ? "UserHeading" : "AssistantHeading", Esc(ExportDocument.Label(m, o)));
            foreach (var a in m.Attachments)
            {
                if (a.Kind == AttachmentKind.Image && o.IncludeImages) body.Append("<w:p>").Append(ctx.Image(a)).Append("</w:p>");
                else if (a.Kind == AttachmentKind.Text) Para(body, "Caption", Esc("📎 " + a.Name));
            }
            if (o.IncludeReasoning && !string.IsNullOrWhiteSpace(m.Reasoning))
            {
                Para(body, "Caption", Esc(o.ReasoningLabel));
                foreach (var line in m.Reasoning!.Trim().Split('\n')) Para(body, "Reasoning", Esc(line.TrimEnd('\r')));
            }
            WriteMarkdown(m.Content, body, ctx);
        }

        // Параметры страницы: A4, поля 2 см
        body.Append("<w:sectPr><w:pgSz w:w=\"11906\" w:h=\"16838\"/><w:pgMar w:top=\"1134\" w:right=\"1134\" w:bottom=\"1134\" w:left=\"1134\" w:header=\"709\" w:footer=\"709\" w:gutter=\"0\"/></w:sectPr>");

        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        Add(zip, "[Content_Types].xml", ContentTypes());
        Add(zip, "_rels/.rels",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/>" +
            "<Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/package/2006/relationships/metadata/core-properties\" Target=\"docProps/core.xml\"/></Relationships>");
        Add(zip, "docProps/core.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><cp:coreProperties xmlns:cp=\"http://schemas.openxmlformats.org/package/2006/metadata/core-properties\" xmlns:dc=\"http://purl.org/dc/elements/1.1/\" xmlns:dcterms=\"http://purl.org/dc/terms/\" xmlns:xsi=\"http://www.w3.org/2001/XMLSchema-instance\">" +
            "<dc:title>" + Esc(doc.Title) + "</dc:title><dc:creator>Offline Chatter</dc:creator><dcterms:created xsi:type=\"dcterms:W3CDTF\">" +
            doc.Created.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ") + "</dcterms:created></cp:coreProperties>");
        Add(zip, "word/styles.xml", Styles());
        Add(zip, "word/document.xml",
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"" + W + "\" xmlns:m=\"" + LatexMath.OmmlNs +
            "\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\" xmlns:wp=\"http://schemas.openxmlformats.org/drawingml/2006/wordprocessingDrawing\"" +
            " xmlns:a=\"http://schemas.openxmlformats.org/drawingml/2006/main\" xmlns:pic=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><w:body>" +
            body + "</w:body></w:document>");
        Add(zip, "word/_rels/document.xml.rels", ctx.Relationships());
        foreach (var (name, data) in ctx.Media)
        {
            var e = zip.CreateEntry("word/media/" + name, CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(data, 0, data.Length);
        }
    }

    // ---------------- Markdown → WordprocessingML ----------------

    private static void WriteMarkdown(string markdown, StringBuilder sb, Context ctx)
    {
        var doc = Markdig.Markdown.Parse(MarkdownText.NormalizeMath(markdown), Exporter.Pipeline);
        foreach (var b in doc) Block(b, sb, ctx, 0, null);
    }

    private static void Block(Block block, StringBuilder sb, Context ctx, int indent, string? listMarker)
    {
        switch (block)
        {
            case HeadingBlock h:
                Para(sb, "Heading" + Math.Min(3, h.Level), Inlines(h.Inline, ctx));
                break;
            case MathBlock mb:
                sb.Append("<w:p>").Append(LatexMath.ToOmml(mb.Lines.ToString().Trim(), true)).Append("</w:p>");
                break;
            case FencedCodeBlock or CodeBlock:
            {
                var code = ((LeafBlock)block).Lines.ToString().TrimEnd('\r', '\n');
                foreach (var line in code.Split('\n')) Para(sb, "Code", Esc(line.TrimEnd('\r')), preserve: true);
                break;
            }
            case ParagraphBlock p:
            {
                var content = Inlines(p.Inline, ctx);
                if (listMarker != null)
                {
                    content = Run(Esc(listMarker) + "\t") + content;
                    listMarker = null;
                }
                Para(sb, indent > 0 ? null : "Normal", content, indentTwips: indent);
                break;
            }
            case QuoteBlock q:
                foreach (var child in q)
                {
                    if (child is ParagraphBlock qp) Para(sb, "Quote", Inlines(qp.Inline, ctx));
                    else Block(child, sb, ctx, indent, null);
                }
                break;
            case ListBlock lb:
            {
                var n = int.TryParse(lb.OrderedStart, out var start) ? start : 1;
                foreach (var item in lb.OfType<ListItemBlock>())
                {
                    var marker = lb.IsOrdered ? n++ + "." : "•";
                    var first = true;
                    foreach (var child in item)
                    {
                        Block(child, sb, ctx, indent + 360, first ? marker : null);
                        first = false;
                    }
                }
                break;
            }
            case Table t:
                TableXml(t, sb, ctx);
                break;
            case ThematicBreakBlock:
                sb.Append("<w:p><w:pPr><w:pBdr><w:bottom w:val=\"single\" w:sz=\"6\" w:space=\"1\" w:color=\"BFBFBF\"/></w:pBdr></w:pPr></w:p>");
                break;
            case HtmlBlock html:
                Para(sb, "Normal", Esc(html.Lines.ToString()));
                break;
            case ContainerBlock c:
                foreach (var child in c) Block(child, sb, ctx, indent, null);
                break;
        }
    }

    private static void TableXml(Table t, StringBuilder sb, Context ctx)
    {
        sb.Append("<w:tbl><w:tblPr><w:tblStyle w:val=\"TableGrid\"/><w:tblW w:w=\"0\" w:type=\"auto\"/><w:tblBorders>");
        foreach (var side in new[] { "top", "left", "bottom", "right", "insideH", "insideV" })
            sb.Append("<w:").Append(side).Append(" w:val=\"single\" w:sz=\"4\" w:space=\"0\" w:color=\"BFBFBF\"/>");
        sb.Append("</w:tblBorders><w:tblCellMar><w:left w:w=\"100\" w:type=\"dxa\"/><w:right w:w=\"100\" w:type=\"dxa\"/></w:tblCellMar></w:tblPr>");
        foreach (var row in t.OfType<TableRow>())
        {
            sb.Append("<w:tr>");
            foreach (var cell in row.OfType<TableCell>())
            {
                sb.Append("<w:tc><w:tcPr>");
                if (row.IsHeader) sb.Append("<w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F2F2F2\"/>");
                sb.Append("</w:tcPr>");
                var any = false;
                foreach (var child in cell)
                {
                    if (child is ParagraphBlock p)
                    {
                        var content = Inlines(p.Inline, ctx);
                        if (row.IsHeader) content = content.Replace("<w:rPr>", "<w:rPr><w:b/>").Replace("<w:r><w:t", "<w:r><w:rPr><w:b/></w:rPr><w:t");
                        Para(sb, "TableText", content);
                        any = true;
                    }
                }
                if (!any) sb.Append("<w:p/>");
                sb.Append("</w:tc>");
            }
            sb.Append("</w:tr>");
        }
        sb.Append("</w:tbl><w:p/>");
    }

    private static string Inlines(ContainerInline? container, Context ctx, string rPr = "")
    {
        var sb = new StringBuilder();
        if (container == null) return string.Empty;
        foreach (var inline in container) InlineXml(inline, sb, ctx, rPr);
        return sb.ToString();
    }

    private static void InlineXml(Inline inline, StringBuilder sb, Context ctx, string rPr)
    {
        switch (inline)
        {
            case LiteralInline lit:
                sb.Append(Run(Esc(lit.Content.ToString()), rPr));
                break;
            case MathInline mi:
                sb.Append(LatexMath.ToOmml(mi.Content.ToString().Trim(), false));
                break;
            case CodeInline code:
                sb.Append(Run(Esc(code.Content), rPr + "<w:rStyle w:val=\"InlineCode\"/>"));
                break;
            case EmphasisInline em:
            {
                var add = em.DelimiterChar == '~' && em.DelimiterCount == 2 ? "<w:strike/>" : em.DelimiterCount >= 2 ? "<w:b/>" : "<w:i/>";
                foreach (var child in em) InlineXml(child, sb, ctx, rPr + add);
                break;
            }
            case LinkInline link when !link.IsImage && !string.IsNullOrEmpty(link.Url):
            {
                var id = ctx.Hyperlink(link.Url!);
                sb.Append("<w:hyperlink r:id=\"").Append(id).Append("\">");
                foreach (var child in link) InlineXml(child, sb, ctx, rPr + "<w:rStyle w:val=\"Hyperlink\"/>");
                if (link.FirstChild == null) sb.Append(Run(Esc(link.Url!), rPr + "<w:rStyle w:val=\"Hyperlink\"/>"));
                sb.Append("</w:hyperlink>");
                break;
            }
            case LinkInline img:
                sb.Append(Run(Esc("[" + (img.Url ?? "image") + "]"), rPr));
                break;
            case AutolinkInline auto:
                sb.Append("<w:hyperlink r:id=\"").Append(ctx.Hyperlink(auto.IsEmail ? "mailto:" + auto.Url : auto.Url)).Append("\">")
                  .Append(Run(Esc(auto.Url), rPr + "<w:rStyle w:val=\"Hyperlink\"/>")).Append("</w:hyperlink>");
                break;
            case LineBreakInline lb:
                sb.Append(lb.IsHard ? "<w:r><w:br/></w:r>" : Run(" ", rPr));
                break;
            case HtmlEntityInline he:
                sb.Append(Run(Esc(he.Transcoded.ToString()), rPr));
                break;
            case HtmlInline html:
                sb.Append(html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase) ? "<w:r><w:br/></w:r>" : Run(Esc(html.Tag), rPr));
                break;
            case TaskList task:
                sb.Append(Run(task.Checked ? "☑ " : "☐ ", rPr));
                break;
            case ContainerInline c:
                foreach (var child in c) InlineXml(child, sb, ctx, rPr);
                break;
        }
    }

    private static string Run(string escapedText, string rPr = "") =>
        "<w:r>" + (rPr.Length > 0 ? "<w:rPr>" + rPr + "</w:rPr>" : string.Empty) + "<w:t xml:space=\"preserve\">" + escapedText + "</w:t></w:r>";

    private static void Para(StringBuilder sb, string? style, string content, bool preserve = false, int indentTwips = 0)
    {
        sb.Append("<w:p>");
        if (style != null || indentTwips > 0)
        {
            sb.Append("<w:pPr>");
            if (style != null) sb.Append("<w:pStyle w:val=\"").Append(style).Append("\"/>");
            if (indentTwips > 0) sb.Append("<w:ind w:left=\"").Append(indentTwips + 360).Append("\" w:hanging=\"360\"/><w:spacing w:after=\"40\"/>");
            sb.Append("</w:pPr>");
        }
        sb.Append(content.StartsWith("<", StringComparison.Ordinal) ? content : Run(content));
        sb.Append("</w:p>");
    }

    private static string Esc(string s)
    {
        // Управляющие символы недопустимы в XML 1.0
        var clean = new StringBuilder(s.Length);
        foreach (var ch in s) if (ch >= 0x20 || ch is '\t' or '\n' or '\r') clean.Append(ch);
        return SecurityElement.Escape(clean.ToString()) ?? string.Empty;
    }

    private static void Add(ZipArchive zip, string name, string content)
    {
        var e = zip.CreateEntry(name, CompressionLevel.Optimal);
        using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
        w.Write(content);
    }

    private static string ContentTypes() =>
        "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\">" +
        "<Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/>" +
        "<Default Extension=\"png\" ContentType=\"image/png\"/><Default Extension=\"jpeg\" ContentType=\"image/jpeg\"/><Default Extension=\"gif\" ContentType=\"image/gif\"/>" +
        "<Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/>" +
        "<Override PartName=\"/word/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.styles+xml\"/>" +
        "<Override PartName=\"/docProps/core.xml\" ContentType=\"application/vnd.openxmlformats-package.core-properties+xml\"/></Types>";

    private static string Styles()
    {
        string P(string id, string name, string ppr, string rpr, string basedOn = "Normal") =>
            $"<w:style w:type=\"paragraph\" w:styleId=\"{id}\"><w:name w:val=\"{name}\"/><w:basedOn w:val=\"{basedOn}\"/><w:qFormat/><w:pPr>{ppr}</w:pPr><w:rPr>{rpr}</w:rPr></w:style>";
        return "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:styles xmlns:w=\"" + W + "\">" +
               "<w:docDefaults><w:rPrDefault><w:rPr><w:rFonts w:ascii=\"Calibri\" w:hAnsi=\"Calibri\" w:cs=\"Calibri\" w:eastAsia=\"Calibri\"/><w:sz w:val=\"22\"/><w:lang w:val=\"ru-RU\"/></w:rPr></w:rPrDefault>" +
               "<w:pPrDefault><w:pPr><w:spacing w:after=\"120\" w:line=\"276\" w:lineRule=\"auto\"/></w:pPr></w:pPrDefault></w:docDefaults>" +
               "<w:style w:type=\"paragraph\" w:default=\"1\" w:styleId=\"Normal\"><w:name w:val=\"Normal\"/><w:qFormat/></w:style>" +
               P("Title", "Title", "<w:spacing w:after=\"60\"/>", "<w:sz w:val=\"40\"/><w:b/>") +
               P("Subtitle", "Subtitle", "<w:spacing w:after=\"240\"/>", "<w:color w:val=\"7F7F7F\"/>") +
               P("Heading1", "heading 1", "<w:keepNext/><w:spacing w:before=\"240\" w:after=\"80\"/><w:outlineLvl w:val=\"0\"/>", "<w:b/><w:sz w:val=\"32\"/>") +
               P("Heading2", "heading 2", "<w:keepNext/><w:spacing w:before=\"200\" w:after=\"60\"/><w:outlineLvl w:val=\"1\"/>", "<w:b/><w:sz w:val=\"28\"/>") +
               P("Heading3", "heading 3", "<w:keepNext/><w:spacing w:before=\"160\" w:after=\"40\"/><w:outlineLvl w:val=\"2\"/>", "<w:b/><w:sz w:val=\"24\"/>") +
               P("UserHeading", "Chat User", "<w:keepNext/><w:spacing w:before=\"280\" w:after=\"60\"/><w:pBdr><w:top w:val=\"single\" w:sz=\"4\" w:space=\"4\" w:color=\"D9D9D9\"/></w:pBdr>", "<w:b/><w:color w:val=\"0067C0\"/><w:sz w:val=\"20\"/><w:caps/>") +
               P("AssistantHeading", "Chat Assistant", "<w:keepNext/><w:spacing w:before=\"280\" w:after=\"60\"/><w:pBdr><w:top w:val=\"single\" w:sz=\"4\" w:space=\"4\" w:color=\"D9D9D9\"/></w:pBdr>", "<w:b/><w:color w:val=\"7F7F7F\"/><w:sz w:val=\"20\"/><w:caps/>") +
               P("Caption", "Chat Caption", "<w:spacing w:after=\"40\"/>", "<w:i/><w:color w:val=\"7F7F7F\"/><w:sz w:val=\"18\"/>") +
               P("Reasoning", "Chat Reasoning", "<w:spacing w:after=\"0\"/><w:ind w:left=\"360\"/>", "<w:color w:val=\"7F7F7F\"/><w:sz w:val=\"18\"/>") +
               P("Quote", "Quote", "<w:ind w:left=\"360\"/><w:pBdr><w:left w:val=\"single\" w:sz=\"18\" w:space=\"8\" w:color=\"0067C0\"/></w:pBdr>", "<w:color w:val=\"595959\"/>") +
               P("Code", "Code", "<w:spacing w:after=\"0\" w:line=\"240\" w:lineRule=\"auto\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F3F3F3\"/>", "<w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"19\"/>") +
               P("TableText", "Table Text", "<w:spacing w:after=\"0\"/>", string.Empty) +
               "<w:style w:type=\"character\" w:styleId=\"InlineCode\"><w:name w:val=\"Inline Code\"/><w:rPr><w:rFonts w:ascii=\"Consolas\" w:hAnsi=\"Consolas\" w:cs=\"Consolas\"/><w:sz w:val=\"20\"/><w:shd w:val=\"clear\" w:color=\"auto\" w:fill=\"F3F3F3\"/></w:rPr></w:style>" +
               "<w:style w:type=\"character\" w:styleId=\"Hyperlink\"><w:name w:val=\"Hyperlink\"/><w:rPr><w:color w:val=\"0563C1\"/><w:u w:val=\"single\"/></w:rPr></w:style>" +
               "</w:styles>";
    }

    /// <summary>Связи документа: гиперссылки и картинки.</summary>
    private sealed class Context
    {
        private readonly StringBuilder _rels = new();
        private int _next = 1;
        public List<(string name, byte[] data)> Media { get; } = new();

        public string Hyperlink(string url)
        {
            var id = "rId" + _next++;
            _rels.Append("<Relationship Id=\"").Append(id).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/hyperlink\" Target=\"")
                 .Append(Esc(url)).Append("\" TargetMode=\"External\"/>");
            return id;
        }

        /// <summary>Картинка как встроенный DrawingML (ширина не больше 6.5 дюйма).</summary>
        public string Image(ChatAttachment a)
        {
            byte[] data;
            try { data = Convert.FromBase64String(a.Data); }
            catch (FormatException) { return Run(Esc("[" + a.Name + "]")); }
            var ext = a.MimeType.Contains("png") ? "png" : a.MimeType.Contains("gif") ? "gif" : "jpeg";
            var name = "image" + (Media.Count + 1) + "." + ext;
            Media.Add((name, data));
            var id = "rId" + _next++;
            _rels.Append("<Relationship Id=\"").Append(id).Append("\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/image\" Target=\"media/")
                 .Append(name).Append("\"/>");

            var (w, h) = ImageSize.Read(data) ?? (640, 480);
            const long emuPerPx = 9525;
            var cx = w * emuPerPx;
            var cy = h * emuPerPx;
            const long maxCx = 5943600; // 6.5"
            if (cx > maxCx)
            {
                cy = cy * maxCx / cx;
                cx = maxCx;
            }
            var n = Media.Count;
            return "<w:r><w:drawing><wp:inline distT=\"0\" distB=\"0\" distL=\"0\" distR=\"0\"><wp:extent cx=\"" + cx + "\" cy=\"" + cy + "\"/><wp:docPr id=\"" + n + "\" name=\"" + Esc(a.Name) + "\"/>" +
                   "<a:graphic><a:graphicData uri=\"http://schemas.openxmlformats.org/drawingml/2006/picture\"><pic:pic><pic:nvPicPr><pic:cNvPr id=\"" + n + "\" name=\"" + name + "\"/><pic:cNvPicPr/></pic:nvPicPr>" +
                   "<pic:blipFill><a:blip r:embed=\"" + id + "\"/><a:stretch><a:fillRect/></a:stretch></pic:blipFill><pic:spPr><a:xfrm><a:off x=\"0\" y=\"0\"/><a:ext cx=\"" + cx + "\" cy=\"" + cy + "\"/></a:xfrm>" +
                   "<a:prstGeom prst=\"rect\"><a:avLst/></a:prstGeom></pic:spPr></pic:pic></a:graphicData></a:graphic></wp:inline></w:drawing></w:r>";
        }

        public string Relationships() =>
            "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\">" +
            "<Relationship Id=\"rIdStyles\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/>" +
            _rels + "</Relationships>";
    }
}

/// <summary>Размер PNG/JPEG/GIF по заголовку (без декодирования).</summary>
public static class ImageSize
{
    public static (int w, int h)? Read(byte[] d)
    {
        if (d.Length > 24 && d[0] == 0x89 && d[1] == 0x50) // PNG
            return (BE(d, 16), BE(d, 20));
        if (d.Length > 10 && d[0] == 'G' && d[1] == 'I') // GIF
            return (d[6] | (d[7] << 8), d[8] | (d[9] << 8));
        if (d.Length > 4 && d[0] == 0xFF && d[1] == 0xD8) // JPEG: ищем SOFn
        {
            var i = 2;
            while (i + 9 < d.Length)
            {
                if (d[i] != 0xFF) { i++; continue; }
                var marker = d[i + 1];
                var len = (d[i + 2] << 8) | d[i + 3];
                if (marker is >= 0xC0 and <= 0xCF and not 0xC4 and not 0xC8 and not 0xCC)
                    return ((d[i + 7] << 8) | d[i + 8], (d[i + 5] << 8) | d[i + 6]);
                i += 2 + len;
            }
        }
        return null;
    }

    private static int BE(byte[] d, int o) => (d[o] << 24) | (d[o + 1] << 16) | (d[o + 2] << 8) | d[o + 3];
}
