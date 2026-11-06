using System;
using System.Linq;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>
/// Публичный OpenAI-совместимый API: запущенный llama-server доступен другим приложениям
/// на постоянном порту и с постоянным секретом (Bearer). По умолчанию выключено: сервер слушает 127.0.0.1
/// на случайном порту со случайным ключом, известным только программе.
/// </summary>
public sealed partial class LocalApiViewModel : ObservableObject
{
    public const int DefaultPort = 8080;
    private readonly AppServices _s;
    private readonly EngineSettings _e;

    public LocalApiViewModel(AppServices services)
    {
        _s = services;
        _e = services.Settings.Current.Engine;
        _exposeApi = _e.ExposeApi;
        _port = (_e.Port > 0 ? _e.Port : DefaultPort).ToString(System.Globalization.CultureInfo.InvariantCulture);
        _allowLan = _e.AllowLan;
        _apiKey = Secrets.UnprotectOrEmpty(_e.ApiKeyProtected);
        services.Engine.Server.StateChanged += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(RaiseStatus));
    }

    [ObservableProperty] private bool _exposeApi;
    [ObservableProperty] private string _port;
    [ObservableProperty] private bool _allowLan;
    [ObservableProperty] private string _apiKey;
    [ObservableProperty] private bool _isKeyVisible;
    [ObservableProperty] private string? _message;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ApplyCommand))]
    private bool _isBusy;

    public string DisplayKey => IsKeyVisible || ApiKey.Length == 0 ? ApiKey : new string('•', Math.Min(24, ApiKey.Length));

    private int PortValue => int.TryParse(Port, out var p) && p is > 0 and < 65536 ? p : DefaultPort;

    public string LocalUrl => $"http://127.0.0.1:{PortValue}/v1";

    /// <summary>Адрес для других устройств в сети (первый IPv4 активного адаптера с шлюзом).</summary>
    public string? LanUrl => AllowLan && LanAddress() is { } ip ? $"http://{ip}:{PortValue}/v1" : null;

    public string Example =>
        $"curl {LocalUrl}/chat/completions -H \"Authorization: Bearer {(ApiKey.Length > 0 ? "<key>" : "-")}\" -H \"Content-Type: application/json\" -d \"{{\\\"messages\\\":[{{\\\"role\\\":\\\"user\\\",\\\"content\\\":\\\"Hi\\\"}}]}}\"";

    public string ServerStatus
    {
        get
        {
            var srv = _s.Engine.Server;
            var state = L.Get("Enum_ServerState_" + srv.State);
            if (srv.State != ServerState.Ready) return state;
            var exposed = ExposeApi && srv.Port == PortValue;
            return exposed
                ? L.F("Api_StatusExposed", state, srv.Options?.Alias ?? _s.ActiveModel?.DisplayName ?? "?", srv.Port)
                : L.F("Api_StatusPrivate", state, srv.Port);
        }
    }

    public string? ModelHint => _s.ActiveModel is { } m ? L.F("Api_ModelHint", m.DisplayName) : L.Get("Chat_NoModel");

    private void RaiseStatus()
    {
        OnPropertyChanged(nameof(ServerStatus));
        OnPropertyChanged(nameof(ModelHint));
    }

    partial void OnExposeApiChanged(bool value)
    {
        if (value)
        {
            if (ApiKey.Length == 0) ApiKey = EngineProcess.NewKey();
            if (!int.TryParse(Port, out _)) Port = DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        Commit();
        Message = L.Get(value ? "Api_ApplyHint" : "Api_DisabledHint");
    }

    partial void OnPortChanged(string value) => Commit();
    partial void OnAllowLanChanged(bool value) => Commit();
    partial void OnApiKeyChanged(string value) => Commit();
    partial void OnIsKeyVisibleChanged(bool value) => OnPropertyChanged(nameof(DisplayKey));

    private void Commit()
    {
        _e.ExposeApi = ExposeApi;
        _e.Port = PortValue;
        _e.AllowLan = AllowLan;
        _e.ApiKeyProtected = ApiKey.Length == 0 ? string.Empty : Secrets.Protect(ApiKey.Trim());
        _s.Settings.Save();
        OnPropertyChanged(nameof(DisplayKey));
        OnPropertyChanged(nameof(LocalUrl));
        OnPropertyChanged(nameof(LanUrl));
        OnPropertyChanged(nameof(Example));
        RaiseStatus();
    }

    [RelayCommand]
    private void RegenerateKey()
    {
        ApiKey = EngineProcess.NewKey();
        IsKeyVisible = true;
        Message = L.Get("Api_ApplyHint");
    }

    [RelayCommand]
    private void CopyKey() => Copy(ApiKey);

    [RelayCommand]
    private void CopyUrl() => Copy(LanUrl ?? LocalUrl);

    [RelayCommand]
    private void CopyExample() => Copy(Example.Replace("<key>", ApiKey));

    private void Copy(string text)
    {
        try
        {
            Clipboard.SetText(text);
            Message = L.Get("Chat_Copied");
        }
        catch (System.Runtime.InteropServices.COMException) { }
    }

    private bool CanApply() => !IsBusy;

    /// <summary>Применить: (пере)запустить сервер с активной моделью и текущими настройками API.</summary>
    [RelayCommand(CanExecute = nameof(CanApply))]
    private async Task ApplyAsync()
    {
        IsBusy = true;
        Message = L.F("Chat_StartingModel", _s.ActiveModel?.DisplayName ?? "?");
        try
        {
            if (ExposeApi && PortBusyByOther(PortValue))
            {
                Message = L.F("Api_PortBusy", PortValue);
                return;
            }
            var error = await Task.Run(() => _s.StartPublicApiAsync(CancellationToken.None));
            Message = error ?? (ExposeApi ? L.F("Api_Running", LanUrl ?? LocalUrl) : L.Get("Api_RunningPrivate"));
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private void StopServer()
    {
        _s.Engine.Server.Stop();
        Message = L.Get("Api_Stopped");
    }

    /// <summary>Порт занят чужим процессом (наш собственный сервер на этом порту не считается).</summary>
    private bool PortBusyByOther(int port)
    {
        if (_s.Engine.Server.State == ServerState.Ready && _s.Engine.Server.Port == port) return false;
        try
        {
            return IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(e => e.Port == port);
        }
        catch (NetworkInformationException)
        {
            return false;
        }
    }

    private static string? LanAddress()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Select(n => n.GetIPProperties())
                .Where(p => p.GatewayAddresses.Count > 0)
                .SelectMany(p => p.UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork)
                .Select(a => a.Address.ToString())
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }
    }
}
