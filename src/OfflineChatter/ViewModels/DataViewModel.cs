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
        if (!await _chat().FlushChangesAsync())
        {
            Status = L.Get("Chat_SaveFailed");
            return;
        }
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
        await Run(() => ChatTransfer.Export(chats, dlg.FileName), "Data_Exporting", L.F("Data_Exported", chats.Count, dlg.FileName));
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task ImportChatsAsync()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json|*.*|*.*", Multiselect = true };
        if (dlg.ShowDialog() != true) return;
        var chatPage = _chat();
        var existing = new HashSet<string>(chatPage.Chats.Select(c => c.Session.Id));
        var imported = new List<ChatSession>();
        var saveFailed = false;
        await Run(async () =>
        {
            foreach (var f in dlg.FileNames)
            {
                foreach (var c in ChatTransfer.Import(f, existing))
                {
                    imported.Add(c);
                    if (!await _s.Chats.SaveAsync(c).ConfigureAwait(false)) saveFailed = true;
                }
            }
        }, "Data_Importing");
        foreach (var c in imported) chatPage.AddImported(c);
        if (saveFailed)
        {
            chatPage.HasSaveError = true;
            Status = L.Get("Chat_SaveFailed");
        }
        else if (Status == null) Status = L.F("Data_Imported", imported.Count);
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
        if (IncludeChats && !await _chat().FlushChangesAsync())
        {
            Status = L.Get("Chat_SaveFailed");
            return;
        }
        _s.Settings.Save();
        BackupInfo? info = null;
        await Run(() => info = Backup.Create(_s.Paths, dlg.FileName, IncludeChats, _s.Version), "Data_CreatingBackup");
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

        var chat = _chat();
        if (!await chat.PrepareForShutdownAsync(cancelGeneration: true))
        {
            Status = L.Get("Chat_SaveFailed");
            return;
        }
        var restarting = false;
        var window = Application.Current.MainWindow;
        try
        {
            if (window != null) window.IsEnabled = false;
            if (!await chat.FlushChangesAsync())
            {
                Status = L.Get("Chat_SaveFailed");
                return;
            }
            _s.Engine.Server.Stop();
            string? safety = null;
            await Run(() => safety = Backup.Restore(_s.Paths, dlg.FileName, restoreChats: info.ChatFiles > 0, _s.Version), "Data_RestoringBackup");
            if (safety == null) return;
            // Настройки в памяти больше не актуальны — без перезапуска старые значения перезаписали бы восстановленные
            MessageBox.Show(L.F("Data_RestoreDone", safety), "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Information);
            _s.Settings.Frozen = true;
            restarting = App.Restart();
        }
        finally
        {
            if (!restarting)
            {
                if (window != null) window.IsEnabled = true;
                chat.PreparingForShutdown = false;
            }
        }
    }

    /// <summary>Программа уже в %LOCALAPPDATA%\Programs\OfflineChatter.</summary>
    public bool CanRelocate => !Shell.Relocation.IsInStandardLocation;

    public string RelocateHint => L.F("Move_Hint", Shell.Relocation.Target);

    [RelayCommand]
    private async Task RelocateAsync()
    {
        if (MessageBox.Show(L.F("Move_Confirm", Shell.Relocation.ExeDir, Shell.Relocation.Target), "Offline Chatter",
                MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
        await Shell.Relocation.MoveAsync(_s);
    }

    [RelayCommand]
    private void CreateShortcut()
    {
        Shell.Relocation.CreateStartMenuShortcut(App.ExePath);
        Status = L.Get("Move_ShortcutDone");
    }

    [RelayCommand]
    private void OpenDataFolder() => SettingsViewModel.OpenFolder(_s.Paths.Root);

    [RelayCommand]
    private void OpenLogsFolder() => SettingsViewModel.OpenFolder(_s.Paths.Logs);

    private Task Run(Action action, string progressKey, string? success = null) => Run(() =>
    {
        action();
        return Task.CompletedTask;
    }, progressKey, success);

    private async Task Run(Func<Task> action, string progressKey, string? success = null)
    {
        Status = L.Get(progressKey);
        IsBusy = true;
        try
        {
            await Task.Run(action);
            Status = success;
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
