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
                DownloadState.Running => (Job.Total is > 0 ? Math.Round(Percent) + " % · " : string.Empty) + $"{size} · {Units.FormatSpeed(Job.BytesPerSecond)}" +
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

/// <summary>Сводка по группе загрузок (например, все шарды и mmproj одной модели) для показа одной полосой.</summary>
public sealed record DownloadSummary(bool Active, bool Failed, bool Indeterminate, double Percent, string Text, string? Error);

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

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => UpdateAggregate();
        _timer.Start();
    }

    public ObservableCollection<DownloadItemViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    /// <summary>0..1 — суммарный прогресс; null — активных загрузок нет.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsActive), nameof(AggregatePercent), nameof(IsAggregateIndeterminate))]
    private double? _aggregateProgress;

    /// <summary>Строка для боковой панели: «Загрузка 34 % · файл · 12 МБ/с».</summary>
    [ObservableProperty] private string? _activeSummary;

    public bool IsActive => AggregateProgress != null;
    public double AggregatePercent => (AggregateProgress ?? 0) * 100;
    public bool IsAggregateIndeterminate => AggregateProgress is 0;

    /// <summary>Раз в полсекунды — чтобы карточки (модель, движок) обновляли свои полосы.</summary>
    public event EventHandler? Updated;

    private void UpdateAggregate()
    {
        AggregateProgress = _manager.AggregateProgress;
        var active = _manager.Jobs.Where(j => j.State is DownloadState.Running or DownloadState.Queued or DownloadState.Verifying).ToList();
        if (active.Count == 0)
        {
            ActiveSummary = null;
        }
        else
        {
            var first = active.FirstOrDefault(j => j.State == DownloadState.Running) ?? active[0];
            var name = active.Count > 1 ? L.F("Download_FilesCount", first.DisplayName, active.Count - 1) : first.DisplayName;
            var speed = active.Sum(j => j.BytesPerSecond);
            ActiveSummary = L.F("Download_SidebarSummary", Math.Round(AggregatePercent), name) + (speed > 1 ? " · " + Units.FormatSpeed(speed) : string.Empty);
        }
        Updated?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Сводка по группе: общий процент по байтам, скорость, оставшееся время. null — таких загрузок нет.</summary>
    public DownloadSummary? Summarize(string group)
    {
        var jobs = _manager.Jobs.Where(j => j.Group == group).ToList();
        if (jobs.Count == 0) return null;
        var active = jobs.Any(j => !j.IsFinished);
        var failed = jobs.FirstOrDefault(j => j.State == DownloadState.Failed);
        long total = 0, received = 0;
        foreach (var j in jobs)
        {
            total += j.Total ?? j.ExpectedSize ?? 0;
            received += j.State == DownloadState.Completed ? (j.Total ?? j.ExpectedSize ?? j.Received) : j.Received;
        }
        var speed = jobs.Where(j => j.State == DownloadState.Running).Sum(j => j.BytesPerSecond);
        var percent = total > 0 ? Math.Min(100, 100.0 * received / total) : 0;
        string text;
        if (jobs.All(j => j.State == DownloadState.Completed)) text = L.Get("Download_GroupDone");
        else if (jobs.Any(j => j.State == DownloadState.Verifying)) text = L.Get("Download_Verifying");
        else if (failed != null && !jobs.Any(j => j.State is DownloadState.Running or DownloadState.Queued)) text = L.Get("Download_Failed");
        else if (jobs.All(j => j.State is DownloadState.Paused or DownloadState.Completed)) text = L.Get("Enum_DownloadState_Paused") + " · " + Math.Round(percent) + " %";
        else
        {
            var eta = speed > 1 && total > received ? " · " + L.F("Download_Eta", Units.FormatDuration(TimeSpan.FromSeconds((total - received) / speed))) : string.Empty;
            text = $"{Math.Round(percent)} % · {Units.FormatBytes(received)} / {Units.FormatBytes(total)}" + (speed > 1 ? " · " + Units.FormatSpeed(speed) : string.Empty) + eta;
        }
        return new DownloadSummary(active, failed != null, active && total == 0, percent, text, failed?.Error);
    }

    public void CancelGroup(string group)
    {
        foreach (var j in _manager.Jobs.Where(j => j.Group == group && !j.IsFinished)) _manager.Cancel(j);
    }

    public void PauseOrResumeGroup(string group)
    {
        var jobs = _manager.Jobs.Where(j => j.Group == group && !j.IsFinished || j.Group == group && j.State == DownloadState.Failed).ToList();
        if (jobs.Any(j => j.State is DownloadState.Running or DownloadState.Queued))
            foreach (var j in jobs.Where(j => j.State is DownloadState.Running or DownloadState.Queued)) _manager.Pause(j);
        else
            foreach (var j in jobs.Where(j => j.State is DownloadState.Paused or DownloadState.Failed)) _manager.Resume(j);
    }

    [RelayCommand]
    private void ClearFinished()
    {
        _manager.ClearFinished();
        foreach (var done in Items.Where(i => i.IsFinished).ToList()) Items.Remove(done);
        OnPropertyChanged(nameof(HasItems));
    }
}
