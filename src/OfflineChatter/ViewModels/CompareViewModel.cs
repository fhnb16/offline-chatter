using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>Одна колонка сравнения: модель, ответ, статистика.</summary>
public sealed partial class CompareSide : ObservableObject
{
    [ObservableProperty] private ModelOption? _model;
    [ObservableProperty] private string _answer = string.Empty;
    [ObservableProperty] private string? _stats;
    [ObservableProperty] private bool _isStreaming;
}

/// <summary>Режим сравнения: один промпт двум моделям (локальным или API), ответы рядом со статистикой.</summary>
public sealed partial class CompareViewModel : PageViewModel
{
    private readonly AppServices _s;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _cts;

    public CompareViewModel(AppServices services, MainViewModel main)
    {
        _s = services;
        _main = main;
    }

    public override PageKind Kind => PageKind.Compare;

    public ObservableCollection<ModelOption> Models => _main.Chat.ModelOptions;
    public CompareSide Left { get; } = new();
    public CompareSide Right { get; } = new();

    [ObservableProperty] private string _prompt = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand))]
    private bool _isRunning;

    [ObservableProperty] private string? _status;

    public override void OnNavigatedTo()
    {
        Left.Model ??= _main.Chat.SelectedModel ?? (Models.Count > 0 ? Models[0] : null);
        Right.Model ??= Models.Count > 1 ? Models[1] : Left.Model;
    }

    private bool CanRun() => !IsRunning;

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync()
    {
        if (string.IsNullOrWhiteSpace(Prompt) || Left.Model == null || Right.Model == null) return;
        IsRunning = true;
        _cts = new CancellationTokenSource();
        try
        {
            // Две локальные модели одновременно не поместятся в один llama-server — по очереди
            var bothLocal = Left.Model.Ref.StartsWith("local:", StringComparison.Ordinal) && Right.Model.Ref.StartsWith("local:", StringComparison.Ordinal);
            if (bothLocal)
            {
                await RunSideAsync(Left, _cts.Token);
                await RunSideAsync(Right, _cts.Token);
            }
            else
            {
                await Task.WhenAll(RunSideAsync(Left, _cts.Token), RunSideAsync(Right, _cts.Token));
            }
            Status = null;
        }
        catch (OperationCanceledException) { }
        finally
        {
            IsRunning = false;
            _cts.Dispose();
            _cts = null;
        }
    }

    [RelayCommand]
    private void Stop() => _cts?.Cancel();

    private async Task RunSideAsync(CompareSide side, CancellationToken ct)
    {
        side.Answer = string.Empty;
        side.Stats = null;
        side.IsStreaming = true;
        var sb = new StringBuilder();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(40) };
        timer.Tick += (_, _) => { lock (sb) side.Answer = sb.ToString(); };
        timer.Start();
        try
        {
            var (client, model, error) = await _s.ResolveChatClientAsync(side.Model!.Ref, s => Status = s, ct);
            if (client == null)
            {
                side.Stats = error;
                return;
            }
            var req = new ChatRequest { Model = model, Thinking = false };
            req.Messages.Add(new ChatMessage { Role = ChatRole.User, Content = Prompt });
            var r = await client.StreamAsync(req, d => { if (d.Content != null) lock (sb) sb.Append(d.Content); }, ct);
            side.Answer = r.Content;
            var s = r.Stats;
            side.Stats = string.Join(" · ", new[]
            {
                s.TokensPerSecond is { } tps ? L.F("Stats_Gen", tps.ToString("0.0")) : null,
                s.CompletionTokens > 0 ? L.F("Stats_Tokens", s.CompletionTokens) : null,
                L.F("Stats_Ttft", Units.FormatDuration(TimeSpan.FromMilliseconds(s.TimeToFirstTokenMs))),
                Units.FormatDuration(TimeSpan.FromMilliseconds(s.TotalMs)),
            }.Where(x => x != null));
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or System.IO.IOException or InvalidOperationException)
        {
            side.Stats = ex is HttpStatusException hs ? $"HTTP {(int)hs.Status}: {hs.Body}" : ex.GetBaseException().Message;
        }
        finally
        {
            timer.Stop();
            side.IsStreaming = false;
        }
    }
}
