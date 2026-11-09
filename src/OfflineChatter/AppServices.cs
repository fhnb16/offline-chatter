using System;
using System.Linq;
using System.Reflection;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Models;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;

namespace OfflineChatter;

/// <summary>
/// Общие сервисы приложения (простая композиция без DI-контейнера — ради веса и предсказуемости).
/// </summary>
public sealed class AppServices : IDisposable
{
    public AppServices(AppPaths paths, SettingsStore settings)
    {
        Paths = paths;
        Settings = settings;
        Version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "0.0.0";

        Roots = new TrustedRoots(paths.Certs);
        Http = new HttpService(settings.Current.Network, Roots, Version);
        Downloads = new DownloadManager(Http, settings.Current.Network);
        Releases = new GitHubReleases(Http);
        Engine = new EngineManager(paths, settings.Current.Engine, settings.Save);
        EngineInstaller = new EngineInstaller(paths, Downloads, Releases);
        Hf = new HfClient(Http, () => Secrets.UnprotectOrEmpty(settings.Current.Models.HfTokenProtected));
        Models = new ModelRegistry(paths.ModelsFile);
        Profiles = new ProfileStore(paths.Profiles);
        Chats = new ChatStore(paths.Chats);
        Providers = new ProviderStore(paths.ProvidersFile);
        Tools = new Core.Tools.ToolRegistry(settings.Current.Tools, Http);
        Mcp = new Core.Mcp.McpConfigStore(paths.McpFile);
        if (Mcp.SeedBuiltin(settings.Current.Tools)) settings.Save();
        Prompts = new PromptPresetStore(paths.PromptsFile);
        Prompts.SeedIfEmpty(Localization.L.IsRussian);
        LocalHttp = new System.Net.Http.HttpClient(new System.Net.Http.HttpClientHandler { UseProxy = false }) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

        // Вывод llama-server — в отдельный журнал engine.jsonl
        var log = settings.Current.Logging;
        EngineLog = new JsonlLogger(paths.Logs, "engine", log.MaxFileSizeMb * 1024L * 1024L, log.MaxFiles, LogLevel.Info);
        Engine.Server.OutputLine += (_, line) => EngineLog.Write(LogLevel.Info, "llama-server", line);
        Engine.Server.StateChanged += (_, _) => UpdateLoopbackForwarder();
    }

    public string Version { get; }
    public AppPaths Paths { get; }
    public SettingsStore Settings { get; }
    public TrustedRoots Roots { get; }
    public HttpService Http { get; }
    public DownloadManager Downloads { get; }
    public GitHubReleases Releases { get; }
    public EngineManager Engine { get; }
    public EngineInstaller EngineInstaller { get; }
    public HfClient Hf { get; }
    public ModelRegistry Models { get; }
    public ProfileStore Profiles { get; }
    public ChatStore Chats { get; }
    public ProviderStore Providers { get; }
    public Core.Tools.ToolRegistry Tools { get; }
    public Core.Mcp.McpConfigStore Mcp { get; }
    public PromptPresetStore Prompts { get; }

    /// <summary>Есть доступная к установке новая версия (проверка при запуске).</summary>
    public UpdateInfo? PendingUpdate { get; set; }

    public event EventHandler? UpdateFound;

