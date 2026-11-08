using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace OfflineChatter.Controls;

/// <summary>
/// Автопрокрутка журналов: при новых строках список или TextBox прокручивается к концу,
/// пока пользователь сам не отмотал вверх. Вернулся к концу — прокрутка снова следует за выводом.
/// Использование: <c>c:AutoScroll.ToEnd="True"</c> на TextBox, ListBox или ItemsControl внутри ScrollViewer.
/// </summary>
public static class AutoScroll
{
    public static readonly DependencyProperty ToEndProperty = DependencyProperty.RegisterAttached(
        "ToEnd", typeof(bool), typeof(AutoScroll), new PropertyMetadata(false, OnToEndChanged));

    public static bool GetToEnd(DependencyObject d) => (bool)d.GetValue(ToEndProperty);
    public static void SetToEnd(DependencyObject d, bool value) => d.SetValue(ToEndProperty, value);

    // Состояние «прилипли к концу» хранится на самом элементе
    private static readonly DependencyProperty StickProperty = DependencyProperty.RegisterAttached(
        "Stick", typeof(bool), typeof(AutoScroll), new PropertyMetadata(true));

    private static readonly DependencyProperty HandlerProperty = DependencyProperty.RegisterAttached(
        "Handler", typeof(NotifyCollectionChangedEventHandler), typeof(AutoScroll));

    private static void OnToEndChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not FrameworkElement fe) return;
        if ((bool)e.NewValue)
        {
            fe.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
            if (fe is TextBox tb) tb.TextChanged += OnTextChanged;
            if (fe is ItemsControl ic)
            {
                NotifyCollectionChangedEventHandler h = (_, _) => OnContentChanged(ic);
                ic.SetValue(HandlerProperty, h);
                ((INotifyCollectionChanged)ic.Items).CollectionChanged += h;
            }
            fe.Loaded += OnLoaded;
        }
        else
        {
            fe.RemoveHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(OnScrollChanged));
            if (fe is TextBox tb) tb.TextChanged -= OnTextChanged;
            if (fe is ItemsControl ic && ic.GetValue(HandlerProperty) is NotifyCollectionChangedEventHandler h)
                ((INotifyCollectionChanged)ic.Items).CollectionChanged -= h;
            fe.Loaded -= OnLoaded;
        }
    }

    private static void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe) ScrollToEndLater(fe);
    }

    private static void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (sender is not DependencyObject d || e.OriginalSource is not ScrollViewer sv) return;
        // Прокрутил пользователь (размер содержимого не менялся) — запоминаем, у конца ли он
        if (e.ExtentHeightChange == 0 && e.ViewportHeightChange == 0)
            d.SetValue(StickProperty, sv.VerticalOffset >= sv.ScrollableHeight - 2);
        else if ((bool)d.GetValue(StickProperty))
            sv.ScrollToEnd();
    }

    private static void OnTextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is TextBox tb) OnContentChanged(tb);
    }

    private static void OnContentChanged(FrameworkElement fe)
    {
        if ((bool)fe.GetValue(StickProperty)) ScrollToEndLater(fe);
    }

    // После разметки: сброс списка (Clear + Add) на мгновение ставит прокрутку в начало
    private static void ScrollToEndLater(FrameworkElement fe) =>
        fe.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (FindScrollViewer(fe) is { } sv)
            {
                sv.ScrollToEnd();
                fe.SetValue(StickProperty, true);
            }
        }));

    private static ScrollViewer? FindScrollViewer(DependencyObject d)
    {
        if (d is ScrollViewer sv) return sv;
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(d); i++)
            if (FindScrollViewer(VisualTreeHelper.GetChild(d, i)) is { } found) return found;
        return null;
    }
}
