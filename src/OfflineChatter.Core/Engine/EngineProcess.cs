using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;

namespace OfflineChatter.Core.Engine;

public enum ServerState
{
    Stopped,
    Starting,
    Loading,
    Ready,
    Failed,
}

/// <summary>
/// Процесс llama-server: Job Object (сервер умирает вместе с оболочкой), запасной PID-файл для Win7
/// (вложенные Job там не работают), автопорт, случайный API-ключ, ожидание готовности по /health.
/// </summary>
public sealed class EngineProcess : IDisposable
{
    private const string PidFileName = "server.pid";
    private readonly string _pidFile;
    private readonly HttpClient _local;
    private readonly object _gate = new();
    private readonly LinkedList<string> _tail = new();
    private Process? _process;
    private IntPtr _job;
    private CancellationTokenSource? _watchCts;

    public EngineProcess(string engineRoot)
    {
        _pidFile = Path.Combine(engineRoot, PidFileName);
        _local = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public int Port { get; private set; }
    public string Host { get; private set; } = "127.0.0.1";
    public string? ApiKey { get; private set; }
    public EngineInstall? Engine { get; private set; }
    public ServerLaunchOptions? Options { get; private set; }
    public DateTime? StartedAt { get; private set; }
    public TimeSpan? LoadTime { get; private set; }
    public int? Pid => _process is { HasExited: false } p ? p.Id : null;

    /// <summary>Аргументы последнего запуска (ключ замаскирован) — для превью и отчётов.</summary>
    public string? CommandLine { get; private set; }

    public string BaseUrl => $"http://{(Host == "0.0.0.0" ? "127.0.0.1" : Host)}:{Port}";

    /// <summary>Аргументы, пропущенные из-за отсутствия в этой версии llama-server.</summary>
    public IReadOnlyList<string> SkippedFlags { get; private set; } = Array.Empty<string>();

    public event EventHandler? StateChanged;
    public event EventHandler<string>? OutputLine;

    /// <summary>Последние строки вывода сервера (для диагностики ошибок запуска).</summary>
    public string Tail
    {
        get { lock (_tail) return string.Join(Environment.NewLine, _tail); }
    }

    /// <param name="fixedKey">Постоянный ключ (публичный API) или null — случайный на каждый запуск.</param>
    /// <param name="host">Адрес прослушивания: 127.0.0.1, 0.0.0.0 или IPv4 адаптера.</param>
    public async Task StartAsync(EngineInstall engine, ServerLaunchOptions options, FlagCatalog? flags, string host, int port, CancellationToken ct, string? fixedKey = null)
    {
        Stop();
        lock (_tail) _tail.Clear();
        Engine = engine;
        Options = options;
        LastError = null;
        Host = string.IsNullOrWhiteSpace(host) ? EngineSettings.Loopback : host;
        Port = port > 0 ? port : FreePort();
        ApiKey = string.IsNullOrEmpty(fixedKey) ? NewKey() : fixedKey;
        SetState(ServerState.Starting);

        var skipped = new List<string>();
        var args = options.BuildArgs(Host, Port, ApiKey, flags, skipped);
        SkippedFlags = skipped;
        var cmd = ServerLaunchOptions.JoinCommandLine(args);
        CommandLine = cmd.Replace(ApiKey, "***");
        Log.Info("Engine", "Starting llama-server", new { engine = engine.Directory, args = CommandLine, skipped });

        var psi = new ProcessStartInfo(engine.ServerExe, cmd)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            WorkingDirectory = engine.Directory,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.EnvironmentVariables["LLAMA_LOG_COLORS"] = "0";

        var p = new Process { StartInfo = psi, EnableRaisingEvents = true };
        p.OutputDataReceived += (_, e) => OnLine(e.Data);
        p.ErrorDataReceived += (_, e) => OnLine(e.Data);
        p.Exited += (_, _) => OnExited(p);

        try
        {
            p.Start();
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            Fail("Cannot start llama-server: " + ex.Message);
            return;
        }

        _process = p;
        StartedAt = DateTime.Now;
        AttachToJob(p);
        WritePidFile(p);
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        _watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await WaitReadyAsync(p, _watchCts.Token).ConfigureAwait(false);
    }

    private async Task WaitReadyAsync(Process p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        while (!ct.IsCancellationRequested && !p.HasExited)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + "/health");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ApiKey);
                using var resp = await _local.SendAsync(req, ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    LoadTime = sw.Elapsed;
                    Log.Info("Engine", "llama-server ready", new { port = Port, loadMs = (long)sw.Elapsed.TotalMilliseconds });
                    SetState(ServerState.Ready);
                    return;
                }
                if (resp.StatusCode == HttpStatusCode.ServiceUnavailable && State != ServerState.Loading)
                    SetState(ServerState.Loading);
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // сервер ещё не слушает порт
            }
            try { await Task.Delay(300, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
        }
    }

    private void OnLine(string? line)
    {
        if (line == null) return;
        lock (_tail)
        {
            _tail.AddLast(line);
            while (_tail.Count > 200) _tail.RemoveFirst();
        }
        OutputLine?.Invoke(this, line);
    }

    private void OnExited(Process p)
    {
        if (!ReferenceEquals(p, _process)) return;
        int code;
        try { code = p.ExitCode; } catch (InvalidOperationException) { code = -1; }
        DeletePidFile();
        if (State is ServerState.Stopped) return;
        var lastLines = string.Join(Environment.NewLine, Enumerable.Reverse(Enumerable.Reverse(Tail.Split('\n')).Take(15)));
        Fail($"llama-server exited with code {code} (0x{code:X8})" + (lastLines.Length > 0 ? Environment.NewLine + lastLines : string.Empty));
    }

    private void Fail(string message)
    {
        LastError = message;
        Log.Error("Engine", message);
        SetState(ServerState.Failed);
    }

    public void Stop()
    {
        _watchCts?.Cancel();
        var p = _process;
        _process = null;
        if (p != null)
        {
            SetState(ServerState.Stopped);
            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(5000);
                }
            }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
                Log.Warn("Engine", "Error while stopping llama-server", ex);
            }
            p.Dispose();
            DeletePidFile();
            Log.Info("Engine", "llama-server stopped");
        }
        if (_job != IntPtr.Zero)
        {
            Native.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
        if (State != ServerState.Stopped) SetState(ServerState.Stopped);
    }

    private void SetState(ServerState s)
    {
        State = s;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ---------------- Job Object и PID-файл ----------------

    private void AttachToJob(Process p)
    {
        try
        {
            _job = Native.CreateJobObject(IntPtr.Zero, null);
            if (_job == IntPtr.Zero) return;
            var info = new Native.JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            var size = Marshal.SizeOf(info);
            var ptr = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, ptr, false);
                if (!Native.SetInformationJobObject(_job, 9, ptr, (uint)size)) // JobObjectExtendedLimitInformation
                    Log.Warn("Engine", "SetInformationJobObject failed: " + Marshal.GetLastWin32Error());
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }

            if (!Native.AssignProcessToJobObject(_job, p.Handle))
            {
                // Win7: процесс оболочки уже в чужом Job (PCA, лаунчер) — вложенные Job не поддерживаются.
                // Остаётся PID-файл и очистка «сирот» при следующем запуске.
                Log.Warn("Engine", "AssignProcessToJobObject failed (nested jobs unsupported?): " + Marshal.GetLastWin32Error());
            }
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Warn("Engine", "Job objects unavailable", ex);
        }
    }

    private void WritePidFile(Process p)
    {
        try
        {
            File.WriteAllText(_pidFile, p.Id.ToString(CultureInfo.InvariantCulture) + "\n" + p.StartInfo.FileName);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warn("Engine", "Cannot write pid file", ex);
        }
    }

    private void DeletePidFile()
    {
        try { File.Delete(_pidFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Убить llama-server, оставшийся от прошлого запуска (оболочка упала, Job не сработал).</summary>
    public static void KillOrphan(string engineRoot)
    {
        var pidFile = Path.Combine(engineRoot, PidFileName);
        if (!File.Exists(pidFile)) return;
        try
        {
            var lines = File.ReadAllLines(pidFile);
            if (lines.Length >= 2 && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
            {
                try
                {
                    using var p = Process.GetProcessById(pid);
                    var exe = p.MainModule?.FileName;
                    if (exe != null && string.Equals(Path.GetFullPath(exe), Path.GetFullPath(lines[1]), StringComparison.OrdinalIgnoreCase))
                    {
                        p.Kill();
                        Log.Warn("Engine", "Killed orphaned llama-server", null, new { pid });
                    }
                }
                catch (ArgumentException) { /* процесса уже нет */ }
                catch (System.ComponentModel.Win32Exception) { }
            }
            File.Delete(pidFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warn("Engine", "Orphan cleanup failed", ex);
        }
    }

    // ---------------- Утилиты ----------------

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    public static string NewKey()
    {
        var bytes = new byte[24];
        using var rng = RandomNumberGenerator.Create();
        rng.GetBytes(bytes);
        return "oc-" + BitConverter.ToString(bytes).Replace("-", string.Empty).ToLowerInvariant();
    }

    public void Dispose()
    {
        Stop();
        _local.Dispose();
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct IO_COUNTERS
        {
            public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        public static extern IntPtr CreateJobObject(IntPtr attrs, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint length);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static extern bool CloseHandle(IntPtr h);
    }
}
