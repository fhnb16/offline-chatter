using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Xml;

namespace OfflineChatter.Core.Chat;

/// <summary>Чтение текстовых вложений: обычный текст/код и DOCX (word/document.xml без сторонних библиотек).</summary>
public static class AttachmentReader
{
    public const long MaxTextBytes = 2 * 1024 * 1024;

    private static readonly string[] TextExtensions =
    {
        ".txt", ".md", ".markdown", ".csv", ".tsv", ".json", ".xml", ".yaml", ".yml", ".ini", ".toml", ".log", ".html", ".htm", ".css",
        ".cs", ".vb", ".fs", ".c", ".h", ".cpp", ".hpp", ".cc", ".java", ".kt", ".py", ".js", ".ts", ".tsx", ".jsx", ".go", ".rs",
        ".rb", ".php", ".swift", ".sql", ".sh", ".bat", ".cmd", ".ps1", ".lua", ".r", ".m", ".tex", ".xaml", ".csproj", ".gradle",
    };

    public static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".gif", ".webp", ".tif", ".tiff" };

    public static bool IsImage(string path) => ImageExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsText(string path) => TextExtensions.Contains(Path.GetExtension(path).ToLowerInvariant());

    public static bool IsDocx(string path) => Path.GetExtension(path).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    /// <summary>Текстовое вложение из файла (null, если формат не поддерживается).</summary>
    public static ChatAttachment? ReadText(string path)
    {
        string? content = null;
        if (IsDocx(path)) content = ReadDocx(path);
        else if (IsText(path))
        {
            var info = new FileInfo(path);
            if (info.Length > MaxTextBytes) throw new IOException($"File is too large ({info.Length / 1024} KB)");
            content = File.ReadAllText(path, Encoding.UTF8);
        }
        return content == null ? null : new ChatAttachment { Kind = AttachmentKind.Text, Name = Path.GetFileName(path), MimeType = "text/plain", Data = content };
    }

    /// <summary>Текст DOCX: абзацы через перевод строки, табуляции и разрывы сохраняются.</summary>
    public static string ReadDocx(string path)
    {
        using var zip = ZipFile.OpenRead(path);
        var entry = zip.GetEntry("word/document.xml") ?? throw new InvalidDataException("Not a DOCX file");
        using var s = entry.Open();
        using var reader = XmlReader.Create(s, new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null });
        const string w = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        var sb = new StringBuilder();
        // ReadElementContentAsString уже переводит читатель на следующий узел — его нельзя пропустить через Read()
        var advanced = false;
        while (advanced || reader.Read())
        {
            advanced = false;
            if (reader.NodeType == XmlNodeType.Element && reader.NamespaceURI == w)
            {
                switch (reader.LocalName)
                {
                    case "t":
                        sb.Append(reader.ReadElementContentAsString());
                        advanced = true;
                        break;
                    case "tab":
                        sb.Append('\t');
                        break;
                    case "br":
                    case "cr":
                        sb.Append('\n');
                        break;
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement && reader.NamespaceURI == w && reader.LocalName == "p")
            {
                sb.Append('\n');
            }
        }
        return sb.ToString().TrimEnd();
    }
}
