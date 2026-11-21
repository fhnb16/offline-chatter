using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OfflineChatter.Core.Engine;
using OfflineChatter.Core.Mcp;
using OfflineChatter.Core.Tools;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

public sealed record ConsentOption(ConsentMode Mode, string Title);

/// <summary>Инструмент в списке: режим согласия меняется прямо в строке.</summary>
public sealed partial class ToolItem : ObservableObject
{
    private readonly McpViewModel _owner;

    public ToolItem(ToolDefinition tool, ConsentMode mode, McpViewModel owner)
    {
        Tool = tool;
        _owner = owner;
        _mode = owner.ConsentOptions.First(o => o.Mode == mode);
    }

    public ToolDefinition Tool { get; }
    public string Name => Tool.Name;
    public string Description => Tool.Description;

    [ObservableProperty] private ConsentOption _mode;

    partial void OnModeChanged(ConsentOption value) => _owner.SetConsent(Tool, value.Mode);
}

public sealed partial class McpServerItem : ObservableObject
{
    public McpServerItem(McpServerConfig config) => Config = config;

    public McpServerConfig Config { get; }
    public string Name => Config.Name;
    public string Target => Config.Transport == "stdio"
        ? ServerLaunchOptions.JoinCommandLine(new[] { Config.Command ?? string.Empty }.Concat(Config.Args ?? new List<string>()))
        : Config.Url ?? string.Empty;
    public string Transport => Config.Transport;

    [ObservableProperty] private string? _status;
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    [ObservableProperty] private bool _isConnected;
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [NotifyPropertyChangedFor(nameof(CanDisconnect))]
    [ObservableProperty] private bool _isConnecting;
    [NotifyPropertyChangedFor(nameof(CanConnect))]
    [ObservableProperty] private bool _enabled;

    public bool CanConnect => Enabled && !CanDisconnect;
    public bool CanDisconnect => IsConnected || IsConnecting;

    public ObservableCollection<ToolItem> Tools { get; } = new();
}

/// <summary>Раздел «Инструменты (MCP)»: встроенные инструменты, серверы MCP, согласие на вызовы.</summary>
public sealed partial class McpViewModel : PageViewModel
{
    private readonly AppServices _s;
    private bool _loading;

