using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace OfflineChatter.Controls;

/// <summary>
/// Раскладка карточек в колонки по ширине: столько колонок, сколько помещается при <see cref="MinColumnWidth"/>
/// (не больше <see cref="MaxColumns"/>). В узком окне карточки идут друг под другом, на широком — рядом,
/// без пустого места справа. Высота строки — по самой высокой карточке в ней.
/// </summary>
public sealed class ResponsiveGrid : Panel
{
    public static readonly DependencyProperty MinColumnWidthProperty = DependencyProperty.Register(
        nameof(MinColumnWidth), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(340.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty MaxColumnsProperty = DependencyProperty.Register(
        nameof(MaxColumns), typeof(int), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(3, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty SpacingProperty = DependencyProperty.Register(
        nameof(Spacing), typeof(double), typeof(ResponsiveGrid),
        new FrameworkPropertyMetadata(12.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double MinColumnWidth
    {
        get => (double)GetValue(MinColumnWidthProperty);
        set => SetValue(MinColumnWidthProperty, value);
    }

    public int MaxColumns
    {
        get => (int)GetValue(MaxColumnsProperty);
        set => SetValue(MaxColumnsProperty, value);
    }

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    private int Columns(double width)
    {
        if (double.IsInfinity(width)) return 1;
        var n = (int)Math.Floor((width + Spacing) / (MinColumnWidth + Spacing));
        return Math.Max(1, Math.Min(Math.Max(1, MaxColumns), n));
    }

    private List<UIElement> Visible()
    {
        var list = new List<UIElement>();
        foreach (UIElement c in InternalChildren)
            if (c.Visibility != Visibility.Collapsed) list.Add(c);
        return list;
    }

    protected override Size MeasureOverride(Size available)
    {
        var items = Visible();
        var cols = Columns(available.Width);
        var colWidth = double.IsInfinity(available.Width) ? double.PositiveInfinity : (available.Width - Spacing * (cols - 1)) / cols;
        double height = 0, maxWidth = 0;
        for (var i = 0; i < items.Count; i += cols)
        {
            double rowHeight = 0;
            for (var j = i; j < Math.Min(items.Count, i + cols); j++)
            {
                items[j].Measure(new Size(colWidth, double.PositiveInfinity));
                rowHeight = Math.Max(rowHeight, items[j].DesiredSize.Height);
                maxWidth = Math.Max(maxWidth, items[j].DesiredSize.Width);
            }
            height += rowHeight + (i + cols < items.Count ? Spacing : 0);
        }
        var width = double.IsInfinity(available.Width) ? maxWidth : available.Width;
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var items = Visible();
        var cols = Columns(final.Width);
        var colWidth = (final.Width - Spacing * (cols - 1)) / cols;
        double y = 0;
        for (var i = 0; i < items.Count; i += cols)
        {
            double rowHeight = 0;
            for (var j = i; j < Math.Min(items.Count, i + cols); j++) rowHeight = Math.Max(rowHeight, items[j].DesiredSize.Height);
            for (var j = i; j < Math.Min(items.Count, i + cols); j++)
                items[j].Arrange(new Rect((j - i) * (colWidth + Spacing), y, colWidth, rowHeight));
            y += rowHeight + Spacing;
        }
        return final;
    }
}
