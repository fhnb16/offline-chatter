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
    [ObservableProperty] private bool _isConnected;
    [ObservableProperty] private bool _enabled;

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
        _searxngUrl = t.SearxngUrl ?? string.Empty;
        _maxSteps = t.MaxSteps;
        _enabledByDefault = t.EnabledByDefault;
        _s.Tools.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(Refresh));
    }

    public override PageKind Kind => PageKind.Mcp;

    public IReadOnlyList<ConsentOption> ConsentOptions { get; }
    public ObservableCollection<ToolItem> Builtins { get; } = new();
    public ObservableCollection<McpServerItem> Servers { get; } = new();

    [ObservableProperty] private string _fileSandbox;
    [ObservableProperty] private string _searxngUrl;
    [ObservableProperty] private int _maxSteps;
    [ObservableProperty] private bool _enabledByDefault;
    [ObservableProperty] private string? _status;

    [ObservableProperty] private string _newName = string.Empty;
    [ObservableProperty] private string _newCommand = string.Empty;
    [ObservableProperty] private string _newUrl = string.Empty;

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
            if (_s.Tools.Clients.TryGetValue(cfg.Name, out var client) && client.IsConnected)
            {
                item.IsConnected = true;
                item.Status = L.F("Mcp_Connected", client.ServerName ?? cfg.Name, client.Tools.Count, client.Resources.Count, client.Prompts.Count);
                foreach (var t in all.Where(t => t.Source == cfg.Name)) item.Tools.Add(new ToolItem(t, _s.Tools.ConsentFor(t), this));
            }
            else
            {
                item.Status = cfg.Disabled ? L.Get("Mcp_Disabled") : _s.McpErrors.TryGetValue(cfg.Name, out var err) ? L.F("Mcp_Error", err) : L.Get("Mcp_NotConnected");
            }
            item.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(McpServerItem.Enabled) && !_loading) ToggleServer(item);
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
    }

    partial void OnFileSandboxChanged(string value) => SaveSettings();
    partial void OnSearxngUrlChanged(string value) => SaveSettings();
    partial void OnMaxStepsChanged(int value) => SaveSettings();
    partial void OnEnabledByDefaultChanged(bool value) => SaveSettings();

    private void SaveSettings()
    {
        var t = _s.Settings.Current.Tools;
        t.FileSandbox = string.IsNullOrWhiteSpace(FileSandbox) ? null : FileSandbox.Trim();
        t.SearxngUrl = string.IsNullOrWhiteSpace(SearxngUrl) ? null : SearxngUrl.Trim();
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

    private void ToggleServer(McpServerItem item)
    {
        item.Config.Disabled = !item.Enabled;
        _s.Mcp.Save();
        if (item.Config.Disabled) _s.Tools.Disconnect(item.Name);
        else _ = ConnectAsync(item);
    }

    [RelayCommand]
    private async Task ConnectAsync(McpServerItem item)
    {
        item.Status = L.Get("Mcp_Connecting");
        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await _s.Tools.ConnectAsync(item.Config, cts.Token);
            _s.McpErrors.TryRemove(item.Name, out _);
        }
        catch (Exception ex)
        {
            _s.McpErrors[item.Name] = ex.GetBaseException().Message;
            Core.Diagnostics.Log.Warn("MCP", "Connect failed " + item.Name, ex);
        }
        Refresh();
    }

    [RelayCommand]
    private void Disconnect(McpServerItem item)
    {
        _s.Tools.Disconnect(item.Name);
        Refresh();
    }

    [RelayCommand]
    private void Remove(McpServerItem item)
    {
        if (MessageBox.Show(L.F("Mcp_ConfirmRemove", item.Name), "Offline Chatter", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        _s.Tools.Disconnect(item.Name);
        _s.Mcp.Servers.Remove(item.Config);
        _s.Mcp.Save();
        Refresh();
    }

    /// <summary>Добавить сервер: команда (stdio) или URL (HTTP; адрес на /sse — старый протокол SSE).</summary>
    [RelayCommand]
    private async Task AddAsync()
    {
        var name = NewName.Trim();
        if (name.Length == 0 || (NewCommand.Trim().Length == 0 && NewUrl.Trim().Length == 0))
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
        if (NewUrl.Trim().Length > 0)
        {
            cfg.Url = NewUrl.Trim();
            cfg.Type = cfg.Url.TrimEnd('/').EndsWith("/sse", StringComparison.OrdinalIgnoreCase) ? "sse" : "http";
        }
        else
        {
            var parts = ServerLaunchOptions.SplitCommandLine(NewCommand.Trim());
            cfg.Command = parts[0];
            cfg.Args = parts.Skip(1).ToList();
        }
        _s.Mcp.Servers.Add(cfg);
        _s.Mcp.Save();
        NewName = NewCommand = NewUrl = string.Empty;
        Refresh();
        var item = Servers.FirstOrDefault(s => s.Name == name);
        if (item != null) await ConnectAsync(item);
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
    private void Reload()
    {
        foreach (var name in _s.Tools.Clients.Keys.ToList()) _s.Tools.Disconnect(name);
        _s.Mcp.Load();
        _s.ConnectMcpServers();
        Status = L.Get("Mcp_Connecting");
        Refresh();
    }
}
