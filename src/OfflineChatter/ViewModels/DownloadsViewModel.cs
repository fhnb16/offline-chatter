using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

/// <summary>Обёртка загрузки для UI: события из фонового потока переводятся в UI-поток.</summary>
public sealed partial class DownloadItemViewModel : ObservableObject
{
    private readonly DownloadManager _manager;

    public DownloadItemViewModel(DownloadJob job, DownloadManager manager)
    {
        Job = job;
        _manager = manager;
        job.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(Refresh), DispatcherPriority.Background);
    }

    public DownloadJob Job { get; }
    public string Name => Job.DisplayName;
    public double Percent => Job.Progress * 100;
    public bool IsIndeterminate => Job.State == DownloadState.Running && Job.Total is null or 0;
    public bool CanPause => Job.State is DownloadState.Running or DownloadState.Queued;
    public bool CanResume => Job.State is DownloadState.Paused or DownloadState.Failed;
    public bool CanCancel => !Job.IsFinished;
    public bool IsFinished => Job.IsFinished;

    public string Status
    {
        get
        {
            var size = Job.Total is > 0
                ? $"{Units.FormatBytes(Job.Received)} / {Units.FormatBytes(Job.Total.Value)}"
                : Units.FormatBytes(Job.Received);
            return Job.State switch
            {
                DownloadState.Running => $"{size} · {Units.FormatSpeed(Job.BytesPerSecond)}" +
                                         (Job.Eta is { } eta ? " · " + L.F("Download_Eta", Units.FormatDuration(eta)) : string.Empty),
                DownloadState.Verifying => L.Get("Download_Verifying") + " · " + size,
                DownloadState.Failed => L.Get("Download_Failed") + ": " + Job.Error,
                _ => L.Get("Enum_DownloadState_" + Job.State) + (Job.State == DownloadState.Completed ? string.Empty : " · " + size),
            };
        }
    }

    public void Refresh()
    {
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void Pause() => _manager.Pause(Job);

    [RelayCommand]
    private void Resume() => _manager.Resume(Job);

    [RelayCommand]
    private void Cancel() => _manager.Cancel(Job);
}

/// <summary>Список загрузок (общий для разделов «Модели» и «Движок») + прогресс для панели задач.</summary>
public sealed partial class DownloadsViewModel : ObservableObject
{
    private readonly DownloadManager _manager;
    private readonly DispatcherTimer _timer;

    public DownloadsViewModel(DownloadManager manager)
    {
        _manager = manager;
        foreach (var j in manager.Jobs) Items.Add(new DownloadItemViewModel(j, manager));
        manager.JobAdded += (_, job) => Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            Items.Insert(0, new DownloadItemViewModel(job, manager));
            OnPropertyChanged(nameof(HasItems));
        }));

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(500) };
        _timer.Tick += (_, _) => UpdateAggregate();
        _timer.Start();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    /// <summary>0..1 — суммарный прогресс; null — активных загрузок нет.</summary>
    [ObservableProperty] private double? _aggregateProgress;

    private void UpdateAggregate() => AggregateProgress = _manager.AggregateProgress;

    [RelayCommand]
    private void ClearFinished()
    {
        _manager.ClearFinished();
        foreach (var done in Items.Where(i => i.IsFinished).ToList()) Items.Remove(done);
        OnPropertyChanged(nameof(HasItems));
    }
}
