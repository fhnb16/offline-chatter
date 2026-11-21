using System;
using System.IO;
using System.Threading;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Core.Net;

public sealed record UpdateFileHashes(string Executable, string? Configuration)
{
    public static UpdateFileHashes Read(string exe, string? config, CancellationToken ct) =>
        new(DownloadManager.ComputeSha256(exe, ct), config == null ? null : DownloadManager.ComputeSha256(config, ct));

    public bool Matches(UpdateFileHashes actual) =>
        string.Equals(Executable, actual.Executable, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(Configuration, actual.Configuration, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Заменённые файлы можно вернуть, пока новая копия не запущена.</summary>
public sealed class UpdateInstallation
{
    private readonly string _exe;
    private readonly string _config;
    private bool _exeMoved;
    private bool _configChanged;
    private bool _hadConfig;

    internal UpdateInstallation(string exe)
    {
        _exe = exe;
        _config = exe + ".config";
    }

    internal void Apply(string newExe, string? newConfig, UpdateFileHashes? approved, CancellationToken ct)
    {
        var suffix = "." + Guid.NewGuid().ToString("N") + ".update.tmp";
        var stagedExe = _exe + suffix;
        var stagedConfig = newConfig == null ? null : _config + suffix;
        try
        {
            File.Copy(newExe, stagedExe);
            if (stagedConfig != null) File.Copy(newConfig!, stagedConfig);
            if (approved != null && !approved.Matches(UpdateFileHashes.Read(stagedExe, stagedConfig, ct)))
                throw new InvalidDataException("The verified update files have changed");
            ct.ThrowIfCancellationRequested();

            // Конфиг заменяем первым: если он занят, работающий exe останется на месте.
            File.Delete(_exe + ".old");
            if (stagedConfig != null)
            {
                _hadConfig = File.Exists(_config);
                File.Delete(_config + ".old");
                if (_hadConfig)
                {
                    File.Move(_config, _config + ".old");
                    _configChanged = true;
                }
                File.Move(stagedConfig, _config);
                _configChanged = true;
            }

            // Windows позволяет переименовать работающий exe.
            File.Move(_exe, _exe + ".old");
            _exeMoved = true;
            File.Move(stagedExe, _exe);
            Log.Info("Update", "Update files replaced", new { exe = _exe });
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            try { Rollback(); }
            catch (IOException rollback)
            {
                throw new IOException(ex.Message + Environment.NewLine + rollback.Message, new AggregateException(ex, rollback));
            }
            throw;
        }
        finally
        {
            DeleteStaged(stagedExe);
            if (stagedConfig != null) DeleteStaged(stagedConfig);
        }
    }

    public void Rollback()
    {
        Exception? failure = null;
        try
        {
            if (_exeMoved)
            {
                Restore(_exe);
                _exeMoved = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure = ex; }

        try
        {
            if (_configChanged)
            {
                if (_hadConfig) Restore(_config);
                else File.Delete(_config);
                _configChanged = false;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            failure = failure == null ? ex : new AggregateException(failure, ex);
        }

        if (failure != null)
            throw new IOException("Restore the .old files next to " + _exe + ": " + failure.Message, failure);
    }

    private static void Restore(string path)
    {
        var old = path + ".old";
        if (!File.Exists(path))
        {
            File.Move(old, path);
            return;
        }

        // На .old ещё может ссылаться запущенный процесс. Переименовать файл можно без записи в него.
        var displaced = path + "." + Guid.NewGuid().ToString("N") + ".update.tmp";
        File.Move(path, displaced);
        try { File.Move(old, path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            File.Move(displaced, path);
            throw;
        }
        DeleteStaged(displaced);
    }

    private static void DeleteStaged(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Update", "Cannot remove staged update file", ex);
        }
    }
}
