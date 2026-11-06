using System;
using System.IO;
using Newtonsoft.Json;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Core.Storage;

/// <summary>Загрузка и сохранение config/settings.json.</summary>
public sealed class SettingsStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string path)
    {
        _path = path;
        Current = new AppSettings();
    }

    public AppSettings Current { get; private set; }

    public string FilePath => _path;

    /// <summary>
    /// Загружает настройки. Если файла нет — значения по умолчанию.
    /// Если файл повреждён — он переименовывается в *.corrupt-*.json и тоже берутся значения по умолчанию.
    /// </summary>
    public AppSettings Load()
    {
        lock (_gate)
        {
            try
            {
                Current = JsonFile.Read<AppSettings>(_path) ?? new AppSettings();
            }
            catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Error("Settings", "settings.json is unreadable, falling back to defaults", ex);
                QuarantineCorruptFile();
                Current = new AppSettings();
            }

            Normalize(Current);
            return Current;
        }
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                Current.Version = AppSettings.CurrentVersion;
                JsonFile.WriteAtomic(_path, Current);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Log.Error("Settings", "Failed to save settings", ex);
            }
        }
    }

    private void QuarantineCorruptFile()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var target = Path.ChangeExtension(_path, null) + ".corrupt-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".json";
            File.Move(_path, target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Settings", "Could not move corrupt settings file aside", ex);
        }
    }

    public static bool IsHttpUrl(string? s) =>
        Uri.TryCreate(s, UriKind.Absolute, out var u) && (u.Scheme == Uri.UriSchemeHttps || u.Scheme == Uri.UriSchemeHttp);

    /// <summary>Восстанавливает null-ветки и выходящие за пределы значения после ручной правки файла.</summary>
    internal static void Normalize(AppSettings s)
    {
        s.Appearance ??= new AppearanceSettings();
        s.Window ??= new WindowPlacementSettings();
        s.Logging ??= new LoggingSettings();
        s.Network ??= new NetworkSettings();
        s.Security ??= new SecuritySettings();
        s.Tools ??= new Tools.ToolsSettings();
        s.Tools.Consent ??= new System.Collections.Generic.Dictionary<string, Tools.ConsentMode>();
        if (s.Tools.MaxSteps is < 1 or > 20) s.Tools.MaxSteps = 6;
        s.Models ??= new Models.ModelsSettings();
        s.Models.ScanFolders ??= new System.Collections.Generic.List<string>();
        s.Models.HfTokenProtected ??= string.Empty;
        s.Engine ??= new Engine.EngineSettings();
        s.Engine.ExternalEngines ??= new System.Collections.Generic.List<string>();
        if (s.Engine.Port is < 0 or > 65535) s.Engine.Port = 0;
        var n = s.Network;
        if (n.TimeoutSeconds is < 5 or > 600) n.TimeoutSeconds = 30;
        if (n.MaxConcurrentDownloads is < 1 or > 8) n.MaxConcurrentDownloads = 2;
        if (n.SpeedLimitKBps < 0) n.SpeedLimitKBps = 0;
        if (!IsHttpUrl(n.HfEndpoint)) n.HfEndpoint = "https://huggingface.co";
        if (!IsHttpUrl(n.GitHubApiEndpoint)) n.GitHubApiEndpoint = "https://api.github.com";
        n.ProxyAddress ??= string.Empty;
        n.ProxyUser ??= string.Empty;
        n.ProxyPasswordProtected ??= string.Empty;
        n.GitHubDownloadPrefix ??= string.Empty;
        if (string.IsNullOrWhiteSpace(s.Language)) s.Language = "auto";
        s.UpdateRepo ??= string.Empty;

        if (string.IsNullOrWhiteSpace(s.Appearance.CustomAccent) || !ColorHex.TryParse(s.Appearance.CustomAccent, out _, out _, out _))
            s.Appearance.CustomAccent = "#0078D4";

        var w = s.Window;
        if (double.IsNaN(w.Width) || w.Width < 640) w.Width = 1180;
        if (double.IsNaN(w.Height) || w.Height < 420) w.Height = 760;
        if (double.IsNaN(w.SidebarWidth) || w.SidebarWidth < 180 || w.SidebarWidth > 600) w.SidebarWidth = 260;

        var l = s.Logging;
        if (l.MaxFileSizeMb is < 1 or > 200) l.MaxFileSizeMb = 5;
        if (l.MaxFiles is < 1 or > 50) l.MaxFiles = 5;
        if (!Enum.TryParse<LogLevel>(l.MinLevel, true, out _)) l.MinLevel = nameof(LogLevel.Info);
    }
}
