using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>Настройки → «Данные»: перенос чатов, резервная копия и восстановление.</summary>
public sealed partial class DataViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Func<ChatPageViewModel> _chat;

    public DataViewModel(AppServices services, Func<ChatPageViewModel> chat)
    {
        _s = services;
        _chat = chat;
        DataLocation = L.F(services.Paths.IsPortable ? "Settings_DataPortable" : "Settings_DataLocal", services.Paths.Root);
    }

    public string DataLocation { get; }

    [ObservableProperty] private bool _includeChats = true;
    [ObservableProperty] private string? _status;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportChatsCommand), nameof(ImportChatsCommand), nameof(CreateBackupCommand), nameof(RestoreBackupCommand))]
    private bool _isBusy;

    private bool CanRun() => !IsBusy;

    private static string Stamp => DateTime.Now.ToString("yyyy-MM-dd");

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ExportChatsAsync()
    {
        var chats = _s.Chats.LoadAll().Where(c => c.Messages.Count > 0).ToList();
        if (chats.Count == 0)
        {
            Status = L.Get("Data_NoChats");
            return;
        }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "offline-chatter-chats-" + Stamp + ".json",
            Filter = "JSON|*.json",
        };
        if (dlg.ShowDialog() != true) return;
        await Run(() => ChatTransfer.Export(chats, dlg.FileName), L.F("Data_Exported", chats.Count, dlg.FileName));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ImportChatsAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json|*.*|*.*", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        var chatPage = _chat();
        var existing = new HashSet<string>(chatPage.Chats.Select(c => c.Session.Id));
        var imported = new List<ChatSession>();
        await Run(() =>
        {
            foreach (var f in dlg.FileNames)
            {
                foreach (var c in ChatTransfer.Import(f, existing))
                {
                    _s.Chats.Save(c);
                    imported.Add(c);
                }
            }
        }, null);
        foreach (var c in imported) chatPage.AddImported(c);
        if (imported.Count > 0 || Status == null) Status = L.F("Data_Imported", imported.Count);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task CreateBackupAsync()
    {
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            FileName = "offline-chatter-backup-" + Stamp + ".zip",
            Filter = "ZIP|*.zip",
        };
        if (dlg.ShowDialog() != true) return;
        _s.Settings.Save();
        BackupInfo? info = null;
        await Run(() => info = Backup.Create(_s.Paths, dlg.FileName, IncludeChats, _s.Version), null);
        if (info != null) Status = L.F("Data_BackupDone", info.ConfigFiles, info.ChatFiles, dlg.FileName);
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RestoreBackupAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "ZIP|*.zip" };
        if (dlg.ShowDialog() != true) return;
        BackupInfo info;
        try
        {
            info = Backup.Inspect(dlg.FileName);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or Newtonsoft.Json.JsonException or UnauthorizedAccessException)
        {
            Status = L.Get("Data_NotBackup");
            return;
        }

        var text = L.F("Data_RestoreConfirm", info.Created.ToLocalTime().ToString("g", L.Culture), info.AppVersion, info.ConfigFiles, info.ChatFiles)
                   + (info.MasterPassword ? string.Empty : "\n\n" + L.Get("Data_RestoreDpapiNote"));
        if (MessageBox.Show(text, "Offline Chatter", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;

        _s.Engine.Server.Stop();
        string? safety = null;
        await Run(() => safety = Backup.Restore(_s.Paths, dlg.FileName, restoreChats: info.ChatFiles > 0, _s.Version), null);
        if (safety == null) return;
        // Настройки в памяти больше не актуальны — без перезапуска старые значения перезаписали бы восстановленные
        MessageBox.Show(L.F("Data_RestoreDone", safety), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Information);
        _s.Settings.Frozen = true;
        App.Restart();
    }

    [RelayCommand]
    private void OpenDataFolder() => SettingsViewModel.OpenFolder(_s.Paths.Root);

    [RelayCommand]
    private void OpenLogsFolder() => SettingsViewModel.OpenFolder(_s.Paths.Logs);

    private async Task Run(Action action, string? success)
    {
        IsBusy = true;
        try
        {
            await Task.Run(action);
            if (success != null) Status = success;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException or Newtonsoft.Json.JsonException)
        {
            Status = L.F("Data_Failed", ex.Message);
            Core.Diagnostics.Log.Warn("Data", "Operation failed", ex);
        }
        finally
        {
            IsBusy = false;
        }
    }
}
