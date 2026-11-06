using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

public sealed record LevelOption(LogLevel Level, string Title);

/// <summary>Раздел «Журнал»: журнал приложения и вывод llama-server, фильтры, автообновление, архив диагностики.</summary>
public sealed partial class LogsViewModel : PageViewModel
{
    private const int MaxEntries = 3000;
    private readonly AppServices _s;
    private readonly DispatcherTimer _timer;

    public LogsViewModel(AppServices services)
    {
        _s = services;
        Levels = new[]
        {
            new LevelOption(LogLevel.Trace, L.Get("Logs_LevelAll")),
            new LevelOption(LogLevel.Info, "Info+"),
            new LevelOption(LogLevel.Warn, "Warn+"),
            new LevelOption(LogLevel.Error, "Error+"),
        };
        _level = Levels[0];
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _timer.Tick += (_, _) => Refresh();
    }

    public override PageKind Kind => PageKind.Logs;

    public IReadOnlyList<LevelOption> Levels { get; }
    public IReadOnlyList<string> Sources { get; } = new[] { "app", "engine" };

    public ObservableCollection<LogEntry> Entries { get; } = new();

    [ObservableProperty] private string _source = "app";
    [ObservableProperty] private LevelOption _level;
    [ObservableProperty] private string _filter = string.Empty;
    [ObservableProperty] private bool _autoRefresh = true;
    [ObservableProperty] private string? _status;

    public override void OnNavigatedTo()
    {
        Refresh();
        _timer.IsEnabled = AutoRefresh;
    }

    partial void OnSourceChanged(string value) => Refresh();
    partial void OnLevelChanged(LevelOption value) => Refresh();
    partial void OnFilterChanged(string value) => Refresh();
    partial void OnAutoRefreshChanged(bool value) => _timer.IsEnabled = value;

    [RelayCommand]
    private void Refresh()
    {
        var all = LogReader.ReadTail(_s.Paths.Logs, Source, MaxEntries);
        var q = Filter?.Trim() ?? string.Empty;
        var list = all.Where(e => e.Level >= Level.Level &&
                                  (q.Length == 0 || e.Message.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                   e.Source.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) >= 0 ||
                                   (e.Exception?.IndexOf(q, StringComparison.CurrentCultureIgnoreCase) ?? -1) >= 0)).ToList();
        if (list.Count == Entries.Count && list.Count > 0 && Equals(list[list.Count - 1], Entries[Entries.Count - 1])) return;
        Entries.Clear();
        foreach (var e in list) Entries.Add(e);
        Status = L.F("Logs_Count", list.Count, all.Count);
    }

    [RelayCommand]
    private void CopyAll()
    {
        var sb = new StringBuilder();
        foreach (var e in Entries)
        {
            sb.Append(e.Time.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)).Append(' ').Append(e.Level).Append(' ')
              .Append(e.Source).Append(": ").AppendLine(e.Message);
            if (e.Data != null) sb.Append("    ").AppendLine(e.Data);
            if (e.Exception != null) sb.AppendLine(e.Exception);
        }
        try { Clipboard.SetText(sb.ToString()); }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    [RelayCommand]
    private void OpenFolder() => SettingsViewModel.OpenFolder(_s.Paths.Logs);

    /// <summary>Архив для отчёта об ошибке: журналы + конфиги без секретов + сведения о системе.</summary>
    [RelayCommand]
    private async System.Threading.Tasks.Task BundleAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "offline-chatter-diagnostics-" + DateTime.Now.ToString("yyyyMMdd-HHmm", CultureInfo.InvariantCulture) + ".zip",
            Filter = "ZIP|*.zip",
        };
        if (dlg.ShowDialog() != true) return;
        var hw = await _s.Engine.GetHardwareAsync();
        var info = new StringBuilder()
            .AppendLine("Offline Chatter " + _s.Version)
            .AppendLine(OsInfo.DisplayName)
            .AppendLine($"CLR {Environment.Version}, {(Environment.Is64BitProcess ? "x64" : "x86")} process")
            .AppendLine($"CPU: {hw.CpuName}, {hw.PhysicalCores}C/{hw.LogicalCores}T, AVX2={hw.Avx2}, AVX512={hw.Avx512}")
            .AppendLine($"RAM: {Units.FormatBytes(hw.TotalRam)}")
            .AppendLine("GPU: " + string.Join("; ", hw.Gpus.Select(g => $"{g.Name} {Units.FormatBytes(g.DedicatedMemory)} driver {g.DriverVersion}")))
            .AppendLine($"NVIDIA: {hw.NvidiaDriver} CUDA {hw.CudaVersionText}; Vulkan: {hw.VulkanAvailable}")
            .AppendLine("Engine: " + (_s.Engine.Registry.Active?.DisplayName ?? "none"))
            .AppendLine("Server: " + _s.Engine.Server.State + " " + _s.Engine.Server.CommandLine)
            .AppendLine("Theme: " + ThemeManager.Current)
            .ToString();
        try
        {
            DiagnosticBundle.Create(_s.Paths, info, dlg.FileName);
            Status = L.F("Logs_BundleSaved", dlg.FileName);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
    }
}
