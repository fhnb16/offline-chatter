using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;

namespace OfflineChatter.Core.Images;

/// <summary>Параметры запуска sd-server, разрешённые из модели, пресета семейства и настроек.</summary>
public sealed class ImageServerOptions
{
    public Dictionary<ImageComponent, string> Files { get; set; } = new();
    public int? Threads { get; set; }
    public bool OffloadToCpu { get; set; }
    public bool ClipOnCpu { get; set; }
    public bool VaeOnCpu { get; set; }
    public bool VaeTiling { get; set; }
    public string FlashAttention { get; set; } = "auto";
    public string? WeightType { get; set; }
    public string? Rng { get; set; }
    public string? LoraDir { get; set; }
    public string? ModelArgs { get; set; }
    /// <summary>Параметры генерации по умолчанию для клиентов сервера (null — не передавать).</summary>
    public ImageGenParams? Defaults { get; set; }
    public string? ExtraArgs { get; set; }

    public static string Flag(ImageComponent c) => c switch
    {
        ImageComponent.Checkpoint => "--model",
        ImageComponent.DiffusionModel => "--diffusion-model",
        ImageComponent.Vae => "--vae",
        ImageComponent.ClipL => "--clip_l",
        ImageComponent.ClipG => "--clip_g",
        ImageComponent.T5xxl => "--t5xxl",
        ImageComponent.Llm => "--llm",
        ImageComponent.LlmVision => "--llm_vision",
        ImageComponent.ClipVision => "--clip_vision",
        _ => throw new ArgumentOutOfRangeException(nameof(c)),
    };

    /// <summary>Модель + пресет + настройки пользователя (null в настройках — значение пресета).</summary>
    public static ImageServerOptions Resolve(ImageModel model, ImageLaunchSettings s)
    {
        var p = model.Preset;
        var args = string.Join(",", new[] { p.ModelArgs, s.ModelArgs }.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!.Trim()));
        ImageGenParams? defaults = null;
        if (s.ServerDefaults)
        {
            defaults = new ImageGenParams();
            defaults.ApplyPreset(p);
        }
        return new ImageServerOptions
        {
            Files = new Dictionary<ImageComponent, string>(model.Files),
            Threads = s.Threads,
            OffloadToCpu = s.OffloadToCpu ?? p.OffloadToCpu,
            ClipOnCpu = s.ClipOnCpu ?? p.ClipOnCpu,
            VaeOnCpu = s.VaeOnCpu ?? false,
            VaeTiling = s.VaeTiling ?? p.VaeTiling,
            FlashAttention = string.IsNullOrEmpty(s.FlashAttention) ? "auto" : s.FlashAttention,
            WeightType = string.IsNullOrWhiteSpace(s.WeightType) ? null : s.WeightType,
            Rng = string.IsNullOrWhiteSpace(s.Rng) ? null : s.Rng,
            LoraDir = string.IsNullOrWhiteSpace(s.LoraDir) ? null : s.LoraDir,
            ModelArgs = args.Length > 0 ? args : null,
            Defaults = defaults,
            ExtraArgs = s.ExtraArgs,
        };
    }

    /// <summary>
    /// Аргументы sd-server. Флаги, которых нет в справке этой версии (если она разобрана), пропускаются
    /// и попадают в <paramref name="skipped"/>.
    /// </summary>
    public List<string> BuildArgs(string host, int port, FlagCatalog? flags, List<string>? skipped = null)
    {
        var args = new List<string>();
        bool Add(string name, string? value = null, string? alt = null)
        {
            var n = name;
            if (flags != null && !flags.Supports(n))
            {
                if (alt != null && flags.Supports(alt)) n = alt;
                else
                {
                    skipped?.Add(name);
                    return false;
                }
            }
            args.Add(n);
            if (value != null) args.Add(value);
            return true;
        }
        string I(int v) => v.ToString(CultureInfo.InvariantCulture);
        string D(double v) => v.ToString("0.###", CultureInfo.InvariantCulture);

        // Порядок ролей фиксирован — превью командной строки не «прыгает»
        foreach (var c in Enum.GetValues(typeof(ImageComponent)).Cast<ImageComponent>())
            if (Files.TryGetValue(c, out var path) && !string.IsNullOrEmpty(path))
                Add(Flag(c), path, c == ImageComponent.Checkpoint ? "-m" : null);

        Add("--listen-ip", host, "--host");
        Add("--listen-port", I(port), "--port");
        if (Threads is { } t and > 0) Add("--threads", I(t), "-t");
        if (OffloadToCpu) Add("--offload-to-cpu");
        if (ClipOnCpu) Add("--clip-on-cpu");
        if (VaeOnCpu) Add("--vae-on-cpu");
        if (VaeTiling) Add("--vae-tiling");
        if (FlashAttention is "on" or "off")
        {
            // Новые сборки: --flash-attn on|off|auto; старые — флаг --diffusion-fa без значения
            if (flags == null || flags.Supports("--flash-attn")) args.AddRange(new[] { "--flash-attn", FlashAttention });
            else if (FlashAttention == "on") Add("--diffusion-fa");
        }
        if (WeightType != null) Add("--type", WeightType);
        if (Rng != null) Add("--rng", Rng);
        if (LoraDir != null) Add("--lora-model-dir", LoraDir);
        if (ModelArgs != null) Add("--model-args", ModelArgs);
        if (Defaults is { } d)
        {
            Add("-W", I(d.Width), "--width");
            Add("-H", I(d.Height), "--height");
            Add("--steps", I(d.Steps));
            Add("--cfg-scale", D(d.Cfg));
            if (!string.IsNullOrEmpty(d.Sampler)) Add("--sampling-method", d.Sampler);
            if (!string.IsNullOrEmpty(d.Scheduler)) Add("--scheduler", d.Scheduler);
            if (d.FlowShift is { } fs) Add("--flow-shift", D(fs));
        }
        if (!string.IsNullOrWhiteSpace(ExtraArgs)) args.AddRange(ServerLaunchOptions.SplitCommandLine(ExtraArgs!));
        return args;
    }
}

