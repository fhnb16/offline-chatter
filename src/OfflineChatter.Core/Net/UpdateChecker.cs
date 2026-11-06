using System;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;

namespace OfflineChatter.Core.Net;

public sealed record UpdateInfo(bool Available, string LatestTag, string PageUrl, string? Notes, ReleaseAsset? Exe = null, ReleaseAsset? Config = null)
{
    /// <summary>Можно обновиться на месте: в релизе есть exe с контрольной суммой.</summary>
    public bool CanInstall => Available && Exe != null && !string.IsNullOrEmpty(Exe.Sha256);
}

/// <summary>
/// Обновление программы из GitHub Releases (fhnb16/offline-chatter по умолчанию).
/// В том же репозитории лежат Win7-сборки llama.cpp (теги llama-*) — они пропускаются.
/// Установка: exe скачивается с проверкой SHA-256 (поле digest у ассета), работающий exe переименовывается в *.old
/// (Windows это позволяет), новый встаёт на его место, программа перезапускается, *.old удаляется при следующем старте.
/// </summary>
public static class UpdateChecker
{
    public const string ExeAssetName = "OfflineChatter.exe";

    public static async Task<UpdateInfo> CheckAsync(HttpService http, string repo, Version current, CancellationToken ct)
    {
        var api = http.Settings.GitHubApiEndpoint.TrimEnd('/');
        repo = repo.Trim().Trim('/');
        var json = await http.GetJsonAsync($"{api}/repos/{repo}/releases?per_page=20", ct).ConfigureAwait(false);
        return Pick(json as JArray ?? new JArray(), repo, current);
    }

    internal static UpdateInfo Pick(JArray releases, string repo, Version current)
    {
        var page = $"https://github.com/{repo}/releases";
        var best = releases.OfType<JObject>()
            .Where(r => (bool?)r["draft"] != true)
            .Select(r => (r, tag: (string?)r["tag_name"] ?? string.Empty))
            .Where(x => !x.tag.StartsWith("llama-", StringComparison.OrdinalIgnoreCase))
            .Select(x => (x.r, x.tag, ver: ParseVersion(x.tag)))
            .Where(x => x.ver != null)
            .OrderByDescending(x => x.ver)
            .FirstOrDefault();
        if (best.r == null) return new UpdateInfo(false, string.Empty, page, null);

        var parsed = GitHubReleases.Parse(best.r);
        var exe = parsed.Assets.FirstOrDefault(a => a.Name.Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase));
        var cfg = parsed.Assets.FirstOrDefault(a => a.Name.Equals(ExeAssetName + ".config", StringComparison.OrdinalIgnoreCase));
        return new UpdateInfo(best.ver! > current, best.tag, (string?)best.r["html_url"] ?? page, (string?)best.r["body"], exe, cfg);
    }

    /// <summary>"v1.2.3", "1.2", "release-1.2.3-beta" → Version.</summary>
    public static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>Скачивает новую версию в cache/update. Возвращает путь к exe или бросает исключение.</summary>
    public static async Task<(string exe, string? config)> DownloadAsync(DownloadManager downloads, UpdateInfo info, string cacheDir, Func<string, string> mirror)
    {
        if (!info.CanInstall) throw new InvalidOperationException("No installable asset");
        var dir = Path.Combine(cacheDir, "update", Sanitize(info.LatestTag));
        Directory.CreateDirectory(dir);

        var exePath = Path.Combine(dir, ExeAssetName);
        var exeJob = downloads.Enqueue(new DownloadJob(mirror(info.Exe!.DownloadUrl), exePath, "Offline Chatter " + info.LatestTag)
        {
            ExpectedSize = info.Exe.Size,
            ExpectedSha256 = info.Exe.Sha256,
            Group = "app-update",
        });
        string? cfgPath = null;
        DownloadJob? cfgJob = null;
        if (info.Config != null)
        {
            cfgPath = Path.Combine(dir, ExeAssetName + ".config");
            cfgJob = downloads.Enqueue(new DownloadJob(mirror(info.Config.DownloadUrl), cfgPath, ExeAssetName + ".config")
            {
                ExpectedSize = info.Config.Size,
                ExpectedSha256 = info.Config.Sha256,
                Group = "app-update",
            });
        }
        if (!await exeJob.WhenFinished.ConfigureAwait(false)) throw new IOException(exeJob.Error ?? "Download failed");
        if (cfgJob != null && !await cfgJob.WhenFinished.ConfigureAwait(false)) cfgPath = null;
        return (exePath, cfgPath);
    }

    private static string Sanitize(string s) => string.Concat(s.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));

    /// <summary>
    /// Подменяет exe (и конфиг) новой версией. Работающий exe переименовывается в *.old.
    /// При ошибке всё возвращается как было.
    /// </summary>
    public static void Swap(string currentExe, string newExe, string? newConfig)
    {
        var old = currentExe + ".old";
        if (File.Exists(old)) File.Delete(old);
        File.Move(currentExe, old);
        try
        {
            File.Copy(newExe, currentExe, overwrite: false);
        }
        catch
        {
            File.Move(old, currentExe);
            throw;
        }

        if (newConfig != null && File.Exists(newConfig))
        {
            var cfg = currentExe + ".config";
            try
            {
                if (File.Exists(cfg)) File.Copy(cfg, cfg + ".old", overwrite: true);
                File.Copy(newConfig, cfg, overwrite: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Warn("Update", "Cannot replace exe.config", ex);
            }
        }
        Log.Info("Update", "Executable replaced", new { exe = currentExe });
    }

    /// <summary>Удаляет *.old после успешного перезапуска.</summary>
    public static void CleanupOld(string currentExe)
    {
        foreach (var f in new[] { currentExe + ".old", currentExe + ".config.old" })
        {
            try { if (File.Exists(f)) File.Delete(f); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* старый процесс ещё не вышел — удалим в следующий раз */ }
        }
    }
}
