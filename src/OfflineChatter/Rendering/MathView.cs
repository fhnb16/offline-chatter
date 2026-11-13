using System;
using System.Collections.Concurrent;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Theming;
using WpfMath.Parsers;
using WpfMath.Rendering;
using XamlMath;

namespace OfflineChatter.Rendering;

/// <summary>
/// Формула LaTeX (XAML-Math) как векторный контур: цвет берётся из темы, масштабируется без потерь.
/// Если формулу разобрать не удалось — показывается исходный LaTeX моноширинным шрифтом.
/// </summary>
public static class MathView
{
    private static readonly ConcurrentDictionary<string, Geometry?> Cache = new();

    public static FrameworkElement Create(string latex, bool display, double fontSize)
    {
        var scale = display ? fontSize * 1.25 : fontSize * 1.1;
        var key = (display ? "D|" : "T|") + scale.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "|" + latex;
        var geometry = Cache.GetOrAdd(key, _ => Render(latex, display, scale));
        if (geometry == null) return Fallback(latex, display);

        var path = new Path
        {
            Data = geometry,
            SnapsToDevicePixels = false,
            ToolTip = latex,
            Cursor = System.Windows.Input.Cursors.Arrow,
        };
        // Цвет наследуется от текста вокруг (в пузыре пользователя он другой, чем в ответе)
        path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding("(TextElement.Foreground)") { RelativeSource = System.Windows.Data.RelativeSource.Self });
        var bounds = geometry.Bounds;
        path.Width = Math.Max(1, bounds.Right);
        path.Height = Math.Max(1, bounds.Bottom);
        path.Margin = display ? new Thickness(0, 6, 0, 6) : new Thickness(1, 0, 1, 0);
        path.HorizontalAlignment = display ? HorizontalAlignment.Center : HorizontalAlignment.Left;
        path.Tag = latex;
        path.ContextMenu = CopyMenu(latex);
        return path;
    }

    private static Geometry? Render(string latex, bool display, double scale)
    {
        try
        {
            var formula = WpfTeXFormulaParser.Instance.Parse(latex);
            var env = WpfTeXEnvironment.Create(display ? TexStyle.Display : TexStyle.Text, scale, "Segoe UI", Brushes.Black, null);
            var g = formula.RenderToGeometry(env, scale, 0, 0);
            g.Freeze();
            return g;
        }
        catch (Exception ex)
        {
            // XAML-Math бросает разные типы (TexParseException, SymbolNotFound…) — для пользователя это просто «не удалось»
            Log.Debug("Math", "Cannot render formula: " + ex.Message, new { latex });
            return null;
        }
    }

    /// <summary>Проверка без отрисовки (для экспорта и тестов).</summary>
    public static bool CanRender(string latex)
    {
        try
        {
            WpfTeXFormulaParser.Instance.Parse(latex);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static FrameworkElement Fallback(string latex, bool display)
    {
        var tb = new TextBlock
        {
            Text = display ? latex : "$" + latex + "$",
            TextWrapping = display ? TextWrapping.Wrap : TextWrapping.NoWrap,
            ToolTip = latex,
            ContextMenu = CopyMenu(latex),
        };
        tb.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.MonoFontFamily);
        tb.SetResourceReference(TextBlock.FontSizeProperty, ThemeKeys.CodeFontSize);
        tb.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.StatusWarn);
        return tb;
    }

    private static ContextMenu CopyMenu(string latex)
    {
        var menu = new ContextMenu();
        var item = new MenuItem { Header = Localization.L.Get("Chat_CopyLatex") };
        item.Click += (_, _) => Clipboard.SetText(latex);
        menu.Items.Add(item);
        return menu;
    }
}
