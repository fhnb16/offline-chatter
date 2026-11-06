using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Net;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

public sealed partial class ProviderModelItem : ObservableObject
{
    public ProviderModelItem(string id, bool pinned)
    {
        Id = id;
        _pinned = pinned;
    }

    public string Id { get; }
    [ObservableProperty] private bool _pinned;
}

public sealed partial class ProviderItemViewModel : ObservableObject
{
    private readonly ProvidersViewModel _owner;

    public ProviderItemViewModel(ProviderConfig config, ProvidersViewModel owner)
    {
        Config = config;
        _owner = owner;
        _name = config.Name;
        _baseUrl = config.BaseUrl;
        _enabled = config.Enabled;
        var q = config.EffectiveQuirks;
        _maxTokensField = q.MaxTokensField;
        _supportsVision = q.SupportsVision;
        _supportsTools = q.SupportsTools;
        _isLlamaCpp = q.IsLlamaCpp;
        LoadModels();
    }

    public ProviderConfig Config { get; }
    public ProviderPreset Preset => ProviderPresets.Get(Config.PresetId);
    public bool HasKeyUrl => Preset.KeyUrl != null;
    public bool HasKey => !string.IsNullOrEmpty(Config.ApiKeyProtected);
    public string KeyState => HasKey ? L.Get("Providers_KeySaved") : (Preset.NeedsKey ? L.Get("Providers_KeyMissing") : L.Get("Providers_KeyOptional"));
    public IReadOnlyList<string> MaxTokensFields { get; } = new[] { "max_tokens", "max_completion_tokens" };

    public ObservableCollection<ProviderModelItem> Models { get; } = new();

    [ObservableProperty] private string _name;
    [ObservableProperty] private string _baseUrl;
    [ObservableProperty] private bool _enabled;
    [ObservableProperty] private string _maxTokensField;
    [ObservableProperty] private bool _supportsVision;
    [ObservableProperty] private bool _supportsTools;
    [ObservableProperty] private bool _isLlamaCpp;
    [ObservableProperty] private string? _status;
    [ObservableProperty] private bool _statusOk;
    [ObservableProperty] private bool _isTesting;

    private void LoadModels()
    {
        Models.Clear();
        foreach (var m in Config.Models) Models.Add(new ProviderModelItem(m, Config.PinnedModels.Contains(m)));
        foreach (var m in Models) m.PropertyChanged += (_, _) => SavePins();
    }

    private void SavePins()
    {
        Config.PinnedModels = Models.Where(m => m.Pinned).Select(m => m.Id).ToList();
        _owner.Save();
    }

    partial void OnNameChanged(string value) { Config.Name = value.Trim(); _owner.Save(); }
    partial void OnBaseUrlChanged(string value) { Config.BaseUrl = value.Trim().TrimEnd('/'); _owner.Save(); }
    partial void OnEnabledChanged(bool value) { Config.Enabled = value; _owner.Save(); }
    partial void OnMaxTokensFieldChanged(string value) => SaveQuirks();
    partial void OnSupportsVisionChanged(bool value) => SaveQuirks();
    partial void OnSupportsToolsChanged(bool value) => SaveQuirks();
    partial void OnIsLlamaCppChanged(bool value) => SaveQuirks();

    private void SaveQuirks()
    {
        var q = Newtonsoft.Json.JsonConvert.DeserializeObject<ProviderQuirks>(Newtonsoft.Json.JsonConvert.SerializeObject(Preset.Quirks))!;
        q.MaxTokensField = MaxTokensField;
        q.SupportsVision = SupportsVision;
        q.SupportsTools = SupportsTools;
        q.IsLlamaCpp = IsLlamaCpp;
        Config.Quirks = q;
        _owner.Save();
    }

    public void SetKey(string key)
    {
        Config.ApiKeyProtected = Secrets.Protect(key.Trim());
        _owner.Save();
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(KeyState));
    }

    [RelayCommand]
    private async Task TestAsync()
    {
        IsTesting = true;
        Status = L.Get("Providers_Testing");
        try
        {
            var client = ChatClient.ForHttpService(_owner.Services.Http, Config.BaseUrl, Secrets.UnprotectOrEmpty(Config.ApiKeyProtected), Config.EffectiveQuirks, Config.Name);
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var models = await client.ListModelsAsync(cts.Token);
            Config.Models = models;
            // Если ничего не выбрано — закрепим первые несколько, чтобы модели сразу появились в чате
            if (Config.PinnedModels.Count == 0) Config.PinnedModels = models.Take(5).ToList();
            _owner.Save();
            LoadModels();
            Status = L.F("Providers_TestOk", models.Count);
            StatusOk = true;
        }
        catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.IO.IOException or Newtonsoft.Json.JsonException)
        {
            Status = ex is HttpStatusException hs ? $"HTTP {(int)hs.Status}: {hs.Body}" : ex.GetBaseException().Message;
            StatusOk = false;
        }
        finally
        {
            IsTesting = false;
        }
    }

    [RelayCommand]
    private void OpenKeyPage()
    {
        if (Preset.KeyUrl != null) EngineViewModel.OpenUrl(Preset.KeyUrl);
    }

    [RelayCommand]
    private void Remove() => _owner.Remove(this);
}

/// <summary>Раздел «API-провайдеры»: облачные и локальные OpenAI-совместимые серверы.</summary>
public sealed partial class ProvidersViewModel : PageViewModel
{
    public ProvidersViewModel(AppServices services)
    {
        Services = services;
        foreach (var p in services.Providers.Items) Items.Add(new ProviderItemViewModel(p, this));
        _selectedPreset = ProviderPresets.All[0];
    }

    public AppServices Services { get; }

    public override PageKind Kind => PageKind.Providers;

    public IReadOnlyList<ProviderPreset> Presets => ProviderPresets.All;

    public ObservableCollection<ProviderItemViewModel> Items { get; } = new();

    public bool HasItems => Items.Count > 0;

    [ObservableProperty] private ProviderPreset _selectedPreset;

    [RelayCommand]
    private void Add()
    {
        var preset = SelectedPreset;
        var config = new ProviderConfig { PresetId = preset.Id, Name = preset.Name, BaseUrl = preset.BaseUrl };
        Services.Providers.Items.Add(config);
        Save();
        Items.Insert(0, new ProviderItemViewModel(config, this));
        OnPropertyChanged(nameof(HasItems));
    }

    public void Remove(ProviderItemViewModel item)
    {
        Services.Providers.Items.Remove(item.Config);
        Items.Remove(item);
        Save();
        OnPropertyChanged(nameof(HasItems));
    }

    public void Save() => Services.Providers.Save();
}
