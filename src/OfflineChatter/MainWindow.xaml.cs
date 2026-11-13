using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using OfflineChatter.Core.Storage;
using OfflineChatter.Theming;
using OfflineChatter.ViewModels;

namespace OfflineChatter;

public partial class MainWindow : Window
{
    private readonly MainViewModel _vm;

    public MainWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = _vm = vm;

        RestorePlacement(vm.Services.Settings.Current.Window);
        ApplyCompact();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.IsCompact) or nameof(MainViewModel.IsSidebarCollapsed)) ApplyCompact();
        };
        ThemeManager.Attach(this);
        PreviewKeyDown += OnShortcut;

        // Заголовок окна: в системных скинах его рисует Windows — туда и выводим название страницы («Чат - Offline Chatter»).
        // Во Fluent страница подписана в собственной полосе заголовка, а окно называется просто «Offline Chatter».
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(MainViewModel.CurrentPage)) UpdateTitle();
        };
        vm.Chat.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(ChatPageViewModel.Title)) UpdateTitle();
        };
        ThemeManager.Changed += OnThemeChanged;
        Closed += (_, _) => ThemeManager.Changed -= OnThemeChanged;
        UpdateTitle();
    }

    private void OnThemeChanged(object? sender, EventArgs e) => UpdateTitle();

    private void UpdateTitle()
    {
        const string app = "Offline Chatter";
        var page = _vm.CurrentPage?.Title;
        var native = ThemeManager.Current is { } t && (t.Skin != SkinKind.Fluent || t.HighContrast);
        Title = native && !string.IsNullOrWhiteSpace(page) ? page + " - " + app : app;
    }

    /// <summary>Ctrl+K — палитра команд, Ctrl+F — поиск по чатам.</summary>
    private void OnShortcut(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.Control) return;
        if (e.Key == System.Windows.Input.Key.K)
        {
            new Views.CommandPalette(_vm) { Owner = this }.Show();
            e.Handled = true;
        }
        else if (e.Key == System.Windows.Input.Key.F)
        {
            if (_vm.IsCompact) return;
            if (_vm.IsSidebarCollapsed)
            {
                OnRailSearch(this, new RoutedEventArgs());
                e.Handled = true;
                return;
            }
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Чёткость и сглаживание текста ставит ThemeManager (настройка «Шрифты», все окна и масштабы)
    }

    // ---------- Положение окна ----------

    private void RestorePlacement(WindowPlacementSettings p)
    {
        Width = p.Width;
        Height = p.Height;
        SidebarColumn.Width = new GridLength(p.SidebarWidth);

        if (p.Left is double left && p.Top is double top && IsOnScreen(left, top, p.Width, p.Height))
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
        {
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (p.Maximized) WindowState = WindowState.Maximized;
    }

    /// <summary>Заголовок окна должен попадать на виртуальный экран (монитор могли отключить).</summary>
    private static bool IsOnScreen(double left, double top, double width, double height)
    {
        var vx = SystemParameters.VirtualScreenLeft;
        var vy = SystemParameters.VirtualScreenTop;
        var vw = SystemParameters.VirtualScreenWidth;
        var vh = SystemParameters.VirtualScreenHeight;
        return left + width > vx + 50 && left < vx + vw - 50 && top >= vy - 10 && top < vy + vh - 50;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        var p = _vm.Services.Settings.Current.Window;
        var bounds = WindowState == WindowState.Normal ? new Rect(Left, Top, Width, Height) : RestoreBounds;
        if (!bounds.IsEmpty)
        {
            p.Left = bounds.Left;
            p.Top = bounds.Top;
            p.Width = bounds.Width;
            p.Height = bounds.Height;
        }
        p.Maximized = WindowState == WindowState.Maximized;
        base.OnClosing(e);
    }

    /// <summary>Ширина колонки иконок в свёрнутой боковой панели.</summary>
    private const double RailWidth = 56;

    /// <summary>
    /// Компактный режим — колонка боковой панели схлопывается полностью, свёрнутая панель — до колонки иконок.
    /// Ширина из настроек возвращается при разворачивании.
    /// </summary>
    private void ApplyCompact()
    {
        SidebarSplitter.Visibility = _vm.IsCompact || _vm.IsSidebarCollapsed ? Visibility.Collapsed : Visibility.Visible;
        if (_vm.IsCompact)
        {
            SidebarColumn.MinWidth = 0;
            SidebarColumn.Width = new GridLength(0);
        }
        else if (_vm.IsSidebarCollapsed)
        {
            SidebarColumn.MinWidth = RailWidth;
            SidebarColumn.Width = new GridLength(RailWidth);
        }
        else
        {
            SidebarColumn.MinWidth = 200;
            SidebarColumn.Width = new GridLength(_vm.Services.Settings.Current.Window.SidebarWidth);
        }
    }

    private void OnSidebarResized(object sender, DragCompletedEventArgs e)
    {
        if (!_vm.IsSidebarCollapsed && !_vm.IsCompact) _vm.Services.Settings.Current.Window.SidebarWidth = SidebarColumn.ActualWidth;
    }

    /// <summary>Поиск в свёрнутой панели: разворачиваем её и ставим курсор в поле поиска.</summary>
    private void OnRailSearch(object sender, RoutedEventArgs e)
    {
        _vm.IsSidebarCollapsed = false;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }), System.Windows.Threading.DispatcherPriority.Input);
    }
}
