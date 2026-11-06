using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OfflineChatter.Core.Diagnostics;

/// <summary>
/// Сторонние утилиты, которые принудительно меняют заголовок и подложку всех окон (Mica For Everyone).
/// Они переписывают атрибуты DWM на каждое событие окна — программа возвращает свою тему, и окно «моргает».
/// Лечится правилом-исключением в самой утилите.
/// </summary>
public static class ExternalThemeTools
{
    public const string MicaForEveryoneProcess = "MicaForEveryone.App";
    private const string MicaForEveryonePackagePrefix = "DongleInsovaki.MicaForEveryone_";

    /// <summary>Запущена ли Mica For Everyone (v2: MicaForEveryone.App, v1: MicaForEveryone).</summary>
    public static bool IsMicaForEveryoneRunning()
    {
        try
        {
            return Process.GetProcessesByName(MicaForEveryoneProcess).Length > 0 || Process.GetProcessesByName("MicaForEveryone").Length > 0;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Файл правил Mica For Everyone 2.x (магазинная версия) или null.</summary>
    public static string? MicaForEveryoneSettingsPath(string? localAppData = null)
    {
        var packages = Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Packages");
        if (!Directory.Exists(packages)) return null;
        return Directory.EnumerateDirectories(packages, MicaForEveryonePackagePrefix + "*")
            .Select(d => Path.Combine(d, "LocalState", "settings.json"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>Есть ли правило для процесса (любое — пользователь мог настроить его сам).</summary>
    public static bool HasProcessRule(string settingsJson, string processName) =>
        Rules(JObject.Parse(settingsJson)).Any(r => IsProcessRule(r, processName));

    /// <summary>
    /// Добавляет правило «ничего не менять» для процесса: заголовок и подложка — Default, как исключение для Блокнота
    /// в настройках по умолчанию. Если правило уже есть, возвращает JSON без изменений.
    /// </summary>
    public static string AddExclusion(string settingsJson, string processName)
    {
        var root = JObject.Parse(settingsJson);
        if (root["rules"] is not JArray rules)
        {
            rules = new JArray();
            root["rules"] = rules;
        }
        if (rules.OfType<JObject>().Any(r => IsProcessRule(r, processName))) return settingsJson;
        rules.Add(new JObject
        {
            ["type"] = "process",
            ["processName"] = processName,
            ["priority"] = 1,
            ["titleBarColor"] = "Default",
            ["backdropPreference"] = "Default",
            ["cornerPreference"] = "Default",
            ["extendFrameIntoClientArea"] = false,
            ["enableBlurBehind"] = false,
            ["titleBarColorCode"] = null,
        });
        return root.ToString(Formatting.None);
    }

    /// <summary>Добавить исключение в файл правил (с резервной копией *.bak). Возвращает false, если оно уже было.</summary>
    public static bool AddExclusionToFile(string path, string processName)
    {
        var json = File.ReadAllText(path);
        var updated = AddExclusion(json, processName);
        if (updated == json) return false;
        File.Copy(path, path + ".bak", overwrite: true);
        Storage.JsonFile.WriteAllTextAtomic(path, updated);
        Log.Info("Theme", "Mica For Everyone exclusion added", new { path, processName });
        return true;
    }

    private static System.Collections.Generic.IEnumerable<JObject> Rules(JObject root) =>
        (root["rules"] as JArray ?? new JArray()).OfType<JObject>();

    private static bool IsProcessRule(JObject r, string processName) =>
        string.Equals((string?)r["type"], "process", StringComparison.OrdinalIgnoreCase)
        && string.Equals((string?)r["processName"], processName, StringComparison.OrdinalIgnoreCase);
}
