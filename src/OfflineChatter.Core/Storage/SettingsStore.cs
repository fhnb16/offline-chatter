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

    /// <summary>Восстанавливает null-ветки и выходящие за пределы значения после ручной правки файла.</summary>
    internal static void Normalize(AppSettings s)
    {
        s.Appearance ??= new AppearanceSettings();
        s.Window ??= new WindowPlacementSettings();
        s.Logging ??= new LoggingSettings();
        if (string.IsNullOrWhiteSpace(s.Language)) s.Language = "auto";

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
