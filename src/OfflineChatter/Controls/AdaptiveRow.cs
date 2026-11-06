using System;
using System.Windows;
using System.Windows.Controls;

namespace OfflineChatter.Controls;

/// <summary>
/// Строка настройки: подпись слева, контрол справа; когда места меньше <see cref="Breakpoint"/> —
/// контрол переносится под подпись и растягивается на всю ширину. Нужна, чтобы настройки одинаково
/// читались и в узком окне, и на весь экран (без фиксированной ширины колонок).
/// Первый дочерний элемент — подпись, второй — контрол.
/// </summary>
public sealed class AdaptiveRow : Panel
{
    public static readonly DependencyProperty BreakpointProperty = DependencyProperty.Register(
        nameof(Breakpoint), typeof(double), typeof(AdaptiveRow),
        new FrameworkPropertyMetadata(520.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    /// <summary>Минимальная ширина контрола в широком режиме (выпадающие списки выравниваются по одной линии).</summary>
    public static readonly DependencyProperty ControlMinWidthProperty = DependencyProperty.Register(
        nameof(ControlMinWidth), typeof(double), typeof(AdaptiveRow),
        new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Breakpoint
    {
        get => (double)GetValue(BreakpointProperty);
        set => SetValue(BreakpointProperty, value);
    }

    public double ControlMinWidth
    {
        get => (double)GetValue(ControlMinWidthProperty);
        set => SetValue(ControlMinWidthProperty, value);
    }

    private const double Gap = 16;
    private const double StackGap = 8;

    private bool IsWide(double width) => double.IsInfinity(width) || width >= Breakpoint;

    protected override Size MeasureOverride(Size available)
    {
        var header = InternalChildren.Count > 0 ? InternalChildren[0] : null;
        var control = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        if (header == null) return new Size();
        if (control == null || control.Visibility == Visibility.Collapsed)
        {
            header.Measure(available);
            return header.DesiredSize;
        }

        if (IsWide(available.Width))
        {
            var maxControl = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width * 0.55;
            control.Measure(new Size(maxControl, available.Height));
            var cw = Math.Min(maxControl, Math.Max(control.DesiredSize.Width, ControlMinWidth));
            var hw = double.IsInfinity(available.Width) ? double.PositiveInfinity : Math.Max(0, available.Width - cw - Gap);
            header.Measure(new Size(hw, double.PositiveInfinity));
            return new Size(header.DesiredSize.Width + Gap + cw, Math.Max(header.DesiredSize.Height, control.DesiredSize.Height));
        }

        header.Measure(new Size(available.Width, double.PositiveInfinity));
        control.Measure(new Size(available.Width, double.PositiveInfinity));
        return new Size(Math.Max(header.DesiredSize.Width, control.DesiredSize.Width),
            header.DesiredSize.Height + StackGap + control.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        var header = InternalChildren.Count > 0 ? InternalChildren[0] : null;
        var control = InternalChildren.Count > 1 ? InternalChildren[1] : null;
        if (header == null) return final;
        if (control == null || control.Visibility == Visibility.Collapsed)
        {
            header.Arrange(new Rect(final));
            return final;
        }

        if (IsWide(final.Width))
        {
            var cw = Math.Min(final.Width * 0.55, Math.Max(control.DesiredSize.Width, ControlMinWidth));
            var hw = Math.Max(0, final.Width - cw - Gap);
            var hh = header.DesiredSize.Height;
            var ch = control.DesiredSize.Height;
            header.Arrange(new Rect(0, Math.Max(0, (final.Height - hh) / 2), hw, hh));
            control.Arrange(new Rect(final.Width - cw, Math.Max(0, (final.Height - ch) / 2), cw, ch));
        }
        else
        {
            var hh = header.DesiredSize.Height;
            header.Arrange(new Rect(0, 0, final.Width, hh));
            control.Arrange(new Rect(0, hh + StackGap, final.Width, control.DesiredSize.Height));
        }
        return final;
    }
}
