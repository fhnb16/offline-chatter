using System;
using System.Windows;
using System.Windows.Controls;

namespace OfflineChatter.Controls;

/// <summary>
/// Две панели с собственной прокруткой, занимающие всю высоту страницы. На широком окне — рядом
/// (первая шириной <see cref="Ratio"/> от доступного, но в пределах <see cref="PrimaryMinWidth"/>…<see cref="PrimaryMaxWidth"/>),
/// на узком (уже <see cref="Breakpoint"/>) — друг под другом, первая занимает <see cref="StackedRatio"/> высоты.
/// Нужна там, где одна часть страницы (результат, превью) должна оставаться на виду, пока прокручивается другая.
/// </summary>
public sealed class SplitPanel : Panel
{
    public static readonly DependencyProperty BreakpointProperty = Register(nameof(Breakpoint), 720.0);
    public static readonly DependencyProperty RatioProperty = Register(nameof(Ratio), 0.45);
    public static readonly DependencyProperty PrimaryMinWidthProperty = Register(nameof(PrimaryMinWidth), 340.0);
    public static readonly DependencyProperty PrimaryMaxWidthProperty = Register(nameof(PrimaryMaxWidth), 600.0);
    public static readonly DependencyProperty StackedRatioProperty = Register(nameof(StackedRatio), 0.6);
    public static readonly DependencyProperty SpacingProperty = Register(nameof(Spacing), 12.0);

    private static DependencyProperty Register(string name, double value) => DependencyProperty.Register(
        name, typeof(double), typeof(SplitPanel), new FrameworkPropertyMetadata(value, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public double Breakpoint { get => (double)GetValue(BreakpointProperty); set => SetValue(BreakpointProperty, value); }
    public double Ratio { get => (double)GetValue(RatioProperty); set => SetValue(RatioProperty, value); }
    public double PrimaryMinWidth { get => (double)GetValue(PrimaryMinWidthProperty); set => SetValue(PrimaryMinWidthProperty, value); }
    public double PrimaryMaxWidth { get => (double)GetValue(PrimaryMaxWidthProperty); set => SetValue(PrimaryMaxWidthProperty, value); }
    public double StackedRatio { get => (double)GetValue(StackedRatioProperty); set => SetValue(StackedRatioProperty, value); }
    public double Spacing { get => (double)GetValue(SpacingProperty); set => SetValue(SpacingProperty, value); }

    /// <summary>Панели стоят рядом (а не друг под другом) при текущей ширине.</summary>
    public bool IsSideBySide { get; private set; }

    private (Rect Primary, Rect Secondary) Layout(Size size)
    {
        var w = double.IsInfinity(size.Width) ? Breakpoint : size.Width;
        // Внутри прокрутки высота бесконечна: даём панелям разумную высоту, а не растягиваем до бесконечности
        var h = double.IsInfinity(size.Height) ? 640 : size.Height;
        IsSideBySide = w >= Breakpoint;
        if (IsSideBySide)
        {
            var inner = Math.Max(0, w - Spacing);
            var pw = Math.Min(Math.Max(inner * Ratio, PrimaryMinWidth), Math.Max(PrimaryMinWidth, PrimaryMaxWidth));
            pw = Math.Min(pw, inner);
            return (new Rect(0, 0, pw, h), new Rect(pw + Spacing, 0, Math.Max(0, inner - pw), h));
        }
        var innerH = Math.Max(0, h - Spacing);
        var ph = Math.Round(innerH * Math.Min(1, Math.Max(0, StackedRatio)));
        return (new Rect(0, 0, w, ph), new Rect(0, ph + Spacing, w, Math.Max(0, innerH - ph)));
    }

    protected override Size MeasureOverride(Size available)
    {
        var (a, b) = Layout(available);
        if (InternalChildren.Count > 0) InternalChildren[0].Measure(a.Size);
        if (InternalChildren.Count > 1) InternalChildren[1].Measure(b.Size);
        for (var i = 2; i < InternalChildren.Count; i++) InternalChildren[i].Measure(new Size(0, 0));
        return new Size(double.IsInfinity(available.Width) ? Breakpoint : available.Width, a.Height + (IsSideBySide ? 0 : Spacing + b.Height));
    }

    protected override Size ArrangeOverride(Size final)
    {
        var (a, b) = Layout(final);
        if (InternalChildren.Count > 0) InternalChildren[0].Arrange(a);
        if (InternalChildren.Count > 1) InternalChildren[1].Arrange(b);
        for (var i = 2; i < InternalChildren.Count; i++) InternalChildren[i].Arrange(new Rect());
        return final;
    }
}
