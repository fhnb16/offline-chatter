using System;
using System.Collections.Specialized;
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

        RestorePlacement(App.Settings.Current.Window);
        ThemeManager.Attach(this);

        _vm.PropertyChanged += OnViewModelPropertyChanged;
        HookMessages(_vm.SelectedChat);
        Loaded += (_, _) => InputBox.Focus();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        UpdateTextFormatting(VisualTreeHelper.GetDpi(this));
    }

    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    {
        base.OnDpiChanged(oldDpi, newDpi);
        UpdateTextFormatting(newDpi);
    }

    /// <summary>При 100% масштабе Display-режим даёт чёткий текст (особенно на Win7), на HiDPI лучше Ideal.</summary>
    private void UpdateTextFormatting(DpiScale dpi) =>
        TextOptions.SetTextFormattingMode(this, dpi.DpiScaleX <= 1.0 ? TextFormattingMode.Display : TextFormattingMode.Ideal);

    // ---------- Автопрокрутка к последнему сообщению ----------

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.SelectedChat)) HookMessages(_vm.SelectedChat);
    }

    private ChatListItem? _hookedChat;

    private void HookMessages(ChatListItem? chat)
    {
        if (_hookedChat != null) _hookedChat.Messages.CollectionChanged -= OnMessagesChanged;
        _hookedChat = chat;
        if (chat != null) chat.Messages.CollectionChanged += OnMessagesChanged;
    }

    private void OnMessagesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Add)
            Dispatcher.BeginInvoke(new Action(() => MessagesScroll.ScrollToEnd()), System.Windows.Threading.DispatcherPriority.Background);
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
        var p = App.Settings.Current.Window;
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

    private void OnSidebarResized(object sender, DragCompletedEventArgs e) =>
        App.Settings.Current.Window.SidebarWidth = SidebarColumn.ActualWidth;
}
