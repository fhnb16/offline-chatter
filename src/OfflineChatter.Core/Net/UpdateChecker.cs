using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;

namespace OfflineChatter.Core.Net;

public sealed record UpdateInfo(bool Available, string LatestTag, string PageUrl, string? Notes, ReleaseAsset? Exe = null, ReleaseAsset? Config = null,
    ReleaseAsset? Package = null)
{
    /// <summary>
    /// Можно обновиться на месте: в релизе есть zip-архив программы (основной путь) или, в старых релизах, exe —
    /// с контрольной суммой SHA-256.
    /// </summary>
    public bool CanInstall => Available && (HasSha(Package) || HasSha(Exe));

    private static bool HasSha(ReleaseAsset? a) => a != null && !string.IsNullOrEmpty(a.Sha256);
}

/// <summary>
/// Обновление программы из GitHub Releases (fhnb16/offline-chatter по умолчанию).
/// В том же репозитории лежат Win7-сборки llama.cpp (теги llama-*) — они пропускаются.
/// Установка: скачивается zip-архив программы (а не голый exe — Windows 11 и антивирусы куда спокойнее к архивам),
/// проверяется SHA-256 (поле digest у ассета), из архива берутся exe и exe.config; работающий exe переименовывается в *.old
/// (Windows это позволяет), новый встаёт на его место, программа перезапускается, *.old удаляется при следующем старте.
/// </summary>
public static class UpdateChecker
{
    public const string ExeAssetName = "OfflineChatter.exe";

    /// <summary>
    /// Единственный источник релизов программы и Win7-сборок llama.cpp. Зашит в код намеренно:
    /// обновление подменяет exe, поэтому источник не должен меняться настройками или правкой config/settings.json.
    /// </summary>
    public const string Repo = "fhnb16/offline-chatter";

    public static string ReleasesPage => $"https://github.com/{Repo}/releases";

    /// <summary>
    /// Список релизов и контрольные суммы — только из официального API, без зеркала из настроек:
    /// иначе подменённое «зеркало» могло бы отдать чужой релиз вместе с подходящей суммой.
    /// Сам файл можно качать через префикс-зеркало: он сверяется с суммой из официального API.
    /// </summary>
    public const string OfficialApi = "https://api.github.com";

    public static async Task<UpdateInfo> CheckAsync(HttpService http, Version current, CancellationToken ct)
    {
        const string api = OfficialApi;
        const string repo = Repo;
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
        // Принимаем только файлы самого релиза в официальном репозитории
        var official = $"https://github.com/{repo}/releases/download/";
        parsed = parsed with { Assets = parsed.Assets.Where(a => a.DownloadUrl.StartsWith(official, StringComparison.OrdinalIgnoreCase)).ToList() };
        var exe = parsed.Assets.FirstOrDefault(a => a.Name.Equals(ExeAssetName, StringComparison.OrdinalIgnoreCase));
        var cfg = parsed.Assets.FirstOrDefault(a => a.Name.Equals(ExeAssetName + ".config", StringComparison.OrdinalIgnoreCase));
        var package = parsed.Assets.FirstOrDefault(IsPackage);
        return new UpdateInfo(best.ver! > current, best.tag, (string?)best.r["html_url"] ?? page, (string?)best.r["body"], exe, cfg, package);
    }

    /// <summary>Архив программы: OfflineChatter-0.1.42.zip (не установщик и не исходники).</summary>
    public static bool IsPackage(ReleaseAsset a) =>
        a.Name.StartsWith("OfflineChatter", StringComparison.OrdinalIgnoreCase)
        && a.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
        && a.Name.IndexOf("setup", StringComparison.OrdinalIgnoreCase) < 0
        && a.Name.IndexOf("source", StringComparison.OrdinalIgnoreCase) < 0;

    /// <summary>"v1.2.3", "1.2", "release-1.2.3-beta" → Version.</summary>
    public static Version? ParseVersion(string tag)
    {
        var m = Regex.Match(tag, @"(\d+)\.(\d+)(?:\.(\d+))?");
        if (!m.Success) return null;
        return new Version(int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value), m.Groups[3].Success ? int.Parse(m.Groups[3].Value) : 0);
    }

    /// <summary>Скачивает новую версию в cache/update. Возвращает пути к exe и конфигу или бросает исключение.</summary>
    public static async Task<(string exe, string? config)> DownloadAsync(DownloadManager downloads, UpdateInfo info, string cacheDir, Func<string, string> mirror)
    {
        if (!info.CanInstall) throw new InvalidOperationException("No installable asset");
        var dir = Path.Combine(cacheDir, "update", Sanitize(info.LatestTag));
        Directory.CreateDirectory(dir);

        if (info.Package is { Sha256: { Length: > 0 } } pkg)
        {
            var zip = Path.Combine(dir, pkg.Name);
            var job = downloads.Enqueue(new DownloadJob(mirror(pkg.DownloadUrl), zip, "Offline Chatter " + info.LatestTag)
            {
                ExpectedSize = pkg.Size,
                ExpectedSha256 = pkg.Sha256,
                Group = "app-update",
            });
            if (!await job.WhenFinished.ConfigureAwait(false)) throw new IOException(job.Error ?? "Download failed");
            return ExtractPackage(zip, Path.Combine(dir, "extracted"));
        }

        // Старые релизы без архива: отдельные exe и exe.config
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

    /// <summary>
    /// Достаёт из архива OfflineChatter.exe и OfflineChatter.exe.config (в архиве они лежат в папке OfflineChatter/).
    /// Остальное содержимое архива (пустые папки models/, engine/ с подсказками) при обновлении не нужно.
    /// </summary>
    public static (string exe, string? config) ExtractPackage(string zipPath, string targetDir)
    {
        Directory.CreateDirectory(targetDir);
        using var zip = System.IO.Compression.ZipFile.OpenRead(zipPath);
        System.IO.Compression.ZipArchiveEntry? Find(string name) => zip.Entries
            .Where(e => e.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
            .OrderBy(e => e.FullName.Length)
            .FirstOrDefault();

        var exeEntry = Find(ExeAssetName) ?? throw new InvalidDataException("The archive has no " + ExeAssetName);
        var exe = Path.Combine(targetDir, ExeAssetName);
        exeEntry.ExtractToFile(exe, overwrite: true);
        string? config = null;
        if (Find(ExeAssetName + ".config") is { } cfgEntry)
        {
            config = Path.Combine(targetDir, ExeAssetName + ".config");
            cfgEntry.ExtractToFile(config, overwrite: true);
        }
        return (exe, config);
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
