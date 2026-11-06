using System;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace OfflineChatter.Core.Net;

public sealed record UpdateInfo(bool Available, string LatestTag, string PageUrl, string? Notes);

/// <summary>
/// Проверка новой версии по GitHub Releases репозитория программы (owner/repo в настройках).
/// Только уведомление и ссылка: автозамена exe требует подписанных релизов (см. TODO.md).
/// </summary>
public static class UpdateChecker
{
    public static async Task<UpdateInfo> CheckAsync(HttpService http, string repo, Version current, CancellationToken ct)
    {
        var api = http.Settings.GitHubApiEndpoint.TrimEnd('/');
        var json = await http.GetJsonAsync($"{api}/repos/{repo.Trim().Trim('/')}/releases?per_page=1", ct).ConfigureAwait(false);
        var first = json is Newtonsoft.Json.Linq.JArray a && a.Count > 0 ? a[0] : null;
        if (first == null) return new UpdateInfo(false, string.Empty, $"https://github.com/{repo}/releases", null);
        var tag = (string?)first["tag_name"] ?? string.Empty;
        var page = (string?)first["html_url"] ?? $"https://github.com/{repo}/releases";
        var latest = ParseVersion(tag);
        return new UpdateInfo(latest != null && latest > current, tag, page, (string?)first["body"]);
    }

    /// <summary>"v1.2.3", "1.2", "release-1.2.3-beta" → Version.</summary>
    public static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }
}
