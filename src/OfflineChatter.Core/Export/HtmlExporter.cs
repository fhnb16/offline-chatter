using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Markdig;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Formulas;
using OfflineChatter.Core.Text;

namespace OfflineChatter.Core.Export;

/// <summary>
/// Самодостаточная HTML-страница: стили встроены, изображения — data URI, формулы — MathML
/// (поддерживается Chrome 109+, Firefox, Safari) с исходным LaTeX в аннотации.
/// </summary>
public static class HtmlExporter
{
    private static readonly Regex InlineMath = new(@"<span class=""math"">\\\((?<tex>[\s\S]*?)\\\)</span>", RegexOptions.Compiled);
    private static readonly Regex BlockMath = new(@"<div class=""math"">\s*\\\[(?<tex>[\s\S]*?)\\\]\s*</div>", RegexOptions.Compiled);

    public static string RenderMarkdown(string markdown)
    {
        var html = Markdig.Markdown.ToHtml(MarkdownText.NormalizeMath(markdown), Exporter.Pipeline);
        html = BlockMath.Replace(html, m => "<div class=\"formula\">" + LatexMath.ToMathML(WebUtility.HtmlDecode(m.Groups["tex"].Value).Trim(), true) + "</div>");
        html = InlineMath.Replace(html, m => LatexMath.ToMathML(WebUtility.HtmlDecode(m.Groups["tex"].Value).Trim(), false));
        return html;
    }

    public static string Export(ExportDocument doc, ExportOptions o)
    {
        string E(string s) => WebUtility.HtmlEncode(s);
        var sb = new StringBuilder();
        sb.Append("<!doctype html><html><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width,initial-scale=1\">")
          .Append("<title>").Append(E(doc.Title)).Append("</title><style>")
          .Append(":root{--bg:#f6f6f6;--card:#fff;--text:#1b1b1b;--muted:#666;--accent:#0067c0;--user:#e8f1fb;--border:#e3e3e3;--code:#f3f3f3}")
          .Append(o.DarkHtml ? ":root{--bg:#1f1f1f;--card:#2b2b2b;--text:#eee;--muted:#aaa;--accent:#60cdff;--user:#1d3247;--border:#3a3a3a;--code:#232323}" : "@media (prefers-color-scheme:dark){:root{--bg:#1f1f1f;--card:#2b2b2b;--text:#eee;--muted:#aaa;--accent:#60cdff;--user:#1d3247;--border:#3a3a3a;--code:#232323}}")
          .Append("body{margin:0;background:var(--bg);color:var(--text);font:15px/1.6 'Segoe UI',system-ui,-apple-system,sans-serif}")
          .Append("main{max-width:860px;margin:0 auto;padding:32px 20px}h1{font-size:26px;margin:0 0 4px}.date{color:var(--muted);margin-bottom:24px}")
          .Append(".msg{background:var(--card);border:1px solid var(--border);border-radius:10px;padding:14px 18px;margin:0 0 14px}.msg.user{background:var(--user)}")
          .Append(".who{font-size:12px;font-weight:600;color:var(--muted);text-transform:uppercase;letter-spacing:.04em;margin-bottom:6px}")
          .Append("pre{background:var(--code);border:1px solid var(--border);border-radius:6px;padding:10px 12px;overflow:auto}code{font-family:'Cascadia Mono',Consolas,monospace;font-size:13px}")
          .Append(":not(pre)>code{background:var(--code);padding:1px 4px;border-radius:4px}table{border-collapse:collapse;margin:8px 0}th,td{border:1px solid var(--border);padding:5px 10px}")
          .Append("blockquote{margin:8px 0;padding:2px 12px;border-left:3px solid var(--accent);color:var(--muted)}a{color:var(--accent)}img{max-width:100%;border-radius:6px}")
          .Append(".formula{text-align:center;margin:8px 0;overflow-x:auto}math{font-size:1.1em}details{margin:0 0 8px;color:var(--muted)}summary{cursor:pointer}")
          .Append(".atts{display:flex;gap:8px;flex-wrap:wrap;margin-bottom:8px}.atts img{max-height:220px}")
          .Append("</style></head><body><main>");
        sb.Append("<h1>").Append(E(doc.Title)).Append("</h1><div class=\"date\">").Append(E(doc.Created.ToString("f"))).Append("</div>");
        foreach (var m in doc.Messages)
        {
            var cls = m.Role == ChatRole.User ? "msg user" : "msg";
            sb.Append("<section class=\"").Append(cls).Append("\"><div class=\"who\">").Append(E(ExportDocument.Label(m, o))).Append("</div>");
            if (o.IncludeImages && m.Attachments.Count > 0)
            {
                sb.Append("<div class=\"atts\">");
                foreach (var a in m.Attachments)
                {
                    if (a.Kind == AttachmentKind.Image) sb.Append("<img alt=\"").Append(E(a.Name)).Append("\" src=\"").Append(a.DataUri).Append("\">");
                    else sb.Append("<span>📎 ").Append(E(a.Name)).Append("</span>");
                }
                sb.Append("</div>");
            }
            if (o.IncludeReasoning && !string.IsNullOrWhiteSpace(m.Reasoning))
                sb.Append("<details><summary>").Append(E(o.ReasoningLabel)).Append("</summary>").Append(RenderMarkdown(m.Reasoning!)).Append("</details>");
            sb.Append(RenderMarkdown(m.Content)).Append("</section>");
        }
        sb.Append("<footer class=\"date\">Offline Chatter</footer></main></body></html>");
        return sb.ToString();
    }
}
