using System.IO;
using System.IO.Compression;
using System.Text;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Text;
using Xunit;

namespace OfflineChatter.Core.Tests;

public class MarkdownTextTests
{
    [Theory]
    [InlineData(@"Euler: \(e^{i\pi}+1=0\).", "Euler: $e^{i\\pi}+1=0$.")]
    [InlineData("Display:\n\\[\n\\int_0^1 x\\,dx\n\\]", "Display:\n$$\n\\int_0^1 x\\,dx\n$$")]
    [InlineData("Code `\\(keep\\)` and \\(x\\)", "Code `\\(keep\\)` and $x$")]
    [InlineData("```\n\\(in fence\\)\n```\n\\(out\\)", "```\n\\(in fence\\)\n```\n$out$")]
    [InlineData("no math here", "no math here")]
    public void Normalizes_Math_Delimiters(string input, string expected) => Assert.Equal(expected, MarkdownText.NormalizeMath(input));

    [Fact]
    public void Stable_Boundary_Ignores_Open_Fences_And_Math()
    {
        Assert.Equal(0, MarkdownText.StableBoundary("partial line"));
        var a = "Para one.\n\nPara two in progress";
        Assert.Equal("Para one.\n\n".Length, MarkdownText.StableBoundary(a));

        var fence = "Intro\n\n```python\nx = 1\n\ny = 2\n";
        Assert.Equal("Intro\n\n".Length, MarkdownText.StableBoundary(fence)); // пустая строка внутри кода — не граница

        var math = "Text\n\n$$\na\n\nb\n";
        Assert.Equal("Text\n\n".Length, MarkdownText.StableBoundary(math));

        var closed = "Intro\n\n```\ncode\n```\n\nAfter";
        Assert.Equal("Intro\n\n```\ncode\n```\n\n".Length, MarkdownText.StableBoundary(closed));
    }
}

public class AttachmentReaderTests
{
    [Fact]
    public void Reads_Docx_Text()
    {
        using var tmp = new TempDir();
        var path = tmp.Combine("a.docx");
        using (var zip = ZipFile.Open(path, ZipArchiveMode.Create))
        {
            var e = zip.CreateEntry("word/document.xml");
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write("<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
                    "<w:p><w:r><w:t>Привет,</w:t></w:r><w:r><w:t xml:space=\"preserve\"> мир</w:t></w:r></w:p>" +
                    "<w:p><w:r><w:t>A</w:t><w:tab/><w:t>B</w:t></w:r></w:p></w:body></w:document>");
        }
        Assert.Equal("Привет, мир\nA\tB", AttachmentReader.ReadDocx(path));
        var att = AttachmentReader.ReadText(path)!;
        Assert.Equal(AttachmentKind.Text, att.Kind);
        Assert.Equal("a.docx", att.Name);
    }

    [Fact]
    public void Unknown_Extension_Is_Not_Text()
    {
        using var tmp = new TempDir();
        File.WriteAllBytes(tmp.Combine("x.bin"), new byte[] { 1, 2 });
        Assert.Null(AttachmentReader.ReadText(tmp.Combine("x.bin")));
        Assert.True(AttachmentReader.IsImage("photo.JPG"));
    }
}
