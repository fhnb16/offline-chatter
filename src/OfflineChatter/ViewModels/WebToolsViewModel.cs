using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CommunityToolkit.Mvvm.ComponentModel;
using OfflineChatter.Core.Tools;

namespace OfflineChatter.ViewModels;

/// <summary>
/// Настройки web_fetch, web_search и города по умолчанию (страница «Инструменты»).
/// Каждое поле пишется в настройки сразу и по отдельности; числа приводятся к допустимым пределам.
/// </summary>
public sealed class WebToolsViewModel : ObservableObject
{
    private readonly AppServices _s;

    public WebToolsViewModel(AppServices services) => _s = services;

    private ToolsSettings T => _s.Settings.Current.Tools;
    private WebToolsSettings W => T.Web;

    public IReadOnlyList<SearchEngine> EngineOptions => SearchEngines.All;
    public IReadOnlyList<UserAgentPreset> UserAgentOptions => UserAgents.Presets;
    public IReadOnlyList<SafeSearchLevel> SafeSearchOptions { get; } = new[] { SafeSearchLevel.Off, SafeSearchLevel.Moderate, SafeSearchLevel.Strict };
    public IReadOnlyList<int> ReadPagesOptions { get; } = new[] { 0, 1, 2, 3 };

    private void Save(Action apply, [CallerMemberName] string? name = null)
    {
        apply();
        _s.Settings.Save();
        OnPropertyChanged(name);
    }

    private static int Clamp(int value, int min, int max) => Math.Max(min, Math.Min(max, value));

    private static string? Trimmed(string? s) => string.IsNullOrWhiteSpace(s) ? null : s!.Trim();

    // ---------------- Поиск ----------------

    public SearchEngine Engine
    {
        get => W.Engine;
        set
        {
            Save(() => W.Engine = value);
            OnPropertyChanged(nameof(IsCustomEngine));
            OnPropertyChanged(nameof(IsSearxng));
            OnPropertyChanged(nameof(EngineTemplate));
            OnPropertyChanged(nameof(HasTemplate));
            OnPropertyChanged(nameof(IsUnreliableEngine));
        }
    }

    public bool IsCustomEngine => W.Engine == SearchEngine.Custom;
    public bool IsSearxng => W.Engine == SearchEngine.Searxng;
    public string EngineTemplate => SearchEngines.Template(W.Engine);
    public bool HasTemplate => EngineTemplate.Length > 0;

    /// <summary>Системы, которые часто отвечают программам проверкой «я не робот» или требуют JavaScript.</summary>
    public bool IsUnreliableEngine => W.Engine is SearchEngine.Google or SearchEngine.Yandex or SearchEngine.Startpage or SearchEngine.Yahoo or SearchEngine.Mojeek;

    public string CustomSearchUrl
    {
        get => W.CustomSearchUrl ?? string.Empty;
        set => Save(() => W.CustomSearchUrl = Trimmed(value));
    }

    public string SearxngUrl
    {
        get => T.SearxngUrl ?? string.Empty;
        set => Save(() => T.SearxngUrl = Trimmed(value));
    }

    public string Region
    {
        get => W.Region ?? string.Empty;
        set => Save(() => W.Region = Trimmed(value)?.ToLowerInvariant());
    }

    public SafeSearchLevel SafeSearch
    {
        get => W.SafeSearch;
        set => Save(() => W.SafeSearch = value);
    }

    public int MaxResults
    {
        get => W.MaxResults;
        set => Save(() => W.MaxResults = Clamp(value, 1, 20));
    }

    public bool Snippets
    {
        get => W.Snippets;
        set => Save(() => W.Snippets = value);
    }

    public int ReadPages
    {
        get => W.ReadPages;
        set
        {
            Save(() => W.ReadPages = Clamp(value, 0, 3));
            OnPropertyChanged(nameof(ReadsPages));
        }
    }

    public bool ReadsPages => W.ReadPages > 0;

    public int ReadPageChars
    {
        get => W.ReadPageChars;
        set => Save(() => W.ReadPageChars = Clamp(value, 500, 10000));
    }

    public string ExcludeDomains
    {
        get => string.Join(", ", W.ExcludeDomains);
        set => Save(() => W.ExcludeDomains = WebToolsSettings.ParseDomains(value));
    }

    // ---------------- Запросы ----------------

    public UserAgentPreset UserAgent
    {
        get => W.UserAgent;
        set
        {
            Save(() => W.UserAgent = value);
            OnPropertyChanged(nameof(IsCustomUserAgent));
            OnPropertyChanged(nameof(UserAgentPreview));
        }
    }

    public bool IsCustomUserAgent => W.UserAgent == UserAgentPreset.Custom;

    /// <summary>Строка, которая действительно уйдёт в заголовке.</summary>
    public string UserAgentPreview => UserAgents.Resolve(W.UserAgent, W.CustomUserAgent, _s.Http.UserAgent);

    public string CustomUserAgent
    {
        get => W.CustomUserAgent ?? string.Empty;
        set
        {
            Save(() => W.CustomUserAgent = Trimmed(value));
            OnPropertyChanged(nameof(UserAgentPreview));
        }
    }

    public string AcceptLanguage
    {
        get => W.AcceptLanguage ?? string.Empty;
        set => Save(() => W.AcceptLanguage = Trimmed(value));
    }

    public int TimeoutSeconds
    {
        get => W.TimeoutSeconds;
        set => Save(() => W.TimeoutSeconds = Clamp(value, 3, 120));
    }

    public int MaxDownloadKb
    {
        get => W.MaxDownloadKb;
        set => Save(() => W.MaxDownloadKb = Clamp(value, 64, 16384));
    }

    public bool AllowLocalAddresses
    {
        get => W.AllowLocalAddresses;
        set => Save(() => W.AllowLocalAddresses = value);
    }

    // ---------------- Очистка страниц ----------------

    public bool MainContentOnly
    {
        get => W.MainContentOnly;
        set => Save(() => W.MainContentOnly = value);
    }

    public bool StripNavigation
    {
        get => W.StripNavigation;
        set => Save(() => W.StripNavigation = value);
    }

    public bool StripBoilerplate
    {
        get => W.StripBoilerplate;
        set => Save(() => W.StripBoilerplate = value);
    }

    public bool StripComments
    {
        get => W.StripComments;
        set => Save(() => W.StripComments = value);
    }

    public bool DropDuplicateLines
    {
        get => W.DropDuplicateLines;
        set => Save(() => W.DropDuplicateLines = value);
    }

    public bool KeepLinks
    {
        get => W.KeepLinks;
        set => Save(() => W.KeepLinks = value);
    }

    public bool Markdown
    {
        get => W.Markdown;
        set => Save(() => W.Markdown = value);
    }

    public int MaxChars
    {
        get => W.MaxChars;
        set => Save(() => W.MaxChars = Clamp(value, 1000, 60000));
    }

    // ---------------- Погода ----------------

    /// <summary>Пусто — город никуда не передаётся (ни в описание инструмента, ни в аргументы).</summary>
    public string DefaultCity
    {
        get => T.DefaultCity ?? string.Empty;
        set => Save(() => T.DefaultCity = DefaultLocation.Normalize(value));
    }
}
