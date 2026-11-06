using System;
using System.Collections.Generic;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OfflineChatter.ViewModels;

/// <summary>Оболочка: навигация между страницами и общий список чатов в боковой панели.</summary>
public sealed partial class MainViewModel : ObservableObject
{
    private readonly Dictionary<PageKind, PageViewModel> _pages = new();
    private bool _syncingSelection;

    public MainViewModel(AppServices services)
    {
        Services = services;
        AllNavItems = new List<NavItem>
        {
            new(PageKind.Models, "IconCube"),
            new(PageKind.Engine, "IconChip"),
            new(PageKind.Providers, "IconCloud"),
            new(PageKind.Mcp, "IconPlug"),
            new(PageKind.Compare, "IconBranch"),
            new(PageKind.Benchmarks, "IconGauge"),
            new(PageKind.Logs, "IconList"),
            new(PageKind.Settings, "IconSettings"),
        };
        RebuildNav();
        Downloads = new DownloadsViewModel(services.Downloads);
        Chat = new ChatPageViewModel(services);
        Register(Chat);
        Register(new SettingsViewModel(services, this));
        Register(new EngineViewModel(services, this));
        Register(new ModelsViewModel(services, this));
        Register(new ProvidersViewModel(services));
        Register(new BenchmarksViewModel(services, this));
        Register(new LogsViewModel(services));
        Register(new McpViewModel(services));
        Register(new WelcomeViewModel(services, this));
        Register(new CompareViewModel(services, this));

        services.UpdateFound += (_, _) => System.Windows.Application.Current?.Dispatcher.BeginInvoke(new Action(() => OnPropertyChanged(nameof(UpdateBadge))));

        Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatPageViewModel.SelectedChat) && Chat.SelectedChat != null && !_syncingSelection
                && !ReferenceEquals(CurrentPage, Chat))
                Navigate(PageKind.Chat);
        };

        _currentPage = Chat;
    }

    public AppServices Services { get; }

    /// <summary>Общий список загрузок (показывается в разделах «Модели» и «Движок», прогресс — в панели задач).</summary>
    public DownloadsViewModel Downloads { get; }

    public ChatPageViewModel Chat { get; }

    /// <summary>Все разделы (для настройки боковой панели).</summary>
    public IReadOnlyList<NavItem> AllNavItems { get; }

    /// <summary>Разделы, видимые в боковой панели.</summary>
    public System.Collections.ObjectModel.ObservableCollection<NavItem> NavItems { get; } = new();

    /// <summary>Текст значка «есть обновление» в боковой панели.</summary>
    public string? UpdateBadge => Services.PendingUpdate is { } u ? Localization.L.F("Update_Available", u.LatestTag) : null;

    public bool IsOffline => Services.Settings.Current.Network.OfflineMode;

    public void NotifyOfflineChanged() => OnPropertyChanged(nameof(IsOffline));

    [RelayCommand]
    private void OpenPrivacy() => GetPage<SettingsViewModel>()?.Show(SettingsSection.Privacy);

    [RelayCommand]
    private void OpenUpdates() => GetPage<SettingsViewModel>()?.Show(SettingsSection.About);

    public bool IsPageVisible(PageKind kind) =>
        kind == PageKind.Settings || !Services.Settings.Current.Window.HiddenPages.Contains(kind.ToString(), StringComparer.OrdinalIgnoreCase);

    /// <summary>Показать или убрать раздел из боковой панели. «Настройки» убрать нельзя — иначе до них не добраться.</summary>
    public void SetPageVisible(PageKind kind, bool visible)
    {
        if (kind == PageKind.Settings) return;
        var hidden = Services.Settings.Current.Window.HiddenPages;
        hidden.RemoveAll(h => string.Equals(h, kind.ToString(), StringComparison.OrdinalIgnoreCase));
        if (!visible) hidden.Add(kind.ToString());
        Services.Settings.Save();
        RebuildNav();
    }

    private void RebuildNav()
    {
        var selected = SelectedNav?.Kind;
        NavItems.Clear();
        foreach (var n in AllNavItems.Where(n => IsPageVisible(n.Kind))) NavItems.Add(n);
        if (selected != null)
        {
            _syncingSelection = true;
            SelectedNav = NavItems.FirstOrDefault(n => n.Kind == selected);
            _syncingSelection = false;
        }
    }

    [ObservableProperty] private PageViewModel _currentPage;

    [ObservableProperty] private NavItem? _selectedNav;

    public void Register(PageViewModel page) => _pages[page.Kind] = page;

    public T? GetPage<T>() where T : PageViewModel => _pages.Values.OfType<T>().FirstOrDefault();

    partial void OnSelectedNavChanged(NavItem? value)
    {
        if (value != null && !_syncingSelection) Navigate(value.Kind);
    }

    [RelayCommand]
    public void Navigate(PageKind kind)
    {
        if (!_pages.TryGetValue(kind, out var page))
            page = _pages[kind] = new PlaceholderPageViewModel(kind);

        _syncingSelection = true;
        try
        {
            if (kind == PageKind.Chat)
            {
                SelectedNav = null;
            }
            else
            {
                SelectedNav = NavItems.FirstOrDefault(n => n.Kind == kind);
                Chat.SelectedChat = null;
            }
        }
        finally
        {
            _syncingSelection = false;
        }

        CurrentPage = page;
        page.OnNavigatedTo();
    }

    [RelayCommand]
    private void NewChat()
    {
        Chat.NewChatCommand.Execute(null);
        Navigate(PageKind.Chat);
    }
}

/// <summary>Временная заглушка для разделов, которые ещё не реализованы.</summary>
public sealed class PlaceholderPageViewModel : PageViewModel
{
    public PlaceholderPageViewModel(PageKind kind) => Kind = kind;

    public override PageKind Kind { get; }
}
