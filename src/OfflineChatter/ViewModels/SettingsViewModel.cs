using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Net;
using OfflineChatter.Localization;
using OfflineChatter.Theming;

namespace OfflineChatter.ViewModels;

public sealed record LanguageOption(string Code, string Title);

public sealed record SizeOption(int Value, string Title);

/// <summary>Вкладки страницы настроек.</summary>
public enum SettingsSection
{
    General,
    Appearance,
    Chat,
    Network,
    LocalApi,
    Privacy,
    Data,
    About,
}

/// <summary>Пункт боковой панели в настройках: показать/скрыть и открыть раздел.</summary>
public sealed partial class NavToggle : ObservableObject
{
    private readonly MainViewModel _main;

    public NavToggle(MainViewModel main, NavItem item)
    {
        _main = main;
        Item = item;
        _isVisible = main.IsPageVisible(item.Kind);
    }

    public NavItem Item { get; }
    public string Title => Item.Title;
    public string IconKey => Item.IconKey;

    [ObservableProperty] private bool _isVisible;

    partial void OnIsVisibleChanged(bool value) => _main.SetPageVisible(Item.Kind, value);

    [RelayCommand]
    private void Open() => _main.Navigate(Item.Kind);
}

/// <summary>Страница «Настройки»: вкладки «Общие», «Оформление», «Чат», «Сеть», «Локальный API», «Приватность», «Данные», «О программе».</summary>
public sealed partial class SettingsViewModel : PageViewModel
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly string _initialLanguage;

    public SettingsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Appearance = new AppearanceViewModel(services.Settings);
        Network = new NetworkSettingsViewModel(services);
        Network.OfflineChanged += (_, _) => main.NotifyOfflineChanged();
        Api = new LocalApiViewModel(services);
        Prompts = new PromptPresetsViewModel(services);
        Data = new DataViewModel(services, () => main.Chat);
        _initialLanguage = services.Settings.Current.Language;
        _language = _initialLanguage;
        _logMessageContent = services.Settings.Current.Logging.LogMessageContent;

        SidebarItems = main.AllNavItems.Where(n => n.Kind != PageKind.Settings).Select(n => new NavToggle(main, n)).ToList();

        var a = services.Settings.Current.Attachments;
        _imageMaxSide = ImageSizeOptions.FirstOrDefault(o => o.Value == a.ImageMaxSide) ?? ImageSizeOptions[2];
        _imageQuality = a.ImageQuality;
        _keepPngWithAlpha = a.KeepPngWithAlpha;

        OsName = OsInfo.DisplayName;
        RuntimeInfo = L.F("Settings_Runtime", Environment.Version, Environment.Is64BitProcess ? "x64" : "x86");
        if (services.PendingUpdate is { } u) ApplyUpdateInfo(u);
        services.UpdateFound += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (services.PendingUpdate is { } info) ApplyUpdateInfo(info);
        }));
    }

    public override PageKind Kind => PageKind.Settings;

    public IReadOnlyList<SettingsSection> Sections { get; } = Enum.GetValues(typeof(SettingsSection)).Cast<SettingsSection>().ToList();

    [ObservableProperty] private SettingsSection _selectedSection = SettingsSection.General;

    /// <summary>Открыть настройки на нужной вкладке (из других разделов и палитры команд).</summary>
    public void Show(SettingsSection section)
    {
        SelectedSection = section;
        _main.Navigate(PageKind.Settings);
    }

    public AppearanceViewModel Appearance { get; }
    public NetworkSettingsViewModel Network { get; }
    public LocalApiViewModel Api { get; }
    public PromptPresetsViewModel Prompts { get; }
    public DataViewModel Data { get; }

    // ---------- Общие ----------
    public IReadOnlyList<LanguageOption> Languages { get; } = new[]
    {
        new LanguageOption("auto", L.Get("Settings_LanguageAuto")),
        new LanguageOption("ru", "Русский"),
        new LanguageOption("en", "English"),
    };

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(RestartRequired))]
    private string _language;

    public bool RestartRequired => !string.Equals(Language, _initialLanguage, StringComparison.OrdinalIgnoreCase);

    partial void OnLanguageChanged(string value)
    {
        _services.Settings.Current.Language = value;
        _services.Settings.Save();
    }

    [RelayCommand]
    private void RestartNow() => App.Restart("--page Settings");

    public IReadOnlyList<NavToggle> SidebarItems { get; }

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

    public string QuickAskHint => string.IsNullOrEmpty(App.QuickAskHotkeyText) ? L.Get("Settings_QuickAskHint") : L.F("Settings_QuickAskActive", App.QuickAskHotkeyText);

    public bool SendWithCtrlEnter
    {
        get => _services.Settings.Current.Window.SendWithCtrlEnter;
        set { _services.Settings.Current.Window.SendWithCtrlEnter = value; _services.Settings.Save(); OnPropertyChanged(); }
    }

    [RelayCommand]
    private void RunWizard() => _main.Navigate(PageKind.Welcome);

    // ---------- Чат: вложения ----------
    public IReadOnlyList<SizeOption> ImageSizeOptions { get; } = new[]
    {
        new SizeOption(768, "768 px"),
        new SizeOption(1024, "1024 px"),
        new SizeOption(1536, "1536 px"),
        new SizeOption(2048, "2048 px"),
        new SizeOption(0, L.Get("Settings_ImageOriginal")),
    };

    public IReadOnlyList<int> QualityOptions { get; } = new[] { 60, 75, 85, 95 };

    [ObservableProperty] private SizeOption _imageMaxSide;
    [ObservableProperty] private int _imageQuality;
    [ObservableProperty] private bool _keepPngWithAlpha;

    partial void OnImageMaxSideChanged(SizeOption value) => SaveAttachments();
    partial void OnImageQualityChanged(int value) => SaveAttachments();
    partial void OnKeepPngWithAlphaChanged(bool value) => SaveAttachments();

    private void SaveAttachments()
    {
        var a = _services.Settings.Current.Attachments;
        a.ImageMaxSide = ImageMaxSide?.Value ?? 1536;
        a.ImageQuality = ImageQuality;
        a.KeepPngWithAlpha = KeepPngWithAlpha;
        _services.Settings.Save();
    }

    public bool ToolsByDefault
    {
        get => _services.Settings.Current.Tools.EnabledByDefault;
        set { _services.Settings.Current.Tools.EnabledByDefault = value; _services.Settings.Save(); OnPropertyChanged(); }
    }

    // ---------- Приватность и безопасность ----------
    [ObservableProperty] private bool _logMessageContent;

    partial void OnLogMessageContentChanged(bool value)
    {
        _services.Settings.Current.Logging.LogMessageContent = value;
        _services.Settings.Save();
    }

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
        var api = Core.Storage.Secrets.UnprotectOrEmpty(s.Engine.ApiKeyProtected);
        var keys = _services.Providers.Items.Select(p => Core.Storage.Secrets.UnprotectOrEmpty(p.ApiKeyProtected)).ToList();
        Core.Storage.Secrets.EnableMasterPassword(password);
        s.Network.ProxyPasswordProtected = Core.Storage.Secrets.Protect(proxy);
        s.Models.HfTokenProtected = Core.Storage.Secrets.Protect(hf);
        s.Engine.ApiKeyProtected = api.Length == 0 ? string.Empty : Core.Storage.Secrets.Protect(api);
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

    // ---------- О программе и обновления ----------
    public string AppVersion => "Offline Chatter " + _services.Version;
    public string OsName { get; }
    public string RuntimeInfo { get; }

    public string UpdateRepo
    {
        get => _services.Settings.Current.UpdateRepo;
        set { _services.Settings.Current.UpdateRepo = string.IsNullOrWhiteSpace(value) ? Core.Storage.AppSettings.DefaultUpdateRepo : value.Trim(); _services.Settings.Save(); OnPropertyChanged(); }
    }

    [ObservableProperty] private string? _updateStatus;
    [ObservableProperty] private string? _updateNotes;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(OpenUpdatePageCommand), nameof(InstallUpdateCommand))]
    private UpdateInfo? _update;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckUpdatesCommand), nameof(InstallUpdateCommand))]
    private bool _isUpdating;

    private void ApplyUpdateInfo(UpdateInfo info)
    {
        Update = info;
        UpdateStatus = info.Available ? L.F("Update_Available", info.LatestTag) : L.F("Update_Latest", _services.Version);
        UpdateNotes = info.Available && !string.IsNullOrWhiteSpace(info.Notes) ? info.Notes!.Trim() : null;
    }

    private bool CanCheckUpdates() => !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanCheckUpdates))]
    private async Task CheckUpdatesAsync()
    {
        UpdateStatus = L.Get("Update_Checking");
        try
        {
            var info = await UpdateChecker.CheckAsync(_services.Http, UpdateRepo, AppServices.CurrentVersion, CancellationToken.None);
            ApplyUpdateInfo(info);
            if (info.Available) _services.PendingUpdate = info;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.IO.IOException or Newtonsoft.Json.JsonException or InvalidCastException)
        {
            UpdateStatus = ex.GetBaseException().Message;
        }
    }

    private bool CanOpenUpdate() => Update != null;

    [RelayCommand(CanExecute = nameof(CanOpenUpdate))]
    private void OpenUpdatePage() => EngineViewModel.OpenUrl(Update!.PageUrl);

    private bool CanInstall() => Update is { CanInstall: true } && !IsUpdating;

    /// <summary>Скачать новую версию (с проверкой SHA-256), подменить exe и перезапуститься.</summary>
    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallUpdateAsync()
    {
        if (Update == null) return;
        IsUpdating = true;
        UpdateStatus = L.F("Update_Downloading", Update.LatestTag);
        try
        {
            var (exe, config) = await UpdateChecker.DownloadAsync(_services.Downloads, Update, _services.Paths.Cache, _services.Releases.MirrorUrl);
            UpdateChecker.Swap(App.ExePath, exe, config);
            App.Restart("--page Settings");
        }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.Net.Http.HttpRequestException)
        {
            // Нет прав на запись в папку программы (Program Files) — предлагаем скачать вручную
            UpdateStatus = L.F("Update_Failed", ex.GetBaseException().Message);
            Core.Diagnostics.Log.Warn("Update", "Install failed", ex);
        }
        finally
        {
            IsUpdating = false;
        }
    }

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