/// <summary>Ход генерации из вывода sd-server: «|=====>    | 3/20 - 1.23s/it».</summary>
public sealed record ImageProgress(int Step, int Total, double? SecondsPerStep)
{
    private static readonly Regex Rx = new(@"\|\s*(?<s>\d+)/(?<t>\d+)\s*-\s*(?<v>[\d.]+)\s*(?<u>s/it|it/s)", RegexOptions.CultureInvariant);

    public static ImageProgress? Parse(string line)
    {
        var m = Rx.Match(line);
        if (!m.Success) return null;
        var step = int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture);
        var total = int.Parse(m.Groups["t"].Value, CultureInfo.InvariantCulture);
        double? spi = double.TryParse(m.Groups["v"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) && v > 0
            ? m.Groups["u"].Value == "s/it" ? v : 1 / v
            : null;
        return total > 0 ? new ImageProgress(step, total, spi) : null;
    }
}

/// <summary>
/// Процесс sd-server: всегда 127.0.0.1 и случайный порт (наружу его выставляет <see cref="Net.AuthProxy"/> с ключом —
/// у самого sd-server ключа нет), Job Object, PID-файл, готовность — по ответу /v1/models (модель грузится до него).
/// </summary>
public sealed class ImageServer : IDisposable
{
    private const string PidFileName = "server.pid";
    private readonly string _pidFile;
    private readonly HttpClient _local;
    private readonly LinkedList<string> _tail = new();
    private Process? _process;
    private IntPtr _job;
    private CancellationTokenSource? _watchCts;

    public ImageServer(string root)
    {
        Directory.CreateDirectory(root);
        _pidFile = Path.Combine(root, PidFileName);
        _local = new HttpClient(new HttpClientHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(5) };
    }

    public ServerState State { get; private set; } = ServerState.Stopped;
    public string? LastError { get; private set; }
    public int Port { get; private set; }
    public string Host => "127.0.0.1";
    public string BaseUrl => $"http://127.0.0.1:{Port}";
    public SdEngineInstall? Engine { get; private set; }
    public ImageModel? Model { get; private set; }
    public ImageServerOptions? Options { get; private set; }
    public string? CommandLine { get; private set; }
    public IReadOnlyList<string> SkippedFlags { get; private set; } = Array.Empty<string>();
    public DateTime? StartedAt { get; private set; }
    public TimeSpan? LoadTime { get; private set; }
    public ImageProgress? Progress { get; private set; }

    public event EventHandler? StateChanged;
    public event EventHandler<string>? OutputLine;
    public event EventHandler<ImageProgress>? ProgressChanged;

    public string Tail
    {
        get { lock (_tail) return string.Join(Environment.NewLine, _tail); }
    }

