using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Net;

public enum DownloadState
{
    Queued,
    Running,
    Paused,
    Verifying,
    Completed,
    Failed,
    Canceled,
}

/// <summary>Одна загрузка. Свойства меняются из фонового потока — UI подписывается на <see cref="Changed"/>.</summary>
public sealed class DownloadJob
{
    internal CancellationTokenSource? Cts;
    internal bool PauseRequested;
    internal readonly TaskCompletionSource<bool> Completion = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public DownloadJob(string url, string targetPath, string displayName)
    {
        Url = url;
        TargetPath = targetPath;
        DisplayName = displayName;
    }

    public Guid Id { get; } = Guid.NewGuid();
    public string Url { get; }
    public string TargetPath { get; }
    public string DisplayName { get; }

    /// <summary>Группа (например, id модели) — чтобы показать загрузки модели и mmproj вместе.</summary>
    public string? Group { get; init; }

    public long? ExpectedSize { get; init; }

    /// <summary>Ожидаемый SHA-256 (hex). Если задан — файл проверяется после загрузки.</summary>
    public string? ExpectedSha256 { get; init; }

    /// <summary>Дополнительные заголовки (например, Authorization для HF).</summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>();

    public DownloadState State { get; internal set; } = DownloadState.Queued;
    public long Received { get; internal set; }
    public long? Total { get; internal set; }
    public double BytesPerSecond { get; internal set; }
    public string? Error { get; internal set; }

    public double Progress => Total is > 0 ? Math.Min(1.0, (double)Received / Total.Value) : 0;

    public TimeSpan? Eta => BytesPerSecond > 1 && Total is > 0 ? TimeSpan.FromSeconds((Total.Value - Received) / BytesPerSecond) : null;

    public string PartPath => TargetPath + ".part";

    public bool IsFinished => State is DownloadState.Completed or DownloadState.Failed or DownloadState.Canceled;

    /// <summary>Завершение: true — файл на месте, false — ошибка или отмена.</summary>
    public Task<bool> WhenFinished => Completion.Task;

    public event EventHandler? Changed;

    internal void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);
}

/// <summary>
/// Очередь загрузок: докачка через Range в *.part, пауза/продолжение, ограничение скорости,
/// проверка свободного места и SHA-256, повтор при обрывах.
/// </summary>
public sealed class DownloadManager
{
    private const int BufferSize = 1024 * 1024;
    private const int MaxRetries = 6;
    private static readonly TimeSpan InactivityTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan ProgressInterval = TimeSpan.FromMilliseconds(250);

    private readonly HttpService _http;
    private readonly NetworkSettings _settings;
    private readonly List<DownloadJob> _jobs = new();
    private readonly object _gate = new();
    private int _running;

    public DownloadManager(HttpService http, NetworkSettings settings)
    {
        _http = http;
        _settings = settings;
    }

    public event EventHandler<DownloadJob>? JobAdded;
    public event EventHandler<DownloadJob>? JobChanged;

    public IReadOnlyList<DownloadJob> Jobs
    {
        get { lock (_gate) return _jobs.ToList(); }
    }

    /// <summary>Суммарный прогресс активных загрузок (для панели задач). null — активных нет.</summary>
    public double? AggregateProgress
    {
        get
        {
            lock (_gate)
            {
                var active = _jobs.Where(j => j.State is DownloadState.Running or DownloadState.Queued or DownloadState.Verifying).ToList();
                if (active.Count == 0) return null;
                long total = 0, received = 0;
                foreach (var j in active)
                {
                    total += j.Total ?? j.ExpectedSize ?? 0;
                    received += j.Received;
                }
                return total > 0 ? (double)received / total : 0;
            }
        }
    }

    public DownloadJob Enqueue(DownloadJob job)
    {
        lock (_gate)
        {
            var existing = _jobs.FirstOrDefault(j => !j.IsFinished && string.Equals(j.TargetPath, job.TargetPath, StringComparison.OrdinalIgnoreCase));
            if (existing != null) return existing;
            _jobs.Add(job);
        }
        JobAdded?.Invoke(this, job);
        Pump();
        return job;
    }

    public void Pause(DownloadJob job)
    {
        if (job.State is not (DownloadState.Running or DownloadState.Queued)) return;
        job.PauseRequested = true;
        if (job.State == DownloadState.Queued)
        {
            SetState(job, DownloadState.Paused);
            return;
        }
        job.Cts?.Cancel();
    }

    public void Resume(DownloadJob job)
    {
        if (job.State is not (DownloadState.Paused or DownloadState.Failed)) return;
        job.PauseRequested = false;
        job.Error = null;
        SetState(job, DownloadState.Queued);
        Pump();
    }

    public void Cancel(DownloadJob job, bool deletePartial = true)
    {
        job.PauseRequested = false;
        var wasRunning = job.State == DownloadState.Running;
        SetState(job, DownloadState.Canceled);
        job.Cts?.Cancel();
        if (!wasRunning) Finish(job, false);
        if (deletePartial) TryDelete(job.PartPath);
    }

