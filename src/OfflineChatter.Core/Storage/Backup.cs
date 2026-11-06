using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OfflineChatter.Core.Storage;

public sealed record BackupInfo(DateTime Created, string AppVersion, int ConfigFiles, int ChatFiles, bool MasterPassword);

/// <summary>
/// Резервная копия настроек (config/: settings, профили, провайдеры, MCP, пресеты промптов, сертификаты) и, по желанию, чатов.
/// Секреты в копии остаются зашифрованными: DPAPI-ключи на другом ПК не расшифруются (их нужно будет ввести заново),
/// с мастер-паролем — расшифруются тем же паролем.
/// </summary>
public static class Backup
{
    public const string Format = "offline-chatter-backup";
    private const string ManifestName = "backup.json";

    public static BackupInfo Create(AppPaths paths, string zipPath, bool includeChats, string appVersion)
    {
        var tmp = zipPath + ".tmp";
        if (File.Exists(tmp)) File.Delete(tmp);
        int config, chats = 0;
        using (var zip = ZipFile.Open(tmp, ZipArchiveMode.Create))
        {
            config = AddFolder(zip, paths.Config, "config");
            if (includeChats) chats = AddFolder(zip, paths.Chats, "chats");
            var info = new BackupInfo(DateTime.UtcNow, appVersion, config, chats, Secrets.Mode == SecretsMode.MasterPassword);
            var manifest = JObject.FromObject(info);
            manifest["format"] = Format;
            manifest["version"] = 1;
            using var w = new StreamWriter(zip.CreateEntry(ManifestName).Open());
            w.Write(manifest.ToString(Formatting.Indented));
        }
        if (File.Exists(zipPath)) File.Delete(zipPath);
        File.Move(tmp, zipPath);
        return new BackupInfo(DateTime.UtcNow, appVersion, config, chats, Secrets.Mode == SecretsMode.MasterPassword);
    }

    private static int AddFolder(ZipArchive zip, string dir, string prefix)
    {
        if (!Directory.Exists(dir)) return 0;
        var n = 0;
        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(f);
            // временные файлы атомарной записи и отложенные повреждённые копии не нужны
            if (name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase) || name.IndexOf(".corrupt-", StringComparison.OrdinalIgnoreCase) >= 0) continue;
            var rel = f.Substring(dir.Length).TrimStart('\\', '/').Replace('\\', '/');
            try
            {
                zip.CreateEntryFromFile(f, prefix + "/" + rel, CompressionLevel.Optimal);
                n++;
            }
            catch (IOException ex)
            {
                Diagnostics.Log.Warn("Backup", "Skipped locked file " + f, ex);
            }
        }
        return n;
    }

    /// <summary>Читает сведения о копии, не распаковывая её. Бросает InvalidDataException для чужих архивов.</summary>
    public static BackupInfo Inspect(string zipPath)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        return ReadManifest(zip);
    }

    private static BackupInfo ReadManifest(ZipArchive zip)
    {
        var entry = zip.GetEntry(ManifestName) ?? throw new InvalidDataException("Not an Offline Chatter backup");
        using var r = new StreamReader(entry.Open());
        var j = JObject.Parse(r.ReadToEnd());
        if ((string?)j["format"] != Format) throw new InvalidDataException("Not an Offline Chatter backup");
        return new BackupInfo(
            (DateTime?)j["Created"] ?? DateTime.MinValue,
            (string?)j["AppVersion"] ?? "?",
            (int?)j["ConfigFiles"] ?? 0,
            (int?)j["ChatFiles"] ?? 0,
            (bool?)j["MasterPassword"] ?? false);
    }

    /// <summary>
    /// Восстанавливает config/ и chats/ из копии. Перед этим текущие данные сохраняются в cache/backups
    /// (возвращается путь к этой страховочной копии). После восстановления программу нужно перезапустить.
    /// </summary>
    public static string Restore(AppPaths paths, string zipPath, bool restoreChats, string appVersion)
    {
        using var zip = ZipFile.OpenRead(zipPath);
        ReadManifest(zip);

        var safetyDir = Path.Combine(paths.Cache, "backups");
        Directory.CreateDirectory(safetyDir);
        var safety = Path.Combine(safetyDir, "before-restore-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".zip");
        Create(paths, safety, includeChats: restoreChats, appVersion);

        var root = Path.GetFullPath(paths.Root).TrimEnd('\\') + "\\";
        foreach (var e in zip.Entries)
        {
            if (string.IsNullOrEmpty(e.Name)) continue; // папка
            var isConfig = e.FullName.StartsWith("config/", StringComparison.Ordinal);
            var isChat = e.FullName.StartsWith("chats/", StringComparison.Ordinal);
            if (!isConfig && !(isChat && restoreChats)) continue;
            var target = Path.GetFullPath(Path.Combine(paths.Root, e.FullName.Replace('/', '\\')));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue; // zip-slip
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            var tmp = target + ".restore.tmp";
            e.ExtractToFile(tmp, overwrite: true);
            if (File.Exists(target)) File.Replace(tmp, target, null, ignoreMetadataErrors: true);
            else File.Move(tmp, target);
        }
        return safety;
    }

    public static IReadOnlyList<string> ListSafetyCopies(AppPaths paths)
    {
        var dir = Path.Combine(paths.Cache, "backups");
        return Directory.Exists(dir) ? Directory.GetFiles(dir, "*.zip").OrderByDescending(f => f).ToList() : new List<string>();
    }
}
