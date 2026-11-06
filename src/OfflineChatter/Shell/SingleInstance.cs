using System;
using System.IO;
using System.IO.Pipes;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Shell;

/// <summary>
/// Один экземпляр на папку программы (портативные копии в разных папках независимы).
/// Повторный запуск передаёт аргументы первому экземпляру через именованный канал и завершается.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private readonly Mutex _mutex;
    private readonly string _pipe;
    private readonly CancellationTokenSource _cts = new();

    private SingleInstance(Mutex mutex, string pipe)
    {
        _mutex = mutex;
        _pipe = pipe;
    }

    public event Action<string[]>? ArgumentsReceived;

    private static string Key()
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(Encoding.UTF8.GetBytes(AppDomain.CurrentDomain.BaseDirectory.ToLowerInvariant() + "|" + Environment.UserName));
        return "OfflineChatter-" + BitConverter.ToString(hash, 0, 8).Replace("-", string.Empty);
    }

    /// <summary>null — уже запущен другой экземпляр (аргументы ему переданы).</summary>
    public static SingleInstance? TryStart(string[] args)
    {
        var key = Key();
        var mutex = new Mutex(true, @"Local\" + key, out var created);
        if (!created)
        {
            try
            {
                using var client = new NamedPipeClientStream(".", key, PipeDirection.Out);
                client.Connect(3000);
                var payload = Encoding.UTF8.GetBytes(string.Join("\0", args.DefaultIfEmpty("--activate")));
                client.Write(payload, 0, payload.Length);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or UnauthorizedAccessException)
            {
                Log.Warn("App", "Cannot reach the running instance", ex);
            }
            mutex.Dispose();
            return null;
        }
        var instance = new SingleInstance(mutex, key);
        _ = Task.Run(instance.ListenAsync);
        return instance;
    }

    private async Task ListenAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                using var server = new NamedPipeServerStream(_pipe, PipeDirection.In, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
                await Task.Factory.FromAsync(server.BeginWaitForConnection, server.EndWaitForConnection, null).ConfigureAwait(false);
                using var ms = new MemoryStream();
                await server.CopyToAsync(ms).ConfigureAwait(false);
                var text = Encoding.UTF8.GetString(ms.ToArray());
                ArgumentsReceived?.Invoke(text.Split('\0').Where(a => a.Length > 0).ToArray());
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or UnauthorizedAccessException)
            {
                if (_cts.IsCancellationRequested) return;
                Log.Warn("App", "Single-instance pipe error", ex);
                await Task.Delay(500).ConfigureAwait(false);
            }
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _mutex.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex.Dispose();
    }
}