    /// <summary>Проверка обновлений при запуске: тихо, без сообщений об ошибках.</summary>
    public async System.Threading.Tasks.Task CheckUpdatesInBackgroundAsync()
    {
        var s = Settings.Current;
        if (!s.Network.CheckUpdates || s.Network.OfflineMode) return;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var info = await UpdateChecker.CheckAsync(Http, CurrentVersion, cts.Token).ConfigureAwait(false);
            if (!info.Available) return;
            PendingUpdate = info;
            Log.Info("Update", "Update available", new { info.LatestTag });
            UpdateFound?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.IO.IOException or Newtonsoft.Json.JsonException or InvalidCastException)
        {
            Log.Info("Update", "Update check failed: " + ex.GetBaseException().Message);
        }
    }

    /// <summary>Новая сборка llama.cpp для активного движка (проверка при запуске).</summary>
    public EngineUpdate? PendingEngineUpdate { get; private set; }

    public event EventHandler? EngineUpdateChanged;

    public void SetEngineUpdate(EngineUpdate? update)
    {
        PendingEngineUpdate = update;
        EngineUpdateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Есть ли новая сборка llama.cpp того же бэкенда, что у активного движка.
    /// Сборки Win7-compat проверяются по исходному репозиторию, свои папки не проверяются.
    /// </summary>
    public async System.Threading.Tasks.Task<EngineUpdate?> CheckEngineUpdateAsync(bool respectIgnored, System.Threading.CancellationToken ct)
    {
        var s = Settings.Current;
        var active = Engine.Registry.Active;
        if (s.Network.OfflineMode || active == null || active.IsExternal) return null;
        // Обновления — из того же источника, откуда скачана сборка
        var repo = active.Manifest.SourceRepo ?? (EngineUpdates.IsCompatBuild(active) ? UpdateChecker.Repo : null);
        var releases = await Releases.ListAsync(10, ct, repo, onlyLlamaTags: repo == UpdateChecker.Repo).ConfigureAwait(false);
        var update = EngineUpdates.Find(active, releases, respectIgnored ? s.Engine.IgnoredUpdateTag : null);
        SetEngineUpdate(update);
        if (update != null) Log.Info("Engine", "Engine update available", new { current = active.Manifest.Tag, update.Tag, variant = update.Variant.Key });
        return update;
    }

    public async System.Threading.Tasks.Task CheckEngineUpdateInBackgroundAsync()
    {
        if (!Settings.Current.Engine.CheckUpdates) return;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            await CheckEngineUpdateAsync(respectIgnored: true, cts.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.IO.IOException or Newtonsoft.Json.JsonException or InvalidCastException)
        {
            Log.Info("Engine", "Engine update check failed: " + ex.GetBaseException().Message);
        }
    }

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Подпись настроек публичного API: при их смене сервер перезапускается.</summary>
    private string ApiSignature
    {
        get
        {
            var e = Settings.Current.Engine;
            return e.ExposeApi ? $"api:{e.Port}:{e.ListenAddress}:{e.ApiKeyProtected.GetHashCode()}" : "api:off";
        }
    }

    /// <summary>Публичный API включён — поднять сервер с активной моделью (при запуске программы и по кнопке).</summary>
    /// <summary>
    /// Публичному API нужен постоянный ключ: без него сервер получал бы случайный ключ при каждом запуске,
    /// и сторонний клиент не смог бы подключиться. Если ключа нет (включили правкой файла, очистили поле) — создаём.
    /// </summary>
    public void EnsureApiKey()
    {
        var e = Settings.Current.Engine;
        if (!e.ExposeApi || Secrets.UnprotectOrEmpty(e.ApiKeyProtected).Length > 0) return;
        e.ApiKeyProtected = Secrets.Protect(EngineProcess.NewKey());
        Settings.Save();
        Log.Info("Engine", "Public API key generated");
    }

    public async System.Threading.Tasks.Task<string?> StartPublicApiAsync(System.Threading.CancellationToken ct)
    {
        EnsureApiKey();
        var model = ActiveModel;
        if (model == null) return Localization.L.Get("Chat_NoModel");
        if (Engine.Registry.Active == null) return Localization.L.Get("Chat_NoEngine");
        return await EnsureServerAsync(model, ct).ConfigureAwait(false) ? null : ServerErrorText ?? "?";
    }

    private System.Threading.Tasks.Task<OfflineChatter.Core.Diagnostics.SysmemFallbackInfo?>? _sysmemFallback;

    /// <summary>
    /// Политика «CUDA - Sysmem Fallback Policy» драйвера NVIDIA (профиль llama-server.exe или глобальный).
    /// null — видеокарты NVIDIA нет. Читается один раз; <paramref name="refresh"/> — перечитать (после смены в панели NVIDIA).
    /// </summary>
    public System.Threading.Tasks.Task<OfflineChatter.Core.Diagnostics.SysmemFallbackInfo?> GetSysmemFallbackAsync(bool refresh = false)
    {
        if (refresh || _sysmemFallback == null)
            _sysmemFallback = System.Threading.Tasks.Task.Run(async () =>
            {
                var hw = await Engine.GetHardwareAsync().ConfigureAwait(false);
                if (!hw.Gpus.Any(g => g.Vendor == GpuVendor.Nvidia)) return (OfflineChatter.Core.Diagnostics.SysmemFallbackInfo?)null;
                var info = OfflineChatter.Core.Diagnostics.NvidiaDriverSettings.ReadSysmemFallback("llama-server.exe", hw.NvidiaDriver);
                Log.Info("Engine", "NVIDIA sysmem fallback policy", new { policy = info.Policy.ToString(), forApp = info.ForApp, raw = info.RawValue, info.Detail, driver = hw.NvidiaDriver });
                return info;
            });
        return _sysmemFallback;
    }

    /// <summary>Ошибка последнего запуска llama-server; известные причины — своим текстом на языке интерфейса.</summary>
    public string? ServerErrorText
    {
        get
        {
            var srv = Engine.Server;
            if (srv.LastErrorCode != "StrictGpu") return srv.LastError;
            var f = srv.Facts;
            return Localization.L.F("Ins_StrictFailed",
                f.OffloadedLayers?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?",
                f.TotalLayers?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "?");
        }
    }

    /// <summary>Последние ошибки подключения MCP по имени сервера (для UI).</summary>
    public System.Collections.Concurrent.ConcurrentDictionary<string, string> McpErrors { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Подключить включённые серверы MCP в фоне (при старте и по кнопке «Перечитать»).</summary>
    public void ConnectMcpServers()
    {
        foreach (var cfg in Mcp.Servers.Where(s => !s.Disabled))
        {
            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                try
                {
                    using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(60));
                    await Tools.ConnectAsync(cfg, cts.Token).ConfigureAwait(false);
                    McpErrors.TryRemove(cfg.Name, out _);
                }
                catch (Exception ex)
                {
                    McpErrors[cfg.Name] = ex.GetBaseException().Message;
                    Core.Diagnostics.Log.Warn("MCP", "Cannot connect " + cfg.Name, ex);
                }
            });
        }
    }

    /// <summary>HTTP к локальному llama-server: без прокси и без общего таймаута (генерация бывает долгой).</summary>
    public System.Net.Http.HttpClient LocalHttp { get; }

    /// <summary>Клиент чата для ссылки на модель: "local:{id}" или "provider:{pid}:{model}".</summary>
    public async System.Threading.Tasks.Task<(ChatClient? client, string? model, string? error)> ResolveChatClientAsync(string? modelRef, Action<string>? status, System.Threading.CancellationToken ct)
    {
        if (modelRef != null && modelRef.StartsWith("provider:", StringComparison.Ordinal))
        {
            var parts = modelRef.Split(new[] { ':' }, 3);
            var p = Providers.Find(parts.Length > 1 ? parts[1] : null);
            if (p == null) return (null, null, Localization.L.Get("Chat_ProviderMissing"));
            var key = Secrets.UnprotectOrEmpty(p.ApiKeyProtected);
            return (ChatClient.ForHttpService(Http, p.BaseUrl, key, p.EffectiveQuirks, p.Name), parts.Length > 2 ? parts[2] : null, null);
        }

        var id = modelRef != null && modelRef.StartsWith("local:", StringComparison.Ordinal) ? modelRef.Substring(6) : null;
        var model = Models.Find(id) ?? ActiveModel;
        if (model == null) return (null, null, Localization.L.Get("Chat_NoModel"));
        if (!model.Exists) return (null, null, Localization.L.F("Chat_ModelMissing", model.Path));
        if (Engine.Registry.Active == null) return (null, null, Localization.L.Get("Chat_NoEngine"));

        if (!(Engine.Server.State == ServerState.Ready && Engine.Server.Options?.ModelPath == model.Path))
            status?.Invoke(Localization.L.F("Chat_StartingModel", model.DisplayName));
        if (!await EnsureServerAsync(model, ct).ConfigureAwait(false))
            return (null, null, Localization.L.F("Chat_ServerFailed", ServerErrorText ?? "?"));
        return (ChatClient.ForLocal(LocalHttp, Engine.Server.BaseUrl + "/v1", Engine.Server.ApiKey, "llama.cpp"), Engine.Server.Options?.Alias, null);
    }
    private string? _runningSignature;
    private readonly System.Threading.SemaphoreSlim _startGate = new(1, 1);

    public LaunchProfile ProfileFor(LocalModel model) =>
        Profiles.Get(model.ProfileId ?? Settings.Current.Models.DefaultProfileId);

    /// <summary>Разрешить профиль для модели на текущем движке и железе (для превью, проверки памяти и запуска).</summary>
    public async System.Threading.Tasks.Task<ResolvedLaunch?> ResolveLaunchAsync(LocalModel model, LaunchProfile profile)
    {
        var engine = Engine.Registry.Active;
        if (engine == null || !model.Exists) return null;
        var hw = await Engine.GetHardwareAsync().ConfigureAwait(false);
        var flags = await Engine.GetFlagsAsync(engine).ConfigureAwait(false);
        var info = await System.Threading.Tasks.Task.Run(() =>
        {
            try { return GgufReader.ReadFile(model.Path); }
            catch (Exception ex) when (ex is System.IO.IOException or System.IO.InvalidDataException) { return null; }
        }).ConfigureAwait(false);
        return ProfileResolver.Resolve(profile, model, info, hw, engine, flags);
    }

    /// <summary>
    /// Убедиться, что llama-server запущен с этой моделью и профилем; иначе (пере)запустить.
    /// Возвращает false, если запуск не удался (подробности — в Engine.Server.LastError).
    /// </summary>
    public async System.Threading.Tasks.Task<bool> EnsureServerAsync(LocalModel model, System.Threading.CancellationToken ct)
    {
        await _startGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var profile = ProfileFor(model);
            var signature = model.Path + "|" + model.MmprojPath + "|" + Newtonsoft.Json.JsonConvert.SerializeObject(profile) + "|" + Engine.Registry.Active?.Id + "|" + ApiSignature;
            if (Engine.Server.State == ServerState.Ready && signature == _runningSignature) return true;

            var resolved = await ResolveLaunchAsync(model, profile).ConfigureAwait(false);
            if (resolved == null) return false;
            await Engine.StartAsync(resolved.Options, ct).ConfigureAwait(false);
            _runningSignature = Engine.Server.State == ServerState.Ready ? signature : null;
            return Engine.Server.State == ServerState.Ready;
        }
        finally
        {
            _startGate.Release();
        }
    }

    /// <summary>Папка для скачанных моделей (настраивается, может быть на другом диске).</summary>
    public string ModelsFolder =>
        string.IsNullOrWhiteSpace(Settings.Current.Models.DownloadFolder) ? Paths.Models : Settings.Current.Models.DownloadFolder!;

    public LocalModel? ActiveModel
    {
        get
        {
            var m = Models.Find(Settings.Current.Models.ActiveModelId);
            return m ?? Models.Models.FirstOrDefault(x => x.Exists);
        }
    }
    public JsonlLogger EngineLog { get; }

    /// <summary>Перешифровать все сохранённые секреты в текущем режиме (DPAPI ↔ мастер-пароль).</summary>
    public void ReprotectAllSecrets()
    {
        var s = Settings.Current;
        s.Network.ProxyPasswordProtected = Secrets.Reprotect(s.Network.ProxyPasswordProtected);
        s.Models.HfTokenProtected = Secrets.Reprotect(s.Models.HfTokenProtected);
        s.Engine.ApiKeyProtected = Secrets.Reprotect(s.Engine.ApiKeyProtected);
        foreach (var p in Providers.Items) p.ApiKeyProtected = Secrets.Reprotect(p.ApiKeyProtected);
        Providers.Save();
        Settings.Save();
    }

    private string? _templateFor;
    private string? _chatTemplate;

    /// <summary>
    /// Шаблон чата загруженной модели (/props llama-server) — по нему точнее всего видно, как модель включает
    /// размышления. Кэшируется до перезапуска сервера; null — сервер не запущен или старая версия без поля.
    /// </summary>
    public async System.Threading.Tasks.Task<string?> GetLocalChatTemplateAsync(System.Threading.CancellationToken ct)
    {
        var srv = Engine.Server;
        if (srv.State != ServerState.Ready) return null;
        var key = srv.BaseUrl + "|" + srv.StartedAt?.Ticks;
        if (_templateFor == key) return _chatTemplate;
        try
        {
            using var req = new System.Net.Http.HttpRequestMessage(System.Net.Http.HttpMethod.Get, srv.BaseUrl + "/props");
            if (!string.IsNullOrEmpty(srv.ApiKey)) req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", srv.ApiKey);
            using var resp = await LocalHttp.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = Newtonsoft.Json.Linq.JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
            _chatTemplate = (string?)json["chat_template"];
            _templateFor = key;
            return _chatTemplate;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or Newtonsoft.Json.JsonException or OperationCanceledException)
        {
            return null;
        }
    }

    private LoopbackForwarder? _forwarder;

    /// <summary>
    /// Публичный API на 127.0.0.1: дополнительно слушаем [::1] и пересылаем на сервер — «localhost» в Windows
    /// разрешается сначала в IPv6, и клиенты, которые не пробуют IPv4, иначе получают «ошибку сети».
    /// </summary>
    private void UpdateLoopbackForwarder()
    {
        var srv = Engine.Server;
        var wanted = Settings.Current.Engine.ExposeApi && srv.State == ServerState.Ready && srv.Host == EngineSettings.Loopback;
        if (wanted && _forwarder != null && _forwarderPort == srv.Port) return;
        _forwarder?.Dispose();
        _forwarder = null;
        if (!wanted) return;
        var f = new LoopbackForwarder(srv.Port);
        if (f.Start())
        {
            _forwarder = f;
            _forwarderPort = srv.Port;
        }
    }

    private int _forwarderPort;

    public void Dispose()
    {
        _forwarder?.Dispose();
        Tools.Dispose();
        Engine.Dispose();
        Http.Dispose();
        EngineLog.Dispose();
    }
}
