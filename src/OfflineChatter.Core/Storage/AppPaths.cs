using System;
using System.IO;

namespace OfflineChatter.Core.Storage;

/// <summary>
/// Все пути приложения. Нигде больше не собирать пути вручную и не использовать CurrentDirectory.
/// </summary>
public sealed class AppPaths
{
    public const string PortableFlagFileName = "portable.flag";
    public const string AppFolderName = "OfflineChatter";

    public AppPaths(string root, bool isPortable)
    {
        if (string.IsNullOrWhiteSpace(root)) throw new ArgumentException("Root is empty.", nameof(root));
        Root = Path.GetFullPath(root);
        IsPortable = isPortable;
    }

    /// <summary>Корень данных: папка exe (портативный режим) или %LOCALAPPDATA%\OfflineChatter.</summary>
    public string Root { get; }

    public bool IsPortable { get; }

    public string Engine => Path.Combine(Root, "engine");
    public string Config => Path.Combine(Root, "config");
    public string Profiles => Path.Combine(Config, "profiles");
    public string Certs => Path.Combine(Config, "certs");
    public string Models => Path.Combine(Root, "models");
    public string Chats => Path.Combine(Root, "chats");
    public string Reports => Path.Combine(Root, "reports");
    public string Logs => Path.Combine(Root, "logs");
    public string Cache => Path.Combine(Root, "cache");

    public string SettingsFile => Path.Combine(Config, "settings.json");
    public string ProvidersFile => Path.Combine(Config, "providers.json");
    public string McpFile => Path.Combine(Config, "mcp.json");
    public string ModelsFile => Path.Combine(Config, "models.json");
    public string PromptsFile => Path.Combine(Config, "prompts.json");

    public void EnsureCreated()
    {
        foreach (var dir in new[] { Engine, Config, Profiles, Certs, Models, Chats, Reports, Logs, Cache })
            Directory.CreateDirectory(dir);
    }

    /// <summary>
    /// Определяет корень данных.
    /// Портативный режим: рядом с exe лежит portable.flag ИЛИ в папку exe можно писать.
    /// Иначе (Program Files и т.п.) — %LOCALAPPDATA%\OfflineChatter.
    /// </summary>
    public static AppPaths Detect(string exeDirectory, string? localAppData = null)
    {
        if (string.IsNullOrWhiteSpace(exeDirectory)) throw new ArgumentException("Exe directory is empty.", nameof(exeDirectory));

        if (File.Exists(Path.Combine(exeDirectory, PortableFlagFileName)) || CanWrite(exeDirectory))
            return new AppPaths(exeDirectory, isPortable: true);

        localAppData ??= Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return new AppPaths(Path.Combine(localAppData, AppFolderName), isPortable: false);
    }

    internal static bool CanWrite(string directory)
    {
        try
        {
            if (!Directory.Exists(directory)) return false;
            var probe = Path.Combine(directory, ".write-test-" + Guid.NewGuid().ToString("N"));
            using (new FileStream(probe, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1, FileOptions.DeleteOnClose))
            {
            }
            return true;
        }
        catch (UnauthorizedAccessException) { return false; }
        catch (IOException) { return false; }
        catch (System.Security.SecurityException) { return false; }
    }
}
