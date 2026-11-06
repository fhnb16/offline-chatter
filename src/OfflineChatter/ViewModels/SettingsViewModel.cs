using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

public sealed record LanguageOption(string Code, string Title);

/// <summary>Страница «Настройки»: оформление, язык, журнал, сведения о системе.</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly string _initialLanguage;

    public SettingsViewModel(AppServices services)
    {
        _services = services;
        Appearance = new AppearanceViewModel(services.Settings);
        Network = new NetworkSettingsViewModel(services);
        _initialLanguage = services.Settings.Current.Language;
        _language = _initialLanguage;
        _logMessageContent = services.Settings.Current.Logging.LogMessageContent;

        OsName = OsInfo.DisplayName;
        RuntimeInfo = L.F("Settings_Runtime", Environment.Version, Environment.Is64BitProcess ? "x64" : "x86");
        DataLocation = L.F(services.Paths.IsPortable ? "Settings_DataPortable" : "Settings_DataLocal", services.Paths.Root);
    }

    public override PageKind Kind => PageKind.Settings;

    public AppearanceViewModel Appearance { get; }

    public NetworkSettingsViewModel Network { get; }

    public IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption("auto", L.Get("Settings_LanguageAuto")),
        new LanguageOption("ru", "Русский"),
        new LanguageOption("en", "English"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartRequired))]
    private string _language;

    [ObservableProperty] private bool _logMessageContent;

    public bool RestartRequired => !string.Equals(Language, _initialLanguage, StringComparison.OrdinalIgnoreCase);

    public string OsName { get; }
    public string RuntimeInfo { get; }
    public string DataLocation { get; }

    partial void OnLanguageChanged(string value)
    {
        _services.Settings.Current.Language = value;
        _services.Settings.Save();
    }

    partial void OnLogMessageContentChanged(bool value)
    {
        _services.Settings.Current.Logging.LogMessageContent = value;
        _services.Settings.Save();
    }

    // ---------- Поведение ----------
    public bool CloseToTray
    {
        get => _services.Settings.Current.Window.CloseToTray;
        set { _services.Settings.Current.Window.CloseToTray = value; _services.Settings.Save(); OnPropertyChanged(); }
    }

    public bool QuickAskHotkey
    {
        get => _services.Settings.Current.Window.QuickAskHotkey;
        set { _services.Settings.Current.Window.QuickAskHotkey = value; _services.Settings.Save(); OnPropertyChanged(); }
    }

    // ---------- Безопасность ----------
    public bool IsMasterPassword => Core.Storage.Secrets.Mode == Core.Storage.SecretsMode.MasterPassword;
    public string SecurityState => L.Get(IsMasterPassword ? "Security_ModeMaster" : "Security_ModeDpapi");
    [ObservableProperty] private string? _securityStatus;

    /// <summary>Включить/сменить мастер-пароль (вызывается из code-behind с паролями из PasswordBox).</summary>
    public void SetMasterPassword(string password, string confirm)
    {
        if (password.Length < 6)
        {
            SecurityStatus = L.Get("Security_TooShort");
            return;
        }
        if (password != confirm)
        {
            SecurityStatus = L.Get("Security_Mismatch");
            return;
        }
        // Сначала расшифровываем старым способом (DPAPI или старым паролем), затем шифруем новым
        var s = _services.Settings.Current;
        var proxy = Core.Storage.Secrets.UnprotectOrEmpty(s.Network.ProxyPasswordProtected);
        var hf = Core.Storage.Secrets.UnprotectOrEmpty(s.Models.HfTokenProtected);
        var keys = _services.Providers.Items.Select(p => Core.Storage.Secrets.UnprotectOrEmpty(p.ApiKeyProtected)).ToList();
        Core.Storage.Secrets.EnableMasterPassword(password);
        s.Network.ProxyPasswordProtected = Core.Storage.Secrets.Protect(proxy);
        s.Models.HfTokenProtected = Core.Storage.Secrets.Protect(hf);
        for (var i = 0; i < keys.Count; i++) _services.Providers.Items[i].ApiKeyProtected = Core.Storage.Secrets.Protect(keys[i]);
        _services.Providers.Save();
        _services.Settings.Save();
        SecurityStatus = L.Get("Security_Enabled");
        OnPropertyChanged(nameof(IsMasterPassword));
        OnPropertyChanged(nameof(SecurityState));
    }

    [RelayCommand]
    private void DisableMasterPassword()
    {
        Core.Storage.Secrets.DisableMasterPassword();
        _services.ReprotectAllSecrets();
        SecurityStatus = L.Get("Security_Disabled");
        OnPropertyChanged(nameof(IsMasterPassword));
        OnPropertyChanged(nameof(SecurityState));
    }

    // ---------- Обновления ----------
    public string UpdateRepo
    {
        get => _services.Settings.Current.UpdateRepo;
        set { _services.Settings.Current.UpdateRepo = value?.Trim() ?? string.Empty; _services.Settings.Save(); OnPropertyChanged(); }
    }

    [ObservableProperty] private string? _updateStatus;
    private string? _updatePage;

    public string AppVersion => "Offline Chatter " + _services.Version;

    [RelayCommand]
    private async System.Threading.Tasks.Task CheckUpdatesAsync()
    {
        if (string.IsNullOrWhiteSpace(UpdateRepo))
        {
            UpdateStatus = L.Get("Update_NoRepo");
            return;
        }
        UpdateStatus = L.Get("Mcp_Connecting");
        try
        {
            var current = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
            var info = await Core.Net.UpdateChecker.CheckAsync(_services.Http, UpdateRepo, current, System.Threading.CancellationToken.None);
            _updatePage = info.PageUrl;
            UpdateStatus = info.Available ? L.F("Update_Available", info.LatestTag) : L.Get("Update_Latest");
            OpenUpdatePageCommand.NotifyCanExecuteChanged();
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.IO.IOException or Newtonsoft.Json.JsonException)
        {
            UpdateStatus = ex.GetBaseException().Message;
        }
    }

    private bool CanOpenUpdate() => _updatePage != null;

    [RelayCommand(CanExecute = nameof(CanOpenUpdate))]
    private void OpenUpdatePage() => EngineViewModel.OpenUrl(_updatePage!);

    [RelayCommand]
    private void RunWizard()
    {
        if (System.Windows.Application.Current?.MainWindow?.DataContext is MainViewModel main) main.Navigate(PageKind.Welcome);
    }

    [RelayCommand]
    private void OpenDataFolder() => OpenFolder(_services.Paths.Root);

    [RelayCommand]
    private void OpenLogsFolder() => OpenFolder(_services.Paths.Logs);

    internal static void OpenFolder(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", "\"" + path + "\"") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Core.Diagnostics.Log.Warn("UI", "Cannot open folder " + path, ex);
        }
    }
}
