using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Storage;

namespace OfflineChatter.Core.Engine;

/// <summary>Фасад движка для UI: реестр сборок, процесс сервера, кэш флагов и сведений о железе.</summary>
public sealed class EngineManager : IDisposable
{
    private readonly AppPaths _paths;
    private readonly EngineSettings _settings;
    private readonly Action _save;
    private readonly ConcurrentDictionary<string, FlagCatalog> _flags = new();
    private Task<HardwareInfo>? _hardware;

    public EngineManager(AppPaths paths, EngineSettings settings, Action saveSettings)
    {
        _paths = paths;
        _settings = settings;
        _save = saveSettings;
        Registry = new EngineRegistry(paths, settings);
        Server = new EngineProcess(paths.Engine);
        EngineProcess.KillOrphan(paths.Engine);
    }

    public EngineRegistry Registry { get; }
    public EngineProcess Server { get; }
    public EngineSettings Settings => _settings;

    public event EventHandler? EnginesChanged;

    public Task<HardwareInfo> GetHardwareAsync(bool refresh = false)
    {
        if (refresh || _hardware == null)
            _hardware = Task.Run(() =>
            {
                var hw = HardwareProbe.Probe();
                Log.Info("Hardware", "Probed", new
                {
                    hw.CpuName, hw.LogicalCores, hw.PhysicalCores, hw.Avx2, hw.Avx512,
                    ramGb = hw.TotalRam >> 30, gpus = hw.Gpus, hw.NvidiaDriver, cuda = hw.CudaVersionText, hw.VulkanAvailable, hw.IsArm64,
                });
                return hw;
            });
        return _hardware;
    }

    public void NotifyChanged()
    {
        _save();
        EnginesChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetActive(EngineInstall e)
    {
        Registry.SetActive(e);
        NotifyChanged();
    }

    public EngineInstall? AddExternal(string path)
    {
        var e = Registry.AddExternal(path);
        if (e != null) NotifyChanged();
        return e;
    }

    public void Remove(EngineInstall e)
    {
        if (Server.Engine?.Id == e.Id) Server.Stop();
        Registry.Remove(e);
        _flags.TryRemove(e.Id, out _);
        NotifyChanged();
    }

    /// <summary>Каталог флагов: из help.txt рядом с движком или запуском --help (результат кэшируется в файл).</summary>
    public async Task<FlagCatalog?> GetFlagsAsync(EngineInstall e)
    {
        if (_flags.TryGetValue(e.Id, out var cached)) return cached;
        var helpFile = Path.Combine(e.IsExternal ? Path.Combine(_paths.Cache, "engine-help") : e.Directory, HelpFileName(e));
        string? help = null;
        try
        {
            if (File.Exists(helpFile) && File.GetLastWriteTimeUtc(helpFile) >= File.GetLastWriteTimeUtc(e.ServerExe))
                help = File.ReadAllText(helpFile);
        }
        catch (IOException) { }

        if (help == null)
        {
            help = await RunHelpAsync(e.ServerExe).ConfigureAwait(false);
            if (help == null) return null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(helpFile)!);
                File.WriteAllText(helpFile, help, Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }

        var catalog = FlagCatalog.Parse(help);
        _flags[e.Id] = catalog;
        return catalog;
    }

    private static string HelpFileName(EngineInstall e) =>
        e.IsExternal ? "help-" + Math.Abs(e.Id.GetHashCode()).ToString("x", System.Globalization.CultureInfo.InvariantCulture) + ".txt" : "help.txt";

    private static async Task<string?> RunHelpAsync(string exe)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, "--help")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                StandardOutputEncoding = Encoding.UTF8,
            };
            using var p = Process.Start(psi)!;
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            var done = await Task.Run(() => p.WaitForExit(20000)).ConfigureAwait(false);
            if (!done)
            {
                try { p.Kill(); } catch (InvalidOperationException) { }
                return null;
            }
            var text = await stdout.ConfigureAwait(false);
            if (text.Length < 200) text += await stderr.ConfigureAwait(false);
            return text.Length > 200 ? text : null;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            Log.Warn("Engine", "Cannot run --help for " + exe, ex);
            return null;
        }
    }

    /// <summary>Запуск сервера на активном движке.</summary>
    public async Task StartAsync(ServerLaunchOptions options, CancellationToken ct)
    {
        var engine = Registry.Active ?? throw new InvalidOperationException("No engine installed");
        var flags = await GetFlagsAsync(engine).ConfigureAwait(false);
        options.SlotSavePath ??= Path.Combine(_paths.Cache, "slots");
        Directory.CreateDirectory(options.SlotSavePath);
        // Публичный API: постоянный порт и ключ, иначе — случайные (сервер нужен только самому приложению)
        var expose = _settings.ExposeApi;
        var key = expose ? Storage.Secrets.UnprotectOrEmpty(_settings.ApiKeyProtected) : null;
        await Server.StartAsync(engine, options, flags, expose && _settings.AllowLan, expose ? _settings.Port : 0, ct, key).ConfigureAwait(false);
    }

    public void Dispose() => Server.Dispose();
}