    /// <summary>Убрать завершённые загрузки из списка.</summary>
    public void ClearFinished()
    {
        lock (_gate) _jobs.RemoveAll(j => j.IsFinished);
    }

    private void Pump()
    {
        while (true)
        {
            DownloadJob? next;
            lock (_gate)
            {
                if (_running >= Math.Max(1, _settings.MaxConcurrentDownloads)) return;
                next = _jobs.FirstOrDefault(j => j.State == DownloadState.Queued);
                if (next == null) return;
                _running++;
                next.State = DownloadState.Running;
            }
            next.RaiseChanged();
            JobChanged?.Invoke(this, next);
            _ = RunAsync(next);
        }
    }

    private async Task RunAsync(DownloadJob job)
    {
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                job.Cts = new CancellationTokenSource();
                try
                {
                    await DownloadOnceAsync(job, job.Cts.Token).ConfigureAwait(false);
                    if (!string.IsNullOrEmpty(job.ExpectedSha256))
                    {
                        SetState(job, DownloadState.Verifying);
                        await VerifyAsync(job, job.Cts.Token).ConfigureAwait(false);
                    }
                    MoveIntoPlace(job);
                    SetState(job, DownloadState.Completed);
                    Log.Info("Download", "Completed " + job.DisplayName, new { job.TargetPath, size = job.Received });
                    Finish(job, true);
                    return;
                }
                catch (OperationCanceledException)
                {
                    if (job.State == DownloadState.Canceled)
                    {
                        Finish(job, false);
                        return;
                    }
                    if (job.PauseRequested)
                    {
                        SetState(job, DownloadState.Paused);
                        return;
                    }
                    if (attempt >= MaxRetries) throw new IOException("Download stalled (no data for a minute)");
                    Log.Warn("Download", $"Stalled, retrying ({attempt}) {job.DisplayName}");
                }
                catch (Exception ex) when (IsTransient(ex) && attempt < MaxRetries)
                {
                    Log.Warn("Download", $"Transient error, retrying ({attempt}) {job.DisplayName}", ex);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 << attempt))).ConfigureAwait(false);
                }
                finally
                {
                    job.Cts?.Dispose();
                    job.Cts = null;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or HttpRequestException or UnauthorizedAccessException or InvalidDataException)
        {
            job.Error = ex.Message;
            SetState(job, DownloadState.Failed);
            Log.Error("Download", "Failed " + job.DisplayName, ex, new { job.Url });
            Finish(job, false);
        }
        finally
        {
            lock (_gate) _running--;
            Pump();
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        NotEnoughSpaceException or InvalidDataException => false,
        HttpStatusException hs => (int)hs.Status is 429 or 500 or 502 or 503 or 504,
        HttpRequestException or IOException => true,
        _ => false,
    };

    private async Task DownloadOnceAsync(DownloadJob job, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(job.TargetPath))!);
        var existing = File.Exists(job.PartPath) ? new FileInfo(job.PartPath).Length : 0;

        if (job.ExpectedSize is { } expected)
        {
            if (existing > expected)
            {
                TryDelete(job.PartPath);
                existing = 0;
            }
            EnsureFreeSpace(job.TargetPath, expected - existing);
            if (existing == expected)
            {
                job.Received = expected;
                job.Total = expected;
                return;
            }
        }

        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var response = await _http.SendAsync(() =>
        {
            var r = new HttpRequestMessage(HttpMethod.Get, job.Url);
            if (existing > 0) r.Headers.Range = new RangeHeaderValue(existing, null);
            foreach (var h in job.Headers) r.Headers.TryAddWithoutValidation(h.Key, h.Value);
            return r;
        }, HttpCompletionOption.ResponseHeadersRead, inactivity.Token).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && existing > 0)
        {
            // Файл уже полностью скачан (или сервер изменил файл) — проверим размер
            var total = response.Content.Headers.ContentRange?.Length;
            if (total == existing || job.ExpectedSize == existing)
            {
                job.Received = existing;
                job.Total = existing;
                return;
            }
            TryDelete(job.PartPath);
            throw new IOException("Server rejected resume range; restarting");
        }

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            throw new HttpStatusException(response.StatusCode, job.Url, body);
        }

        var append = response.StatusCode == HttpStatusCode.PartialContent && existing > 0;
        if (!append) existing = 0;
        var length = response.Content.Headers.ContentLength;
        job.Total = response.Content.Headers.ContentRange?.Length ?? (length.HasValue ? existing + length.Value : job.ExpectedSize);
        job.Received = existing;
        if (job.Total is { } t && job.ExpectedSize is null) EnsureFreeSpace(job.TargetPath, t - existing);

        using var input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        using var output = new FileStream(job.PartPath, append ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.Read, BufferSize, FileOptions.SequentialScan);

        var buffer = new byte[BufferSize];
        var speedWatch = Stopwatch.StartNew();
        var lastReport = TimeSpan.Zero;
        long windowBytes = 0;
        var windowStart = TimeSpan.Zero;

        while (true)
        {
            inactivity.CancelAfter(InactivityTimeout);
            var n = await input.ReadAsync(buffer, 0, buffer.Length, inactivity.Token).ConfigureAwait(false);
            if (n == 0) break;
            await output.WriteAsync(buffer, 0, n, ct).ConfigureAwait(false);
            job.Received += n;
            windowBytes += n;

            var now = speedWatch.Elapsed;
            if (now - lastReport >= ProgressInterval)
            {
                var span = (now - windowStart).TotalSeconds;
                if (span > 0) job.BytesPerSecond = job.BytesPerSecond <= 0 ? windowBytes / span : job.BytesPerSecond * 0.7 + windowBytes / span * 0.3;
                windowBytes = 0;
                windowStart = now;
                lastReport = now;
                job.RaiseChanged();
                JobChanged?.Invoke(this, job);
            }

            await ThrottleAsync(n, ct).ConfigureAwait(false);
        }

        await output.FlushAsync(ct).ConfigureAwait(false);
        if (job.Total is { } finalTotal && job.Received != finalTotal)
            throw new IOException($"Incomplete download: {job.Received} of {finalTotal} bytes");
        if (job.ExpectedSize is { } exp && job.Received != exp)
            throw new InvalidDataException($"Size mismatch: expected {exp}, got {job.Received}");
    }

    /// <summary>Простое ограничение скорости: делим лимит между активными загрузками.</summary>
    private async Task ThrottleAsync(int bytes, CancellationToken ct)
    {
        var limit = _settings.SpeedLimitKBps;
        if (limit <= 0) return;
        int running;
        lock (_gate) running = Math.Max(1, _running);
        var perJob = limit * 1024.0 / running;
        var delayMs = bytes / perJob * 1000.0;
        if (delayMs >= 1) await Task.Delay(TimeSpan.FromMilliseconds(delayMs), ct).ConfigureAwait(false);
    }

    private async Task VerifyAsync(DownloadJob job, CancellationToken ct)
    {
        var hash = await Task.Run(() => ComputeSha256(job.PartPath, ct, done =>
        {
            job.Received = done;
            job.RaiseChanged();
        }), ct).ConfigureAwait(false);

        if (!string.Equals(hash, job.ExpectedSha256!.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(job.PartPath);
            throw new InvalidDataException($"SHA-256 mismatch for {job.DisplayName}: expected {job.ExpectedSha256}, got {hash}");
        }
    }

    public static string ComputeSha256(string path, CancellationToken ct, Action<long>? progress = null)
    {
        using var sha = SHA256.Create();
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, FileOptions.SequentialScan);
        var buffer = new byte[BufferSize];
        long done = 0;
        var sw = Stopwatch.StartNew();
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.TransformBlock(buffer, 0, n, null, 0);
            done += n;
            if (sw.ElapsedMilliseconds > 250)
            {
                progress?.Invoke(done);
                sw.Restart();
            }
        }
        sha.TransformFinalBlock(Array.Empty<byte>(), 0, 0);
        return BitConverter.ToString(sha.Hash!).Replace("-", string.Empty).ToLowerInvariant();
    }

    private static void MoveIntoPlace(DownloadJob job)
    {
        if (File.Exists(job.TargetPath)) File.Delete(job.TargetPath);
        File.Move(job.PartPath, job.TargetPath);
    }

    private static void EnsureFreeSpace(string targetPath, long needed)
    {
        if (needed <= 0) return;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetPath));
            if (string.IsNullOrEmpty(root) || root!.StartsWith(@"\\", StringComparison.Ordinal)) return;
            var free = new DriveInfo(root).AvailableFreeSpace;
            const long reserve = 200L * 1024 * 1024;
            if (free < needed + reserve) throw new NotEnoughSpaceException(root, needed, free);
        }
        catch (ArgumentException) { }
    }

    private void SetState(DownloadJob job, DownloadState state)
    {
        job.State = state;
        if (state != DownloadState.Running) job.BytesPerSecond = 0;
        job.RaiseChanged();
        JobChanged?.Invoke(this, job);
    }

    private static void Finish(DownloadJob job, bool ok) => job.Completion.TrySetResult(ok);

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("Download", "Cannot delete " + path, ex); }
    }
}

public sealed class NotEnoughSpaceException : IOException
{
    public NotEnoughSpaceException(string drive, long needed, long free)
        : base($"Not enough disk space on {drive}: need {needed / 1048576} MB, free {free / 1048576} MB")
    {
        Drive = drive;
        Needed = needed;
        Free = free;
    }

    public string Drive { get; }
    public long Needed { get; }
    public long Free { get; }
}
