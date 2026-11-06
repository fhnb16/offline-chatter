using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

public sealed record NetCheckRow(string Host, bool Ok, string Details);

public sealed record AdapterOption(string Id, string Title);

/// <summary>Раздел «Сеть» в настройках: прокси, TLS, зеркала, загрузки, проверка соединения.</summary>
public sealed partial class NetworkSettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly NetworkSettings _s;

    public NetworkSettingsViewModel(AppServices services)
    {
        _services = services;
        _s = services.Settings.Current.Network;
        _proxyMode = _s.ProxyMode;
        _proxyUser = _s.ProxyUser;
        _tlsMode = _s.TlsMode;
        _hfEndpoint = _s.HfEndpoint;
        _gitHubApiEndpoint = _s.GitHubApiEndpoint;
        _gitHubDownloadPrefix = _s.GitHubDownloadPrefix;
        _maxConcurrentDownloads = _s.MaxConcurrentDownloads;
        _speedLimitKBps = _s.SpeedLimitKBps;
        _offlineMode = _s.OfflineMode;
        _checkUpdates = _s.CheckUpdates;
        _timeoutSeconds = _s.TimeoutSeconds;
        var parsed = ProxyEndpoint.Parse(_s.ProxyAddress);
        _proxyKind = parsed?.Kind ?? ProxyKind.Http;
        _proxyHost = parsed?.Host ?? string.Empty;
        _proxyPort = parsed?.Port.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        RefreshAdapters();
        _selectedInterface = Adapters.FirstOrDefault(a => a.Id == _s.NetworkInterface) ?? Adapters[0];
        if (!string.IsNullOrEmpty(_s.NetworkInterface) && _selectedInterface.Id.Length == 0)
        {
            // Адаптер сейчас отключён (VPN не запущен) — показываем его, чтобы выбор не потерялся
            var missing = new AdapterOption(_s.NetworkInterface, L.F("Net_InterfaceMissing", _s.NetworkInterface));
            Adapters.Add(missing);
            _selectedInterface = missing;
        }
        RootsInfo = L.F("Net_RootsInfo", services.Roots.Count, services.Paths.Certs);
    }

    public IReadOnlyList<ProxyMode> ProxyModes { get; } = Enum.GetValues(typeof(ProxyMode)).Cast<ProxyMode>().ToList();
    public IReadOnlyList<TlsMode> TlsModes { get; } = Enum.GetValues(typeof(TlsMode)).Cast<TlsMode>().ToList();
    public IReadOnlyList<int> ConcurrencyOptions { get; } = new[] { 1, 2, 3, 4 };
    public IReadOnlyList<ProxyKind> ProxyKinds { get; } = new[] { ProxyKind.Http, ProxyKind.Socks5, ProxyKind.Socks5h };
    public IReadOnlyList<int> TimeoutOptions { get; } = new[] { 15, 30, 60, 120, 300 };

    public ObservableCollection<AdapterOption> Adapters { get; } = new();

    /// <summary>Офлайн-режим изменён (для индикатора в боковой панели).</summary>
    public event EventHandler? OfflineChanged;

    [ObservableProperty] private bool _offlineMode;
    [ObservableProperty] private bool _checkUpdates;
    [ObservableProperty] private int _timeoutSeconds;
    [ObservableProperty] private AdapterOption _selectedInterface;
    [ObservableProperty] private ProxyKind _proxyKind;
    [ObservableProperty] private string _proxyHost;
    [ObservableProperty] private string _proxyPort;
    [ObservableProperty] private string _proxyLink = string.Empty;
    [ObservableProperty] private string? _proxyLinkStatus;

    /// <summary>Выбран свой интерфейс или SOCKS — работает только собственный сетевой стек (видно в подсказке).</summary>
    public bool UsesManagedStack => _services.Http.ForcesManagedStack;

    [RelayCommand]
    private void RefreshAdapters()
    {
        var keep = SelectedInterface?.Id;
        Adapters.Clear();
        Adapters.Add(new AdapterOption(string.Empty, L.Get("Net_InterfaceDefault")));
        foreach (var a in NetworkRoute.ListAdapters()) Adapters.Add(new AdapterOption(a.Id, a.Title));
        if (keep != null) SelectedInterface = Adapters.FirstOrDefault(a => a.Id == keep) ?? Adapters[0];
    }

    /// <summary>Разбор ссылки вида socks5h://user:pass@host:port — заполняет поля и пароль.</summary>
    [RelayCommand]
    private void ApplyProxyLink()
    {
        var p = ProxyEndpoint.Parse(ProxyLink);
        if (p == null)
        {
            ProxyLinkStatus = L.Get("Net_ProxyLinkBad");
            return;
        }
        ProxyMode = ProxyMode.Manual;
        ProxyKind = p.Kind;
        ProxyHost = p.Host;
        ProxyPort = p.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (p.Credentials != null)
        {
            ProxyUser = p.Credentials.UserName;
            SetProxyPassword(p.Credentials.Password);
        }
        ProxyLink = string.Empty;
        ProxyLinkStatus = L.F("Net_ProxyLinkOk", p.ToUrl(withUser: true));
    }

    public string RootsInfo { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualProxy))]
    private ProxyMode _proxyMode;

    [ObservableProperty] private string _proxyUser;
    [ObservableProperty] private TlsMode _tlsMode;
    [ObservableProperty] private string _hfEndpoint;
    [ObservableProperty] private string _gitHubApiEndpoint;
    [ObservableProperty] private string _gitHubDownloadPrefix;
    [ObservableProperty] private int _maxConcurrentDownloads;
    [ObservableProperty] private int _speedLimitKBps;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CheckCommand))]
    private bool _isChecking;

    public bool IsManualProxy => ProxyMode == ProxyMode.Manual;

    public ObservableCollection<NetCheckRow> CheckResults { get; } = new();

    /// <summary>Пароль прокси приходит из PasswordBox (code-behind) и сразу шифруется.</summary>
    public void SetProxyPassword(string password)
    {
        _s.ProxyPasswordProtected = Secrets.Protect(password);
        Commit();
        OnPropertyChanged(nameof(HasProxyPassword));
    }

    public bool HasProxyPassword => !string.IsNullOrEmpty(_s.ProxyPasswordProtected);

    partial void OnProxyModeChanged(ProxyMode value) => Commit();
    partial void OnProxyKindChanged(ProxyKind value) => Commit();
    partial void OnProxyHostChanged(string value) => Commit();
    partial void OnProxyPortChanged(string value) => Commit();
    partial void OnSelectedInterfaceChanged(AdapterOption value) => Commit();
    partial void OnCheckUpdatesChanged(bool value) => Commit();
    partial void OnTimeoutSecondsChanged(int value) => Commit();

    partial void OnOfflineModeChanged(bool value)
    {
        Commit();
        Core.Diagnostics.Log.Info("Net", value ? "Offline mode on" : "Offline mode off");
        OfflineChanged?.Invoke(this, EventArgs.Empty);
    }
    partial void OnProxyUserChanged(string value) => Commit();
    partial void OnTlsModeChanged(TlsMode value) => Commit();
    partial void OnHfEndpointChanged(string value) => Commit();
    partial void OnGitHubApiEndpointChanged(string value) => Commit();
    partial void OnGitHubDownloadPrefixChanged(string value) => Commit();
    partial void OnMaxConcurrentDownloadsChanged(int value) => Commit();
    partial void OnSpeedLimitKBpsChanged(int value) => Commit();

    private void Commit()
    {
        _s.ProxyMode = ProxyMode;
        var host = ProxyHost?.Trim() ?? string.Empty;
        var scheme = ProxyKind switch { ProxyKind.Socks5 => "socks5", ProxyKind.Socks5h => "socks5h", _ => "http" };
        var port = int.TryParse(ProxyPort, out var pp) && pp is > 0 and < 65536 ? ":" + pp.ToString(System.Globalization.CultureInfo.InvariantCulture) : string.Empty;
        _s.ProxyAddress = host.Length == 0 ? string.Empty : $"{scheme}://{host}{port}";
        _s.OfflineMode = OfflineMode;
        _s.CheckUpdates = CheckUpdates;
        if (TimeoutSeconds is >= 5 and <= 600) _s.TimeoutSeconds = TimeoutSeconds;
        if (SelectedInterface != null) _s.NetworkInterface = SelectedInterface.Id;
        _s.ProxyUser = ProxyUser?.Trim() ?? string.Empty;
        _s.TlsMode = TlsMode;
        if (SettingsStore.IsHttpUrl(HfEndpoint)) _s.HfEndpoint = HfEndpoint.Trim().TrimEnd('/');
        if (SettingsStore.IsHttpUrl(GitHubApiEndpoint)) _s.GitHubApiEndpoint = GitHubApiEndpoint.Trim().TrimEnd('/');
        _s.GitHubDownloadPrefix = GitHubDownloadPrefix?.Trim() ?? string.Empty;
        _s.MaxConcurrentDownloads = Math.Max(1, MaxConcurrentDownloads);
        _s.SpeedLimitKBps = Math.Max(0, SpeedLimitKBps);
        _services.Http.Reconfigure();
        _services.Settings.Save();
        OnPropertyChanged(nameof(UsesManagedStack));
    }

    private bool CanCheck() => !IsChecking;

    /// <summary>Проверяет основные хосты и показывает, какой уровень TLS сработал.</summary>
    [RelayCommand(CanExecute = nameof(CanCheck))]
    private async Task CheckAsync()
    {
        IsChecking = true;
        CheckResults.Clear();
        try
        {
            var urls = new[]
            {
                _s.GitHubApiEndpoint + "/repos/ggml-org/llama.cpp",
                "https://release-assets.githubusercontent.com/",
                _s.HfEndpoint + "/api/models?limit=1",
                "https://cas-bridge.xethub.hf.co/",
            };
            var tasks = urls.Select(u => _services.Http.CheckAsync(u, CancellationToken.None)).ToList();
            foreach (var r in await Task.WhenAll(tasks))
            {
                var details = r.Ok
                    ? L.F("Net_CheckOk", (int)r.Elapsed.TotalMilliseconds, L.Get("Enum_TlsLevel_" + (r.Level ?? TlsLevel.System)), r.Message)
                    : r.Message;
                CheckResults.Add(new NetCheckRow(r.Host, r.Ok, details));
            }
        }
        finally
        {
            IsChecking = false;
        }
    }
}
