using CommunityToolkit.Mvvm.ComponentModel;
using OfflineChatter.Localization;

namespace OfflineChatter.ViewModels;

public enum PageKind
{
    Chat,
    Models,
    Engine,
    Providers,
    Mcp,
    Benchmarks,
    Logs,
    Settings,
    Welcome,
    Compare,
}

/// <summary>Страница в основной области окна.</summary>
public abstract class PageViewModel : ObservableObject
{
    public abstract PageKind Kind { get; }

    public virtual string Title => L.Get("Nav_" + Kind);

    /// <summary>Вызывается при каждом переходе на страницу.</summary>
    public virtual void OnNavigatedTo() { }
}

/// <summary>Пункт навигации в боковой панели.</summary>
public sealed class NavItem
{
    public NavItem(PageKind kind, string iconKey)
    {
        Kind = kind;
        IconKey = iconKey;
    }

    public PageKind Kind { get; }
    public string IconKey { get; }
    public string Title => L.Get("Nav_" + Kind);
}
