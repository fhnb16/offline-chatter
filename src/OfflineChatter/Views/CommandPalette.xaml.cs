using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using OfflineChatter.Core.Storage;
using OfflineChatter.Localization;
using OfflineChatter.Theming;
using OfflineChatter.ViewModels;

namespace OfflineChatter.Views;

public sealed record PaletteItem(string Title, string Hint, Action Run);

/// <summary>Палитра команд (Ctrl+K): разделы, действия и чаты с поиском по подстроке.</summary>
public partial class CommandPalette : Window
{
    private readonly List<PaletteItem> _all;
    private bool _closing;

    public CommandPalette(MainViewModel main)
    {
        InitializeComponent();
        ThemeManager.Attach(this);
        var s = main.Services.Settings;
        _all = new List<PaletteItem>
        {
            new(L.Get("Chat_New"), "Ctrl+N", () => main.NewChatCommand.Execute(null)),
            new(L.Get("Export_Chat"), string.Empty, () => main.Chat.ExportChatCommand.Execute(null)),
            new(L.Get("Export_Print"), string.Empty, () => main.Chat.PrintCommand.Execute(null)),
            new(L.Get("Chat_ToggleInspector"), string.Empty, () => main.Chat.ToggleInspectorCommand.Execute(null)),
            new(L.Get("Palette_ToggleTheme"), string.Empty, () =>
            {
                var a = s.Current.Appearance;
                a.Mode = ThemeManager.Current?.IsDark == true ? ThemeMode.Light : ThemeMode.Dark;
                ThemeManager.Apply(a);
                s.Save();
            }),
            new(L.Get("Palette_StopServer"), string.Empty, () => main.Services.Engine.Server.Stop()),
        };
        foreach (PageKind k in Enum.GetValues(typeof(PageKind)))
        {
            if (k == PageKind.Chat) continue;
            var kind = k;
            _all.Add(new PaletteItem(L.F("Palette_Go", L.Get("Nav_" + kind)), L.Get("Palette_Section"), () => main.Navigate(kind)));
        }
        foreach (var c in main.Chat.Chats.Take(200))
        {
            var chat = c;
            _all.Add(new PaletteItem(chat.Title, L.Get("Nav_Chat"), () =>
            {
                main.Chat.SelectedChat = chat;
                main.Navigate(PageKind.Chat);
            }));
        }
        Filter(string.Empty);
        Loaded += (_, _) => Query.Focus();
    }

    private void Filter(string q)
    {
        var words = q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
        Items.ItemsSource = _all.Where(i => words.All(w => CultureInfo.CurrentCulture.CompareInfo.IndexOf(i.Title, w, CompareOptions.IgnoreCase) >= 0)).Take(60).ToList();
        Items.SelectedIndex = 0;
    }

    private void OnQueryChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => Filter(Query.Text);

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down:
                Items.SelectedIndex = Math.Min(Items.Items.Count - 1, Items.SelectedIndex + 1);
                Items.ScrollIntoView(Items.SelectedItem);
                e.Handled = true;
                break;
            case Key.Up:
                Items.SelectedIndex = Math.Max(0, Items.SelectedIndex - 1);
                Items.ScrollIntoView(Items.SelectedItem);
                e.Handled = true;
                break;
            case Key.Enter:
                Run();
                e.Handled = true;
                break;
            case Key.Escape:
                SafeClose();
                e.Handled = true;
                break;
        }
    }

    private void OnListKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Run();
        else if (e.Key == Key.Escape) SafeClose();
    }

    private void OnItemActivated(object sender, MouseButtonEventArgs e) => Run();

    private void Run()
    {
        if (Items.SelectedItem is not PaletteItem item) return;
        SafeClose();
        item.Run();
    }

    private void OnDeactivated(object? sender, EventArgs e) => SafeClose();

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
