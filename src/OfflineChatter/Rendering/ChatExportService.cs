using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using OfflineChatter.Core.Chat;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Export;
using OfflineChatter.Localization;
using WpfMath;
using WpfMath.Parsers;

namespace OfflineChatter.Rendering;

/// <summary>Экспорт и печать чата из UI: диалог сохранения с выбором формата, рендер формул в PNG для RTF/.doc.</summary>
public static class ChatExportService
{
    private static readonly (ExportFormat format, string filter)[] Formats =
    {
        (ExportFormat.Docx, "Word (*.docx)|*.docx"),
        (ExportFormat.Doc, "Word 97–2003 (*.doc)|*.doc"),
        (ExportFormat.Rtf, "RTF (*.rtf)|*.rtf"),
        (ExportFormat.Html, "HTML (*.html)|*.html"),
        (ExportFormat.Markdown, "Markdown (*.md)|*.md"),
        (ExportFormat.Json, "JSON (*.json)|*.json"),
    };

    public static ExportOptions Options() => new()
    {
        UserLabel = L.Get("Export_User"),
        AssistantLabel = L.Get("Export_Assistant"),
        ReasoningLabel = L.Get("Chat_Reasoning"),
        RenderMath = RenderMathPng,
    };

    /// <summary>Сохранить; формат выбирается в самом диалоге (тип файла). Возвращает путь или null.</summary>
    public static string? SaveWithDialog(ExportDocument doc, ChatSession? session)
    {
        var name = string.Join("_", (string.IsNullOrWhiteSpace(doc.Title) ? "chat" : doc.Title).Split(Path.GetInvalidFileNameChars()));
        if (name.Length > 60) name = name.Substring(0, 60);
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = L.Get("Export_Title"),
            FileName = name,
            Filter = string.Join("|", Formats.Select(f => f.filter)),
            FilterIndex = 1,
            AddExtension = true,
        };
        if (dlg.ShowDialog() != true) return null;
        var format = Formats[Math.Max(0, Math.Min(Formats.Length - 1, dlg.FilterIndex - 1))].format;
        try
        {
            Exporter.Export(doc, format, dlg.FileName, Options(), session);
            Log.Info("Export", "Exported", new { format = format.ToString(), messages = doc.Messages.Count });
            return dlg.FileName;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(ex.Message, "Offline Chatter", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    /// <summary>Формула → PNG (XAML-Math) для RTF/.doc; 2× масштаб для чёткости при печати.</summary>
    public static (byte[] png, int width, int height)? RenderMathPng(string latex, bool display)
    {
        try
        {
            var formula = WpfTeXFormulaParser.Instance.Parse(latex);
            var png = formula.RenderToPng(display ? 40 : 34, 0, 0, "Cambria Math");
            var size = ImageSize.Read(png);
            if (size == null) return null;
            // Логический размер — половина пикселей (картинка рендерится с двойной плотностью)
            return (png, Math.Max(1, size.Value.w / 2), Math.Max(1, size.Value.h / 2));
        }
        catch (Exception ex)
        {
            Log.Debug("Export", "Formula not rendered: " + ex.Message);
            return null;
        }
    }

    /// <summary>Печать (и «Печать в PDF/XPS» системными принтерами) через FlowDocument с нашим рендером.</summary>
    public static void Print(ExportDocument doc)
    {
        var dlg = new PrintDialog();
        if (dlg.ShowDialog() != true) return;

        var renderer = new MarkdownRenderer { FontSize = 12 };
        var fd = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize = 12,
            Foreground = Brushes.Black,
            Background = Brushes.White,
            PagePadding = new Thickness(56),
            ColumnWidth = double.PositiveInfinity,
            PageWidth = dlg.PrintableAreaWidth,
            PageHeight = dlg.PrintableAreaHeight,
        };
        // Для печати — светлые цвета независимо от темы приложения
        fd.Resources[Theming.ThemeKeys.TextPrimary] = Brushes.Black;
        fd.Resources[Theming.ThemeKeys.TextSecondary] = Brushes.DimGray;
        fd.Resources[Theming.ThemeKeys.TextTertiary] = Brushes.Gray;
        fd.Resources[Theming.ThemeKeys.CodeBackground] = new SolidColorBrush(Color.FromRgb(0xF3, 0xF3, 0xF3));
        fd.Resources[Theming.ThemeKeys.CardBorder] = Brushes.LightGray;

        fd.Blocks.Add(new Paragraph(new Run(doc.Title)) { FontSize = 20, FontWeight = FontWeights.SemiBold });
        foreach (var m in doc.Messages)
        {
            fd.Blocks.Add(new Paragraph(new Run(ExportDocument.Label(m, Options()).ToUpperInvariant()))
            {
                FontSize = 10, FontWeight = FontWeights.SemiBold, Foreground = m.Role == ChatRole.User ? Brushes.SteelBlue : Brushes.Gray,
                Margin = new Thickness(0, 14, 0, 4),
            });
            foreach (var b in renderer.Render(m.Content)) fd.Blocks.Add(b);
        }
        dlg.PrintDocument(((IDocumentPaginatorSource)fd).DocumentPaginator, doc.Title);
    }
}
