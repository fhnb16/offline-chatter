using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace OfflineChatter.Core.Storage;

/// <summary>
/// Где лежит программа и куда её предложить перенести. Архив обычно распаковывают в «Загрузки» или на рабочий стол,
/// а Windows иногда запускает exe прямо из временной папки архиватора — оттуда данные потеряются.
/// Стандартное место для программ пользователя без прав администратора — %LOCALAPPDATA%\Programs\OfflineChatter
/// (туда же ставит установщик).
/// </summary>
public static class InstallLocation
{
    public const string ProgramFolderName = "OfflineChatter";

    /// <summary>Папки данных, которые переносятся вместе с программой (логи и кэш создаются заново).</summary>
    public static readonly IReadOnlyList<string> DataFolders = new[] { "config", "chats", "models", "engine", "reports" };

    public static string DefaultTarget(string? localAppData = null) =>
        Path.Combine(localAppData ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", ProgramFolderName);

    /// <summary>Временная папка: архиватор распаковал exe «на минутку» — всё созданное рядом пропадёт.</summary>
    public static bool IsTemporary(string exeDir, string? tempPath = null)
    {
        var temp = Normalize(tempPath ?? Path.GetTempPath());
        return IsUnder(Normalize(exeDir), temp);
    }

    /// <summary>
    /// Предлагать ли перенос: программа запущена из «Загрузок», с рабочего стола или из временной папки
    /// и ещё не стоит в стандартном месте.
    /// </summary>
    public static bool ShouldOfferMove(string exeDir, string target, IEnumerable<string> transientRoots, string? tempPath = null)
    {
        var dir = Normalize(exeDir);
        if (IsUnder(dir, Normalize(target)) || string.Equals(dir, Normalize(target), StringComparison.OrdinalIgnoreCase)) return false;
        if (IsTemporary(exeDir, tempPath)) return true;
        return transientRoots.Where(r => !string.IsNullOrWhiteSpace(r)).Select(Normalize)
            .Any(r => string.Equals(dir, r, StringComparison.OrdinalIgnoreCase) || IsUnder(dir, r));
    }

    /// <summary>«Загрузки» и рабочий стол текущего пользователя.</summary>
    public static IReadOnlyList<string> DefaultTransientRoots()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            string.IsNullOrEmpty(profile) ? string.Empty : Path.Combine(profile, "Downloads"),
        };
    }

    /// <summary>
    /// Копирует программу и её данные в новую папку. Возвращает путь к новому exe.
    /// Логи и кэш не переносятся; файлы, которые уже есть в целевой папке, перезаписываются только exe и конфигом.
    /// </summary>
    public static string CopyTo(string sourceDir, string targetDir, string exeName, Action<string>? progress = null)
    {
        Directory.CreateDirectory(targetDir);
        foreach (var file in new[] { exeName, exeName + ".config", AppPaths.PortableFlagFileName })
        {
            var src = Path.Combine(sourceDir, file);
            if (File.Exists(src)) File.Copy(src, Path.Combine(targetDir, file), overwrite: true);
        }
        foreach (var folder in DataFolders)
        {
            var src = Path.Combine(sourceDir, folder);
            if (!Directory.Exists(src)) continue;
            progress?.Invoke(folder);
            CopyDirectory(src, Path.Combine(targetDir, folder));
        }
        return Path.Combine(targetDir, exeName);
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var dir in Directory.EnumerateDirectories(src, "*", SearchOption.AllDirectories))
            Directory.CreateDirectory(Path.Combine(dst, dir.Substring(src.Length).TrimStart('\\', '/')));
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name.EndsWith(".part", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(dst, file.Substring(src.Length).TrimStart('\\', '/'));
            if (!File.Exists(target)) File.Copy(file, target);
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path).TrimEnd('\\', '/');

    private static bool IsUnder(string path, string root) =>
        path.StartsWith(root + "\\", StringComparison.OrdinalIgnoreCase);
}