    public async Task StartAsync(SdEngineInstall engine, ImageModel model, ImageServerOptions options, FlagCatalog? flags, CancellationToken ct)
    {
        Stop();
        lock (_tail) _tail.Clear();
        Engine = engine;
        Model = model;
        Options = options;
        LastError = null;
        LoadTime = null;
        Port = EngineProcess.FreePort();
        SetState(ServerState.Starting);

        var skipped = new List<string>();
        var args = options.BuildArgs(Host, Port, flags, skipped);
        SkippedFlags = skipped;
        CommandLine = ServerLaunchOptions.JoinCommandLine(args);
        Log.Info("ImageEngine", "Starting sd-server", new { engine = engine.Directory, args = CommandLine, skipped });

        var psi = new ProcessStartInfo(engine.ServerExe, CommandLine)
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
            Fail("Cannot start sd-server: " + ex.Message);
            return;
        }
        _process = p;
        StartedAt = DateTime.Now;
        AttachToJob(p);
        try { File.WriteAllText(_pidFile, p.Id.ToString(CultureInfo.InvariantCulture) + "\n" + engine.ServerExe); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn("ImageEngine", "Cannot write pid file", ex); }
        p.BeginOutputReadLine();
        p.BeginErrorReadLine();

        _watchCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        await WaitReadyAsync(p, _watchCts.Token).ConfigureAwait(false);
    }

    private async Task WaitReadyAsync(Process p, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        SetState(ServerState.Loading);
        while (!ct.IsCancellationRequested && !p.HasExited)
        {
            try
            {
                using var resp = await _local.GetAsync(BaseUrl + "/v1/models", ct).ConfigureAwait(false);
                if (resp.IsSuccessStatusCode)
                {
                    LoadTime = sw.Elapsed;
                    Log.Info("ImageEngine", "sd-server ready", new { port = Port, loadMs = (long)sw.Elapsed.TotalMilliseconds });
                    SetState(ServerState.Ready);
                    return;
                }
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                // модель ещё грузится, порт не слушается
            }
            try { await Task.Delay(500, ct).ConfigureAwait(false); }
            catch (TaskCanceledException) { return; }
        }
    }

    private void OnLine(string? line)
    {
        if (line == null) return;
        var progress = ImageProgress.Parse(line);
        if (progress != null)
        {
            Progress = progress;
            ProgressChanged?.Invoke(this, progress);
            // Полоса прогресса перерисовывается десятки раз — в журнал попадает только последний шаг
            if (progress.Step != progress.Total) return;
        }
        lock (_tail)
        {
            _tail.AddLast(line);
            while (_tail.Count > 400) _tail.RemoveFirst();
        }
        OutputLine?.Invoke(this, line);
    }

    /// <summary>Сбросить прогресс перед новой генерацией.</summary>
    public void ResetProgress() => Progress = null;

    private void OnExited(Process p)
    {
        if (!ReferenceEquals(p, _process)) return;
        int code;
        try { code = p.ExitCode; } catch (InvalidOperationException) { code = -1; }
        DeletePidFile();
        if (State is ServerState.Stopped) return;
        var lines = Tail.Split('\n');
        var last = string.Join("\n", lines.Skip(Math.Max(0, lines.Length - 15)));
        Fail($"sd-server exited with code {code} (0x{code:X8})" + (last.Length > 0 ? Environment.NewLine + last : string.Empty));
    }

    private void Fail(string message)
    {
        LastError = message;
        Log.Error("ImageEngine", message);
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
                Log.Warn("ImageEngine", "Error while stopping sd-server", ex);
            }
            p.Dispose();
            DeletePidFile();
            Log.Info("ImageEngine", "sd-server stopped");
        }
        if (_job != IntPtr.Zero)
        {
            Native.CloseHandle(_job);
            _job = IntPtr.Zero;
        }
        if (State != ServerState.Stopped) SetState(ServerState.Stopped);
    }

    public bool IsRunning => State is ServerState.Starting or ServerState.Loading or ServerState.Ready;

    private void SetState(ServerState s)
    {
        State = s;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void DeletePidFile()
    {
        try { File.Delete(_pidFile); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

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
                Native.SetInformationJobObject(_job, 9, ptr, (uint)size);
            }
            finally
            {
                Marshal.FreeHGlobal(ptr);
            }
            // Win7: вложенные Job не поддерживаются — остаётся PID-файл и очистка «сирот»
            if (!Native.AssignProcessToJobObject(_job, p.Handle))
                Log.Warn("ImageEngine", "AssignProcessToJobObject failed: " + Marshal.GetLastWin32Error());
        }
        catch (Exception ex) when (ex is EntryPointNotFoundException or DllNotFoundException)
        {
            Log.Warn("ImageEngine", "Job objects unavailable", ex);
        }
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
