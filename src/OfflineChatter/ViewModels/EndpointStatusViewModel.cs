using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>
/// Состояние подключения к эндпоинту выбранной модели: для локальной — llama-server, для API — провайдер
/// (связь, ключ, отклик). Показывается в шапке чата и блоком «Подключение» правой панели.
/// Проверка API — лёгкий запрос списка моделей: при выборе модели, по щелчку и раз в минуту, пока открыт чат;
/// результат каждого настоящего запроса учитывается без отдельной проверки.
/// </summary>
public sealed partial class EndpointStatusViewModel : ObservableObject
{
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromSeconds(60);

    private readonly AppServices _s;
    private readonly ServerInsightViewModel _server;
    private readonly Dictionary<string, EndpointStatus> _known = new();
    private readonly DispatcherTimer _timer;
    private CancellationTokenSource? _probeCts;
    private string? _modelRef;
    private string? _modelTitle;
    private bool _checking;

    public EndpointStatusViewModel(AppServices services, ServerInsightViewModel server)
    {
        _s = services;
        _server = server;
        Group = new InsightGroup(L.Get("Inspector_Section_Connection"));
        _server.PropertyChanged += (_, e) =>
        {
            if (!IsApi && e.PropertyName is nameof(ServerInsightViewModel.PillText) or nameof(ServerInsightViewModel.State)) Refresh();
        };
        _s.Providers.Changed += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            // Адрес или ключ могли поменять — прежний результат больше ничего не значит
            _known.Clear();
            Refresh();
            if (IsApi && Active) _ = CheckAsync();
        }));
        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(15) };
        _timer.Tick += (_, _) =>
        {
            if (Active && IsApi && !Busy && !_checking && IsStale) _ = CheckAsync();
        };
        Refresh();
    }

    /// <summary>Блок «Подключение» для правой панели.</summary>
    public InsightGroup Group { get; }

    /// <summary>Выбрана модель API-провайдера, а не локальная.</summary>
    public bool IsApi => _modelRef?.StartsWith("provider:", StringComparison.Ordinal) == true;

    /// <summary>Страница чата открыта — можно проверять связь по таймеру.</summary>
    public bool Active
    {
        get => _timer.IsEnabled;
        set
        {
            _timer.IsEnabled = value;
            if (value && IsApi && !_checking && IsStale) _ = CheckAsync();
        }
    }

    /// <summary>Идёт генерация: отдельная проверка не нужна, состояние покажет сам запрос.</summary>
    public bool Busy { get; set; }

    /// <summary>Цвет точки состояния: Ready, Starting, Failed или Stopped (как у llama-server).</summary>
    [ObservableProperty] private string _dotState = "Stopped";

    /// <summary>Шапка чата: «подключено · OpenRouter · 180 мс» или «готов · b7083 · Vulkan».</summary>
    [ObservableProperty] private string _pillText = string.Empty;

    /// <summary>Подсказка к шапке.</summary>
    [ObservableProperty] private string _summary = string.Empty;

    /// <summary>Боковая панель, первая строка: модель.</summary>
    [ObservableProperty] private string _title = string.Empty;

    /// <summary>Боковая панель, вторая строка: «b7083 · Vulkan · готов» или «OpenRouter · подключено».</summary>
    [ObservableProperty] private string _line = string.Empty;

    private ProviderConfig? Provider
    {
        get
        {
            if (!IsApi) return null;
            var parts = _modelRef!.Split(new[] { ':' }, 3);
            return _s.Providers.Find(parts.Length > 1 ? parts[1] : null);
        }
    }

    private string? ApiModel => IsApi && _modelRef!.Split(new[] { ':' }, 3) is { Length: > 2 } p ? p[2] : null;

    private EndpointStatus? Status => Provider is { } p && _known.TryGetValue(p.Id, out var s) ? s : null;

    private bool IsStale => Status is not { } s || DateTime.Now - s.CheckedAt > ProbeInterval;

    /// <summary>Выбрана другая модель (или чат с другой моделью).</summary>
    /// <param name="title">Название выбранной модели: пока сервер не запущен, показываем её, а не модель по умолчанию.</param>
    public void SetTarget(string? modelRef, string? title)
    {
        if (_modelRef == modelRef && _modelTitle == title) return;
        _modelTitle = title;
        if (_modelRef == modelRef)
        {
            Refresh();
            return;
        }
        _modelRef = modelRef;
        _probeCts?.Cancel();
        _checking = false;
        OnPropertyChanged(nameof(IsApi));
        Refresh();
        if (IsApi && Active && IsStale) _ = CheckAsync();
    }

    /// <summary>Итог настоящего запроса чата к провайдеру: связь проверена им самим.</summary>
    public void ReportRequest(string? modelRef, Exception? error, double? firstTokenMs)
    {
        if (modelRef?.StartsWith("provider:", StringComparison.Ordinal) != true) return;
        var parts = modelRef.Split(new[] { ':' }, 3);
        if (parts.Length < 2 || _s.Providers.Find(parts[1]) is not { } p) return;
        // Отклик запроса чата — это время до первого токена, а не до ответа сервера: прежний замер оставляем
        var latency = _known.TryGetValue(p.Id, out var old) ? old.LatencyMs : null;
        _known[p.Id] = EndpointStatus.FromRequest(error, error == null ? latency : null);
        Refresh();
    }

    /// <summary>Проверить подключение сейчас.</summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        var provider = Provider;
        if (provider == null) return;
        _probeCts?.Cancel();
        using var cts = _probeCts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var target = _modelRef;
        _checking = true;
        Refresh();
        try
        {
            var client = ChatClient.ForHttpService(_s.Http, provider.BaseUrl, Secrets.UnprotectOrEmpty(provider.ApiKeyProtected), provider.EffectiveQuirks, provider.Name);
            var status = await client.ProbeAsync(cts.Token);
            // Unknown — проверку прервали: сменой модели (результат не нужен) или своим тайм-аутом (это «нет связи»)
            if (status.State != EndpointState.Unknown) _known[provider.Id] = status;
            else if (target == _modelRef && ReferenceEquals(_probeCts, cts))
                _known[provider.Id] = new EndpointStatus(EndpointState.Unreachable, null, null, L.Get("Conn_Timeout"));
        }
        finally
        {
            if (ReferenceEquals(_probeCts, cts)) _probeCts = null;
            if (target == _modelRef)
            {
                _checking = false;
                Refresh();
            }
        }
    }

    private void Refresh()
    {
        var rows = new List<InsightRowData>();
        if (!IsApi)
        {
            var running = _server.IsRunning;
            var srv = _s.Engine.Server;
            DotState = _server.State switch
            {
                ServerState.Ready => "Ready",
                ServerState.Starting or ServerState.Loading => "Starting",
                ServerState.Failed => "Failed",
                _ => "Stopped",
            };
            // Сервер запущен — модель, которая в нём загружена; остановлен — та, что выбрана в чате и загрузится при отправке
            var localModel = running ? _server.ModelTitle : _modelTitle ?? _server.ModelTitle;
            PillText = _server.PillText;
            Summary = _server.Summary;
            Title = localModel;
            Line = _server.StatusLine;
            rows.Add(new InsightRowData(L.Get("Conn_Endpoint"), "llama.cpp" + (running ? " · " + Host(srv.BaseUrl) : string.Empty), false));
            rows.Add(new InsightRowData(L.Get("Conn_State"), _server.StateText, false, _server.State == ServerState.Failed));
            rows.Add(new InsightRowData(L.Get("Conn_Model"), localModel, false));
            if (_server.ErrorText is { } err) rows.Add(new InsightRowData(L.Get("Conn_Error"), err, false, true));
            Group.Set(rows);
            return;
        }

        var p = Provider;
        if (p == null)
        {
            DotState = "Failed";
            PillText = L.Get("Chat_ProviderMissing");
            Summary = PillText;
            Title = ApiModel ?? L.Get("Model_NotSelected");
            Line = PillText;
            rows.Add(new InsightRowData(L.Get("Conn_State"), PillText, false, true));
            Group.Set(rows);
            return;
        }

        var status = Status;
        var state = _checking ? EndpointState.Checking
            : _s.Settings.Current.Network.OfflineMode ? EndpointState.Offline
            : status?.State ?? EndpointState.Unknown;
        var failed = state is EndpointState.Unauthorized or EndpointState.Unreachable or EndpointState.Error;
        var stateText = L.Get("Enum_EndpointState_" + state) + (failed && status?.HttpStatus is { } code ? " (HTTP " + code + ")" : string.Empty);
        var latency = status?.LatencyMs is { } ms && !failed ? L.F("Conn_Ms", ms.ToString("0", CultureInfo.InvariantCulture)) : null;

        DotState = state switch
        {
            EndpointState.Online => "Ready",
            EndpointState.Checking => "Starting",
            EndpointState.Unknown or EndpointState.Offline => "Stopped",
            _ => "Failed",
        };
        PillText = stateText + " · " + p.Name + (latency != null ? " · " + latency : string.Empty);
        Title = ApiModel ?? p.Name;
        Line = p.Name + " · " + stateText;

        rows.Add(new InsightRowData(L.Get("Conn_Endpoint"), p.Name + " · " + Host(p.BaseUrl), false));
        rows.Add(new InsightRowData(L.Get("Conn_State"), stateText, false, failed));
        rows.Add(new InsightRowData(L.Get("Conn_Latency"), latency ?? "—"));
        rows.Add(new InsightRowData(L.Get("Conn_Checked"), status != null ? status.CheckedAt.ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "—"));
        if (ApiModel is { } model) rows.Add(new InsightRowData(L.Get("Conn_Model"), model, false));
        if (failed && !string.IsNullOrEmpty(status?.Error)) rows.Add(new InsightRowData(L.Get("Conn_Error"), status!.Error, false, true));
        Group.Set(rows);

        var tip = new List<string> { p.Name + " · " + p.BaseUrl, L.Get("Conn_State") + ": " + stateText };
        if (latency != null) tip.Add(L.Get("Conn_Latency") + ": " + latency);
        if (failed && !string.IsNullOrEmpty(status?.Error)) tip.Add(status!.Error!);
        tip.Add(L.Get("Conn_TipOpen"));
        Summary = string.Join(Environment.NewLine, tip);
    }

    /// <summary>«api.openai.com» или «127.0.0.1:8080» — без схемы и пути.</summary>
    private static string Host(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? (u.IsDefaultPort ? u.Host : u.Host + ":" + u.Port) : url;
}
