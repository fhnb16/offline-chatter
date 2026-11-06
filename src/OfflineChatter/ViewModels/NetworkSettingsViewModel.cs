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
        _proxyAddress = _s.ProxyAddress;
        _proxyUser = _s.ProxyUser;
        _tlsMode = _s.TlsMode;
        _hfEndpoint = _s.HfEndpoint;
        _gitHubApiEndpoint = _s.GitHubApiEndpoint;
        _gitHubDownloadPrefix = _s.GitHubDownloadPrefix;
        _maxConcurrentDownloads = _s.MaxConcurrentDownloads;
        _speedLimitKBps = _s.SpeedLimitKBps;
        RootsInfo = L.F("Net_RootsInfo", services.Roots.Count, services.Paths.Certs);
    }

    public IReadOnlyList<ProxyMode> ProxyModes { get; } = Enum.GetValues(typeof(ProxyMode)).Cast<ProxyMode>().ToList();
    public IReadOnlyList<TlsMode> TlsModes { get; } = Enum.GetValues(typeof(TlsMode)).Cast<TlsMode>().ToList();
    public IReadOnlyList<int> ConcurrencyOptions { get; } = new[] { 1, 2, 3, 4 };

    public string RootsInfo { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsManualProxy))]
    private ProxyMode _proxyMode;

    [ObservableProperty] private string _proxyAddress;
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
    }

    public bool HasProxyPassword => !string.IsNullOrEmpty(_s.ProxyPasswordProtected);

    partial void OnProxyModeChanged(ProxyMode value) => Commit();
    partial void OnProxyAddressChanged(string value) => Commit();
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
        _s.ProxyAddress = ProxyAddress?.Trim() ?? string.Empty;
        _s.ProxyUser = ProxyUser?.Trim() ?? string.Empty;
        _s.TlsMode = TlsMode;
        if (SettingsStore.IsHttpUrl(HfEndpoint)) _s.HfEndpoint = HfEndpoint.Trim().TrimEnd('/');
        if (SettingsStore.IsHttpUrl(GitHubApiEndpoint)) _s.GitHubApiEndpoint = GitHubApiEndpoint.Trim().TrimEnd('/');
        _s.GitHubDownloadPrefix = GitHubDownloadPrefix?.Trim() ?? string.Empty;
        _s.MaxConcurrentDownloads = Math.Max(1, MaxConcurrentDownloads);
        _s.SpeedLimitKBps = Math.Max(0, SpeedLimitKBps);
        _services.Http.Reconfigure();
        _services.Settings.Save();
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
