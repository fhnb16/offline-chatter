using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Tools;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

public sealed record ToolPresetOption(ToolPreset Value, string Title);

/// <summary>Инструмент в правой панели чата: галочка и цена описания в токенах.</summary>
public sealed class ChatToolItem : ObservableObject
{
    private readonly Action<ChatToolItem> _toggled;
    private bool _isOn;

    public ChatToolItem(ToolDefinition tool, int tokens, bool isOn, Action<ChatToolItem> toggled)
    {
        Tool = tool;
        Tokens = tokens;
        _isOn = isOn;
        _toggled = toggled;
    }

    public ToolDefinition Tool { get; }
    public string Name => Tool.Name;
    public string Hint => Tool.Source == "builtin" ? Tool.Description : Tool.Description + Environment.NewLine + "MCP: " + Tool.Source;
    public int Tokens { get; }
    public string TokensText => "~" + Tokens.ToString("N0", CultureInfo.CurrentCulture);

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (!SetProperty(ref _isOn, value)) return;
            _toggled(this);
        }
    }

    /// <summary>Сменить состояние без реакции (при смене набора).</summary>
    public void SetSilently(bool value)
    {
        _isOn = value;
        OnPropertyChanged(nameof(IsOn));
    }
}

/// <summary>
/// Набор инструментов чата (правая панель): готовые наборы и галочки с ценой каждого инструмента. Описания всех
/// включённых инструментов уходят в каждом запросе, поэтому выключенное в чате модели не отправляется.
/// По умолчанию — «Все»: модель получает то же, что и до появления наборов.
/// </summary>
public sealed partial class ChatToolSetViewModel : ObservableObject
{
    private readonly AppServices _s;
    private readonly Action _changed;
    private ChatSession? _session;
    private bool _loading;

    public ChatToolSetViewModel(AppServices services, Action changed)
    {
        _s = services;
        _changed = changed;
        Presets = new[] { ToolPreset.All, ToolPreset.None, ToolPreset.Math, ToolPreset.Web, ToolPreset.Custom }
            .Select(p => new ToolPresetOption(p, L.Get("Enum_ToolPreset_" + p))).ToList();
        // Подключился или отключился MCP-сервер, сменилось согласие или офлайн-режим — список и цена другие
        _s.Tools.Changed += (_, _) => Application.Current?.Dispatcher.BeginInvoke(new Action(Refresh));
    }

    public IReadOnlyList<ToolPresetOption> Presets { get; }
    public ObservableCollection<ChatToolItem> Items { get; } = new();

    [ObservableProperty] private ToolPresetOption? _preset;

    /// <summary>Сколько инструментов и токенов уходит в каждом запросе этого чата.</summary>
    public int SentCount { get; private set; }
    public int SentTokens { get; private set; }

    public string Summary => SentCount == 0
        ? L.Get("Tools_SentNone")
        : L.F("Tools_Sent", SentCount, Items.Count, SentTokens.ToString("N0", CultureInfo.CurrentCulture));

    public bool HasItems => Items.Count > 0;

    /// <summary>Какие инструменты отправлять модели в этом чате; null — никакие.</summary>
    public static Func<ToolDefinition, bool>? EnabledFilter(AppServices s, ChatSession chat)
    {
        var preset = chat.EffectiveToolPreset(s.Settings.Current.Tools.EnabledByDefault);
        if (preset == ToolPreset.None) return null;
        var ids = chat.ToolIds == null ? null : new HashSet<string>(chat.ToolIds, StringComparer.Ordinal);
        bool Filter(ToolDefinition t) => ToolPresets.IsEnabled(t, preset, ids);
        return s.Tools.All().Any(t => s.Tools.IsOffered(t) && Filter(t)) ? Filter : null;
    }

    public void Load(ChatSession? session)
    {
        _session = session;
        Refresh();
    }

    /// <summary>Пересобрать список из реестра инструментов и набора чата.</summary>
    public void Refresh()
    {
        _loading = true;
        try
        {
            Items.Clear();
            var session = _session;
            var preset = session?.EffectiveToolPreset(_s.Settings.Current.Tools.EnabledByDefault) ?? ToolPreset.All;
            Preset = Presets.First(p => p.Value == preset);
            var filter = session == null ? null : EnabledFilter(_s, session);
            foreach (var t in _s.Tools.All().Where(_s.Tools.IsOffered))
                Items.Add(new ChatToolItem(t, _s.Tools.EstimateTokens(t), filter?.Invoke(t) == true, OnToggled));
        }
        finally
        {
            _loading = false;
        }
        UpdateTotals();
    }

    private void UpdateTotals()
    {
        var on = Items.Where(i => i.IsOn).ToList();
        SentCount = on.Count;
        SentTokens = on.Sum(i => i.Tokens);
        OnPropertyChanged(nameof(SentCount));
        OnPropertyChanged(nameof(SentTokens));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasItems));
    }

    partial void OnPresetChanged(ToolPresetOption? value)
    {
        if (_loading || value == null || _session == null) return;
        var s = _session;
        if (value.Value == ToolPreset.Custom)
        {
            // «Свой набор» начинается с того, что включено сейчас
            s.ToolIds ??= Items.Where(i => i.IsOn).Select(i => i.Tool.Id).ToList();
        }
        s.ToolPreset = value.Value;
        s.Tools = value.Value != ToolPreset.None;
        Refresh();
        _changed();
    }

    private void OnToggled(ChatToolItem item)
    {
        if (_loading || _session == null) return;
        // Галочка — это свой набор: список включённых сейчас
        _session.ToolPreset = ToolPreset.Custom;
        _session.ToolIds = Items.Where(i => i.IsOn).Select(i => i.Tool.Id).ToList();
        _session.Tools = _session.ToolIds.Count > 0;
        _loading = true;
        Preset = Presets.First(p => p.Value == ToolPreset.Custom);
        _loading = false;
        UpdateTotals();
        _changed();
    }
}
