using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Xml;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Export;
using OfflineChatter.Core.Formulas;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class LatexMathTests
{
    private static XmlDocument Xml(string s)
    {
        var d = new XmlDocument();
        d.LoadXml(s);
        return d;
    }

    [Theory]
    [InlineData(@"x = \frac{-b \pm \sqrt{b^2 - 4ac}}{2a}")]
    [InlineData(@"\sum_{i=1}^{n} i^2 = \frac{n(n+1)(2n+1)}{6}")]
    [InlineData(@"\int_0^\infty e^{-x^2}\,dx = \frac{\sqrt{\pi}}{2}")]
    [InlineData(@"\begin{pmatrix} a & b \\ c & d \end{pmatrix}")]
    [InlineData(@"f(x) = \begin{cases} x^2 & x \ge 0 \\ -x & x < 0 \end{cases}")]
    [InlineData(@"\left( \frac{a}{b} \right)^2 + \hat{y} + \vec{v} + \mathbb{R} + \text{if } x \in A")]
    [InlineData(@"\lim_{x \to 0} \frac{\sin x}{x} = 1, \quad \sqrt[3]{8} = 2, \alpha' \neq \beta")]
    [InlineData(@"\unknowncommand{x} + {")]
    public void MathML_And_Omml_Are_Well_Formed(string latex)
    {
        var mml = Xml(LatexMath.ToMathML(latex, true));
        Assert.Equal("math", mml.DocumentElement!.LocalName);
        Assert.Contains(latex.Trim(), mml.DocumentElement.InnerText); // LaTeX в аннотации

        var omml = LatexMath.ToOmml(latex, true);
        Xml($"<root xmlns:m=\"{LatexMath.OmmlNs}\">{omml}</root>");
    }

    [Fact]
    public void Structure_Is_Correct()
    {
        var mml = LatexMath.ToMathML(@"\frac{1}{2} + x^2 + \sum_{k=0}^{n} k", false);
        Assert.Contains("<mfrac><mn>1</mn><mn>2</mn></mfrac>", mml);
        Assert.Contains("<msup><mi>x</mi><mn>2</mn></msup>", mml);
        Assert.Contains("<munderover><mo largeop=\"true\">∑</mo>", mml);

        var omml = LatexMath.ToOmml(@"\sum_{k=0}^{n} k + \sqrt{x}", false);
        Assert.Contains("<m:nary><m:naryPr><m:chr m:val=\"∑\"/>", omml);
        Assert.Contains("<m:rad><m:radPr><m:degHide m:val=\"1\"/></m:radPr>", omml);

        Assert.Equal("(a)/(b)+√(x)", LatexMath.ToPlainText(@"\frac{a}{b} + \sqrt{x}").Replace(" ", ""));
    }
}

public class ExporterTests
{
    private static ExportDocument Doc()
    {
        var chat = new ChatSession { Title = "Тест <экспорта> & формул", SystemPrompt = "Будь краток." };
        chat.Append(new ChatMessage
        {
            Role = ChatRole.User,
            Content = "Реши \\(x^2=4\\)",
            Attachments = { new ChatAttachment { Kind = AttachmentKind.Image, Name = "a.png", MimeType = "image/png", Data = TinyPng } },
        });
        chat.Append(new ChatMessage
        {
            Role = ChatRole.Assistant,
            Reasoning = "Корни квадратного уравнения.",
            Content = "## Ответ\n\n$$x = \\pm 2$$\n\n- **Плюс**: $x=2$\n- *Минус*: `x=-2`\n\n| a | b |\n|---|---|\n| 1 | $\\frac12$ |\n\n```python\nprint(\"hi\")\n```\n\n> цитата [ссылка](https://example.com)",
        });
        return ExportDocument.FromSession(chat);
    }

    // PNG 1×1
    private const string TinyPng = "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNk+M9QDwADhgGAWjR9awAAAABJRU5ErkJggg==";

    [Fact]
    public void Markdown_Keeps_Source_And_Labels()
    {
        var md = MarkdownExporter.Export(Doc(), new ExportOptions { UserLabel = "Вы", AssistantLabel = "Модель" });
        Assert.Contains("### Вы", md);
        Assert.Contains("$$x = \\pm 2$$", md);
        Assert.Contains("<details><summary>Reasoning</summary>", md);
        Assert.Contains("data:image/png;base64,", md);
    }