    public McpViewModel(AppServices services)
    {
        _s = services;
        ConsentOptions = new[]
        {
            new ConsentOption(ConsentMode.Ask, L.Get("Enum_ConsentMode_Ask")),
            new ConsentOption(ConsentMode.Always, L.Get("Enum_ConsentMode_Always")),
            new ConsentOption(ConsentMode.Disabled, L.Get("Enum_ConsentMode_Disabled")),
        };
        var t = services.Settings.Current.Tools;
        _fileSandbox = t.FileSandbox ?? string.Empty;
        _maxSteps = t.MaxSteps;
        Web = new WebToolsViewModel(services);
        _enabledByDefault = t.EnabledByDefault;
        _s.Tools.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(Refresh));
    }

    public override PageKind Kind => PageKind.Mcp;

    public IReadOnlyList<ConsentOption> ConsentOptions { get; }
    public ObservableCollection<ToolItem> Builtins { get; } = new();
    public ObservableCollection<McpServerItem> Servers { get; } = new();

    /// <summary>Настройки web_fetch, web_search и город для погоды.</summary>
    public WebToolsViewModel Web { get; }

    [ObservableProperty] private string _fileSandbox;
    [ObservableProperty] private int _maxSteps;
    [ObservableProperty] private bool _enabledByDefault;
    [ObservableProperty] private string? _status;

    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newCommand = string.Empty;
    [ObservableProperty] private string _newWorkingDirectory = string.Empty;
    [ObservableProperty] private string _newUrl = string.Empty;

    /// <summary>Убирать из схем служебные поля перед отправкой модели.</summary>
    public bool CompactSchemas
    {
        get => _s.Settings.Current.Tools.CompactSchemas;
        set
        {
            _s.Settings.Current.Tools.CompactSchemas = value;
            _s.Settings.Save();
            OnPropertyChanged();
            _s.Tools.NotifyChanged(); // цена инструментов в правой панели чата пересчитывается
        }
    }

    /// <summary>Отправлять результаты инструментов из прошлых вопросов сокращёнными.</summary>
    public bool CompactOldResults
    {
        get => _s.Settings.Current.Tools.CompactOldResults;
        set
        {
            _s.Settings.Current.Tools.CompactOldResults = value;
            _s.Settings.Save();
            OnPropertyChanged();
        }
    }

    public string ConfigPath => _s.Mcp.FilePath;
    public bool HasServers => Servers.Count > 0;

    public override void OnNavigatedTo() => Refresh();

    private void Refresh()
    {
        _loading = true;
        var all = _s.Tools.All();
        Builtins.Clear();
        foreach (var t in all.Where(t => t.Source == "builtin")) Builtins.Add(new ToolItem(t, _s.Tools.ConsentFor(t), this));

        Servers.Clear();
        foreach (var cfg in _s.Mcp.Servers)
        {
            var item = new McpServerItem(cfg) { Enabled = !cfg.Disabled };
            _s.Tools.Clients.TryGetValue(cfg.Name, out var client);
            if (client?.IsConnected == true)
            {
                item.IsConnected = true;
                item.Status = L.F("Mcp_Connected", client.ServerName ?? cfg.Name, client.Tools.Count, client.Resources.Count, client.Prompts.Count);
                foreach (var t in all.Where(t => t.Source == cfg.Name)) item.Tools.Add(new ToolItem(t, _s.Tools.ConsentFor(t), this));
            }
            else
            {
                item.IsConnecting = client != null && client.LastError == null;
                var error = _s.McpErrors.TryGetValue(cfg.Name, out var err) ? err : client?.LastError;
                item.Status = cfg.Disabled ? L.Get("Mcp_Disabled")
                    : item.IsConnecting ? L.Get("Mcp_Connecting")
                    : error != null ? L.F("Mcp_Error", error) : L.Get("Mcp_NotConnected");
            }
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(McpServerItem.Enabled) && !_loading) _ = ToggleServerAsync(item);
            };
            Servers.Add(item);
        }
        OnPropertyChanged(nameof(HasServers));
        _loading = false;
    }

    public void SetConsent(ToolDefinition tool, ConsentMode mode)
    {
        if (_loading) return;
        _s.Settings.Current.Tools.Consent[tool.Id] = mode;
        _s.Settings.Save();
        // Выключенный инструмент пропадает из набора чата и из его цены
        _s.Tools.NotifyChanged();
    }

    partial void OnFileSandboxChanged(string value) => SaveSettings();
    partial void OnMaxStepsChanged(int value) => SaveSettings();
    partial void OnEnabledByDefaultChanged(bool value) => SaveSettings();

    private void SaveSettings()
    {
        var t = _s.Settings.Current.Tools;
        t.FileSandbox = string.IsNullOrWhiteSpace(FileSandbox) ? null : FileSandbox.Trim();
        t.MaxSteps = Math.Max(1, Math.Min(20, MaxSteps));
        t.EnabledByDefault = EnabledByDefault;
        _s.Settings.Save();
        if (!_loading) Refresh();
    }

    [RelayCommand]
    private void PickSandbox()
    {
        var dir = Dialogs.PickFolder(L.Get("Tools_Sandbox"), FileSandbox);
        if (dir != null) FileSandbox = dir;
    }

    private async Task ToggleServerAsync(McpServerItem item)
    {
        item.Config.Disabled = !item.Enabled;
        _s.Mcp.Save();
        if (item.Config.Disabled) await DisconnectAsync(item);
        else await ConnectAsync(item);
    }

    [RelayCommand]
    private async Task ConnectAsync(McpServerItem item)
    {
        item.Status = L.Get("Mcp_Connecting");
        using var cts = new CancellationTokenSource(item.Config.RequestTimeout);
        try
        {
            await _s.Tools.ConnectAsync(item.Config, cts.Token);
            _s.McpErrors.TryRemove(item.Name, out _);
        }
        catch (OperationCanceledException) when (!cts.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _s.McpErrors[item.Name] = ex.GetBaseException().Message;
            Core.Diagnostics.Log.Warn("MCP", "Connect failed " + item.Name, ex);
        }
        Refresh();
    }

    [RelayCommand]
    private async Task DisconnectAsync(McpServerItem item)
    {
        await Task.Run(() => _s.Tools.Disconnect(item.Name));
        _s.McpErrors.TryRemove(item.Name, out _);
        Refresh();
    }

    [RelayCommand]
    private async Task RemoveAsync(McpServerItem item)
    {
        if (MessageBox.Show(L.F("Mcp_ConfirmRemove", item.Name), "Offline Chatter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _s.Mcp.Servers.Remove(item.Config);
        _s.Mcp.Save();
        await DisconnectAsync(item);
    }

    /// <summary>Добавить сервер: команда (stdio) или URL (HTTP; адрес на /sse — старый протокол SSE).</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        var parts = ServerLaunchOptions.SplitCommandLine(NewCommand.Trim());
        var url = NewUrl.Trim();
        if (name.Length == 0 || (parts.Count > 0) == (url.Length > 0) || (parts.Count > 0 && string.IsNullOrWhiteSpace(parts[0])))
        {
            Status = L.Get("Mcp_AddHint");
            return;
        }
        if (_s.Mcp.Servers.Any(s => s.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
        {
            Status = L.F("Mcp_Exists", name);
            return;
        }
        var cfg = new McpServerConfig { Name = name };
        if (url.Length > 0)
        {
            cfg.Url = url;
            cfg.Type = url.TrimEnd('/').EndsWith("/sse", StringComparison.OrdinalIgnoreCase) ? "sse" : "http";
        }
        else
        {
            cfg.Type = "stdio";
            cfg.Command = parts[0];
            cfg.Args = parts.Skip(1).ToList();
            cfg.Cwd = string.IsNullOrWhiteSpace(NewWorkingDirectory) ? null : NewWorkingDirectory.Trim();
        }
        _s.Mcp.Servers.Add(cfg);
        _s.Mcp.Save();
        NewName = NewCommand = NewWorkingDirectory = NewUrl = string.Empty;
        Status = null;
        Refresh();
        var item = Servers.FirstOrDefault(s => s.Name == name);
        if (item != null) await ConnectAsync(item);
    }

    [RelayCommand]
    private void PickWorkingDirectory()
    {
        var path = Dialogs.PickFolder(L.Get("Mcp_WorkingDirectory"), NewWorkingDirectory);
        if (path != null) NewWorkingDirectory = path;
    }

    [RelayCommand]
    private void ImportClaude()
    {
        var path = McpConfigStore.ClaudeDesktopConfigPath;
        if (!File.Exists(path))
        {
            var dlg = new Microsoft.Win32.OpenFileDialog { Filter = "JSON|*.json", Title = L.Get("Mcp_ImportTitle") };
            if (dlg.ShowDialog() != true) return;
            path = dlg.FileName;
        }
        try
        {
            var added = _s.Mcp.ImportFrom(path);
            Status = L.F("Mcp_Imported", added);
            _s.ConnectMcpServers();
        }
        catch (Exception ex) when (ex is IOException or Newtonsoft.Json.JsonException or UnauthorizedAccessException)
        {
            Status = ex.Message;
        }
        Refresh();
    }

    [RelayCommand]
    private void OpenConfig()
    {
        if (!File.Exists(ConfigPath)) _s.Mcp.Save();
        EngineViewModel.OpenUrl(ConfigPath);
    }

    [RelayCommand]
    private async Task ReloadAsync()
    {
        var names = _s.Tools.Clients.Keys.ToList();
        await Task.WhenAll(names.Select(name => Task.Run(() => _s.Tools.Disconnect(name))));
        _s.Mcp.Load();
        _s.ConnectMcpServers();
        Status = L.Get("Mcp_Connecting");
        Refresh();
    }
}
