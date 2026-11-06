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
        if (!s.Network.CheckUpdates || s.Network.OfflineMode || string.IsNullOrWhiteSpace(s.UpdateRepo)) return;
        try
        {
            using var cts = new System.Threading.CancellationTokenSource(TimeSpan.FromSeconds(30));
            var info = await UpdateChecker.CheckAsync(Http, s.UpdateRepo, CurrentVersion, cts.Token).ConfigureAwait(false);
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

    public static Version CurrentVersion => Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);

    /// <summary>Подпись настроек публичного API: при их смене сервер перезапускается.</summary>
    private string ApiSignature
    {
        get
        {
            var e = Settings.Current.Engine;
            return e.ExposeApi ? $"api:{e.Port}:{e.AllowLan}:{e.ApiKeyProtected.GetHashCode()}" : "api:off";
        }
    }

    /// <summary>Публичный API включён — поднять сервер с активной моделью (при запуске программы и по кнопке).</summary>
    public async System.Threading.Tasks.Task<string?> StartPublicApiAsync(System.Threading.CancellationToken ct)
    {
        var model = ActiveModel;
        if (model == null) return Localization.L.Get("Chat_NoModel");
        if (Engine.Registry.Active == null) return Localization.L.Get("Chat_NoEngine");
        return await EnsureServerAsync(model, ct).ConfigureAwait(false) ? null : Engine.Server.LastError ?? "?";
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
            return (null, null, Localization.L.F("Chat_ServerFailed", Engine.Server.LastError ?? "?"));
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

    public void Dispose()
    {
        Tools.Dispose();
        Engine.Dispose();
        Http.Dispose();
        EngineLog.Dispose();
    }
}
