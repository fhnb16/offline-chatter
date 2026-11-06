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
        ThemeManager.Attach(this);
        PreviewKeyDown += OnShortcut;
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
            SearchBox.Focus();
            SearchBox.SelectAll();
            e.Handled = true;
        }
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

    private void OnSidebarResized(object sender, DragCompletedEventArgs e) =>
        _vm.Services.Settings.Current.Window.SidebarWidth = SidebarColumn.ActualWidth;
}
