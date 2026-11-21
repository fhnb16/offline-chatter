using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Chat;

namespace OfflineChatter.Core.Mcp;

internal static class McpResultReader
{
    private const int MaxImages = 8;
    private const int MaxEncodedChars = 32 * 1024 * 1024;
    private static readonly Regex LocalPath = new(
        @"(?<![\w/\\])(?:file:///)?[a-z]:[/\\][^\r\n""<>|?*:\x60]{1,1024}?\.(?:png|jpe?g|gif|bmp|tiff?|webp)(?![\w.])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex DrivePath = new(@"^[a-zA-Z]:[/\\]");

    public static async Task<McpToolResult> ReadAsync(JObject response, bool allowFiles, string? cwd, CancellationToken ct)
    {
        var result = new McpToolResult { IsError = (bool?)response["isError"] ?? false };
        var text = new StringBuilder();
        var fileSources = new List<string>();
        var remaining = MaxEncodedChars;

        void AddImage(string? mime, string? data)
        {
            var ext = ImageExtension(mime);
            if (ext == null || string.IsNullOrEmpty(data) || data!.Length > remaining || result.Images.Count >= MaxImages) return;
            var image = new ChatAttachment { Kind = AttachmentKind.Image, Name = $"image-{result.Images.Count + 1}{ext}", MimeType = mime!, Data = data };
            result.Images.Add(image);
            remaining -= data.Length;
        }

        void AddText(string? value)
        {
            if (value == null) return;
            text.AppendLine(value);
            if (allowFiles) fileSources.AddRange(ExtractStrings(value));
        }

        foreach (var block in (response["content"] as JArray ?? new JArray()).OfType<JObject>())
        {
            switch ((string?)block["type"])
            {
                case "text":
                    AddText((string?)block["text"]);
                    break;
                case "image":
                    AddImage((string?)block["mimeType"] ?? "image/png", (string?)block["data"]);
                    text.AppendLine("[image]");
                    break;
                case "resource" when block["resource"] is JObject resource:
                    AddText((string?)resource["text"] ?? (string?)resource["uri"]);
                    if (resource["blob"] is JValue blob && ImageExtension((string?)resource["mimeType"]) != null)
                    {
                        AddImage((string?)resource["mimeType"], (string?)blob);
                        text.AppendLine("[image]");
                    }
                    break;
                case "resource_link":
                    AddText((string?)block["uri"]);
                    break;
                default:
                    text.AppendLine(block.ToString(Formatting.None));
                    break;
            }
        }

        if (response["structuredContent"] is { } structured)
        {
            if (text.Length == 0) text.Append(structured.ToString());
            if (allowFiles) fileSources.AddRange(StringValues(structured));
        }

        if (allowFiles)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in fileSources)
            {
                if (result.Images.Count >= MaxImages || remaining < 4) break;
                foreach (var candidate in FileCandidates(source))
                {
                    ct.ThrowIfCancellationRequested();
                    var path = ResolvePath(candidate, cwd);
                    if (path == null || !seen.Add(path)) continue;
                    var attachment = await ReadFileAsync(path, remaining, ct).ConfigureAwait(false);
                    if (attachment == null) continue;
                    result.Images.Add(attachment);
                    remaining -= attachment.Data.Length;
                    if (result.Images.Count >= MaxImages || remaining < 4) break;
                }
            }
        }
        result.Text = text.ToString().TrimEnd();
        return result;
    }

    private static IEnumerable<string> ExtractStrings(string text)
    {
        var first = text.TrimStart().FirstOrDefault();
        if (first is '{' or '[' or '"')
        {
            JToken? parsed = null;
            try { parsed = JToken.Parse(text); }
            catch (JsonException) { }
            if (parsed != null) return StringValues(parsed);
        }
        return new[] { text };
    }

    private static IEnumerable<string> StringValues(JToken token)
    {
        if (token is JValue { Value: string text })
            yield return text;
        foreach (var child in token.Children())
        foreach (var value in StringValues(child))
            yield return value;
    }

    private static IEnumerable<string> FileCandidates(string source)
    {
        var trimmed = source.Trim().Trim('"', '\u0027', '\u0060', '<', '>');
        if (trimmed.Length <= 1024 && trimmed.IndexOfAny(new[] { '\r', '\n' }) < 0) yield return trimmed;
        foreach (Match match in LocalPath.Matches(source)) yield return match.Value;
    }

    private static string? ResolvePath(string candidate, string? cwd)
    {
        try
        {
            if (candidate.StartsWith("file:", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(candidate, UriKind.Absolute, out var uri) || !uri.IsFile || uri.IsUnc || uri.Host.Length != 0) return null;
                candidate = uri.LocalPath;
            }
            if (!DrivePath.IsMatch(candidate))
            {
                if (cwd == null || !DrivePath.IsMatch(cwd) || Path.IsPathRooted(candidate) || candidate.Contains(":")) return null;
                candidate = Path.Combine(cwd, candidate);
            }
            if (!AttachmentReader.IsImage(candidate)) return null;
            var full = Path.GetFullPath(candidate);
            // Двоеточие после буквы диска обозначает альтернативный поток NTFS.
            if (full.IndexOf(':', 2) >= 0) return null;
            return full;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or IOException) { return null; }
    }

    private static async Task<ChatAttachment?> ReadFileAsync(string path, int remaining, CancellationToken ct)
    {
        try
        {
            if (new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Network) return null;
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 8192, useAsync: true);
            var size = stream.Length;
            if (size == 0 || size > remaining / 4 * 3) return null;
            var bytes = new byte[(int)size];
            var offset = 0;
            while (offset < bytes.Length)
            {
                var read = await stream.ReadAsync(bytes, offset, bytes.Length - offset, ct).ConfigureAwait(false);
                if (read == 0) return null;
                offset += read;
            }
            var mime = Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => "image/jpeg",
                ".tif" or ".tiff" => "image/tiff",
                var ext => "image/" + ext.Substring(1),
            };
            return new ChatAttachment { Kind = AttachmentKind.Image, Name = Path.GetFileName(path), MimeType = mime, Data = Convert.ToBase64String(bytes) };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) { return null; }
    }

    private static string? ImageExtension(string? mime) => mime?.ToLowerInvariant() switch
    {
        "image/png" => ".png",
        "image/jpeg" => ".jpg",
        "image/gif" => ".gif",
        "image/bmp" => ".bmp",
        "image/tiff" => ".tif",
        "image/webp" => ".webp",
        _ => null,
    };
}
