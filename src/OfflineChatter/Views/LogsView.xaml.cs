using System;
using System.Collections.Specialized;
using System.Windows.Controls;
using System.Windows.Threading;
using OfflineChatter.ViewModels;

namespace OfflineChatter.Views;

public partial class LogsView : UserControl
{
    public LogsView()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is LogsViewModel vm)
                vm.Entries.CollectionChanged += OnEntriesChanged;
        };
    }

    /// <summary>Новые записи внизу — показываем последнюю.</summary>
    private void OnEntriesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            if (Entries.Items.Count > 0) Entries.ScrollIntoView(Entries.Items[Entries.Items.Count - 1]);
        }), DispatcherPriority.Background);
    }
}
