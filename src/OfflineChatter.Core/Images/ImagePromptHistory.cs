using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace OfflineChatter.Core.Images;

public sealed record ImagePromptEntry(string Prompt, string NegativePrompt)
{
    public static ImagePromptEntry? FromParams(ImageGenParams? p) =>
        p == null ? null : new ImagePromptEntry(p.Prompt, p.NegativePrompt);

    [JsonIgnore]
    public bool HasNegativePrompt => !string.IsNullOrWhiteSpace(NegativePrompt);

    [JsonIgnore]
    public string PromptPreview => OneLine(Prompt);

    [JsonIgnore]
    public string NegativePromptPreview => OneLine(NegativePrompt);

    private static string OneLine(string text) => text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');
}

public static class ImagePromptHistory
{
    public const int Limit = 40;

    /// <summary>Записи передаются от новых к старым. Совпадающая пара промптов занимает одну строку.</summary>
    public static List<ImagePromptEntry> Recent(IEnumerable<ImagePromptEntry?> entries) => entries
        .OfType<ImagePromptEntry>()
        .Where(entry => !string.IsNullOrWhiteSpace(entry.Prompt))
        .Select(entry => entry.NegativePrompt == null ? entry with { NegativePrompt = string.Empty } : entry)
        .Distinct()
        .Take(Limit)
        .ToList();
}
