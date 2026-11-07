using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace OfflineChatter.Controls;

/// <summary>
/// Значок поиска и подсказка (Tag) поверх обычного TextBox системного скина (Aero, XP, Classic).
/// Положение берётся из фактического начала текста (GetRectFromCharacterIndex), поэтому значок, подсказка
/// и введённый текст стоят на одной линии при любых внутренних отступах шаблона.
/// </summary>
public static class SearchDecoration
{
    public static readonly DependencyProperty IsEnabledProperty = DependencyProperty.RegisterAttached(
        "IsEnabled", typeof(bool), typeof(SearchDecoration), new PropertyMetadata(false, OnChanged));

    public static bool GetIsEnabled(DependencyObject o) => (bool)o.GetValue(IsEnabledProperty);
    public static void SetIsEnabled(DependencyObject o, bool v) => o.SetValue(IsEnabledProperty, v);

    private static readonly DependencyProperty AdornerProperty = DependencyProperty.RegisterAttached(
        "Adorner", typeof(SearchAdorner), typeof(SearchDecoration));

    private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextBox box) return;
        if ((bool)e.NewValue)
        {
            box.Loaded += Attach;
            box.Unloaded += Detach;
            if (box.IsLoaded) Attach(box, new RoutedEventArgs());
        }
        else
        {
            box.Loaded -= Attach;
            box.Unloaded -= Detach;
            Detach(box, new RoutedEventArgs());
        }
    }

    private static void Attach(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.GetValue(AdornerProperty) != null) return;
        var layer = AdornerLayer.GetAdornerLayer(box);
        if (layer == null) return;
        var adorner = new SearchAdorner(box);
        layer.Add(adorner);
        box.SetValue(AdornerProperty, adorner);
    }

    private static void Detach(object sender, RoutedEventArgs e)
    {
        var box = (TextBox)sender;
        if (box.GetValue(AdornerProperty) is SearchAdorner adorner)
        {
            AdornerLayer.GetAdornerLayer(box)?.Remove(adorner);
            adorner.Dispose();
            box.ClearValue(AdornerProperty);
        }
    }

    private sealed class SearchAdorner : Adorner, IDisposable
    {
        private readonly TextBox _box;

        public SearchAdorner(TextBox box) : base(box)
        {
            _box = box;
            IsHitTestVisible = false;
            box.TextChanged += Invalidate;
            box.SizeChanged += Invalidate;
            box.LayoutUpdated += OnLayout;
        }

        private double _lastX = double.NaN;

        // Начало текста известно только после разметки шаблона — перерисовываем, когда оно сдвинулось
        private void OnLayout(object? sender, EventArgs e)
        {
            var x = TextStart().X;
            if (!x.Equals(_lastX)) InvalidateVisual();
        }

        private void Invalidate(object sender, EventArgs e) => InvalidateVisual();

        private Point TextStart()
        {
            try
            {
                var r = _box.GetRectFromCharacterIndex(0);
                if (!r.IsEmpty && !double.IsInfinity(r.X)) return new Point(r.X, r.Y + r.Height / 2);
            }
            catch (ArgumentOutOfRangeException) { }
            return new Point(_box.Padding.Left + _box.BorderThickness.Left + 2, _box.ActualHeight / 2);
        }

        protected override void OnRender(DrawingContext dc)
        {
            var start = TextStart();
            _lastX = start.X;
            var brush = _box.TryFindResource("TextTertiaryBrush") as Brush ?? SystemColors.GrayTextBrush;

            if (Application.Current?.TryFindResource("IconSearch") is Geometry icon)
            {
                var pen = new Pen(brush, 1.3) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
                // Значок 16×16 слева от текста с промежутком 4 px
                dc.PushTransform(new TranslateTransform(Math.Max(2, start.X - 20), start.Y - 8));
                dc.DrawGeometry(null, pen, icon);
                dc.Pop();
            }

            if (_box.Text.Length == 0 && _box.Tag is string hint && hint.Length > 0)
            {
                var text = new FormattedText(hint, CultureInfo.CurrentUICulture, _box.FlowDirection,
                    new Typeface(_box.FontFamily, _box.FontStyle, _box.FontWeight, _box.FontStretch), _box.FontSize, brush,
                    VisualTreeHelper.GetDpi(this).PixelsPerDip)
                {
                    MaxTextWidth = Math.Max(1, _box.ActualWidth - start.X - 6),
                    MaxLineCount = 1,
                    Trimming = TextTrimming.CharacterEllipsis,
                };
                dc.DrawText(text, new Point(start.X, start.Y - text.Height / 2));
            }
        }

        public void Dispose()
        {
            _box.TextChanged -= Invalidate;
            _box.SizeChanged -= Invalidate;
            _box.LayoutUpdated -= OnLayout;
        }
    }
}