    [Fact]
    public void Html_Has_MathML_And_Escaping()
    {
        var html = HtmlExporter.Export(Doc(), new ExportOptions());
        Assert.Contains("<title>Тест &lt;экспорта&gt; &amp; формул</title>", html);
        Assert.Contains("<math xmlns=\"http://www.w3.org/1998/Math/MathML\" display=\"block\">", html);
        Assert.Contains("<math xmlns=\"http://www.w3.org/1998/Math/MathML\">", html); // инлайн из \( \)
        Assert.Contains("<table>", html);
        Assert.Contains("<img alt=\"a.png\" src=\"data:image/png;base64,", html);
        Assert.DoesNotContain("class=\"math\"", html);
    }

    [Fact]
    public void Docx_Is_A_Valid_Package()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("chat.docx");
        DocxExporter.Export(Doc(), new ExportOptions(), path);
        using var zip = ZipFile.OpenRead(path);
        foreach (var name in new[] { "[Content_Types].xml", "_rels/.rels", "word/document.xml", "word/styles.xml", "word/_rels/document.xml.rels", "docProps/core.xml", "word/media/image1.png" })
            Assert.NotNull(zip.GetEntry(name));

        var doc = new XmlDocument();
        using (var s = zip.GetEntry("word/document.xml")!.Open()) doc.Load(s);
        var ns = new XmlNamespaceManager(doc.NameTable);
        ns.AddNamespace("w", "http://schemas.openxmlformats.org/wordprocessingml/2006/main");
        ns.AddNamespace("m", LatexMath.OmmlNs);
        Assert.True(doc.SelectNodes("//m:oMathPara", ns)!.Count >= 1);
        Assert.True(doc.SelectNodes("//m:oMath", ns)!.Count >= 3);
        Assert.Single(doc.SelectNodes("//w:tbl", ns)!.Cast<XmlNode>());
        Assert.True(doc.SelectNodes("//w:hyperlink", ns)!.Count == 1);
        Assert.Contains(doc.SelectNodes("//w:pStyle/@w:val", ns)!.Cast<XmlNode>(), n => n.Value == "Code");

        var rels = new XmlDocument();
        using (var s = zip.GetEntry("word/_rels/document.xml.rels")!.Open()) rels.Load(s);
        Assert.Contains("https://example.com", rels.OuterXml);
        var styles = new XmlDocument();
        using (var s = zip.GetEntry("word/styles.xml")!.Open()) styles.Load(s);
    }

    [Fact]
    public void Rtf_Escapes_Unicode_And_Uses_Renderer()
    {
        var calls = 0;
        var rtf = RtfExporter.Export(Doc(), new ExportOptions
        {
            RenderMath = (latex, display) => { calls++; return (Convert.FromBase64String(TinyPng), 1, 1); },
        });
        Assert.StartsWith(@"{\rtf1", rtf);
        Assert.EndsWith("}", rtf);
        Assert.True(rtf.All(c => c < 128), "RTF must be pure ASCII");
        Assert.Contains("\\" + "u1058?", rtf); // 'Т' (U+0422) как \uN?
        Assert.Contains(@"\pngblip", rtf);
        Assert.True(calls >= 3);
        Assert.Equal(rtf.Count(c => c == '{') - rtf.Count(c => c == '}'), CountEscapedBraceBalance(rtf));
    }

    /// <summary>Баланс фигурных скобок без учёта экранированных \{ \}.</summary>
    private static int CountEscapedBraceBalance(string rtf) => rtf.Split(new[] { @"\{" }, StringSplitOptions.None).Length - rtf.Split(new[] { @"\}" }, StringSplitOptions.None).Length;

    /// <summary>OC_DUMP=папка — сохранить примеры экспорта для ручной проверки (Word, LibreOffice, браузер).</summary>
    [Fact]
    public void Dump_Samples()
    {
        var dir = Environment.GetEnvironmentVariable("OC_DUMP");
        if (string.IsNullOrEmpty(dir)) return;
        Directory.CreateDirectory(dir);
        var d = Doc();
        File.WriteAllText(Path.Combine(dir, "sample.md"), MarkdownExporter.Export(d, new ExportOptions()));
        File.WriteAllText(Path.Combine(dir, "sample.html"), HtmlExporter.Export(d, new ExportOptions()));
        File.WriteAllText(Path.Combine(dir, "sample.rtf"), RtfExporter.Export(d, new ExportOptions()));
        DocxExporter.Export(d, new ExportOptions(), Path.Combine(dir, "sample.docx"));
    }

    [Fact]
    public void Image_Size_From_Header()
    {
        Assert.Equal((1, 1), ImageSize.Read(Convert.FromBase64String(TinyPng)));
    }
}
