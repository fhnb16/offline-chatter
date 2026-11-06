using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Extensions.Mathematics;
using Markdig.Extensions.Tables;
using Markdig.Extensions.TaskLists;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using OfflineChatter.Core.Diagnostics;
using OfflineChatter.Core.Text;
using OfflineChatter.Localization;
using OfflineChatter.Theming;
using MdBlock = Markdig.Syntax.Block;
using MdInline = Markdig.Syntax.Inlines.Inline;
using MdTable = Markdig.Extensions.Tables.Table;
using WBlock = System.Windows.Documents.Block;
using WInline = System.Windows.Documents.Inline;
using WTable = System.Windows.Documents.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;
using WRow = System.Windows.Documents.TableRow;
using WCell = System.Windows.Documents.TableCell;

namespace OfflineChatter.Rendering;

/// <summary>Markdown (Markdig) → блоки FlowDocument: заголовки, списки, цитаты, таблицы, код, формулы, ссылки.</summary>
public sealed class MarkdownRenderer
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseMathematics()
        .UsePipeTables()
        .UseGridTables()
        .UseEmphasisExtras()
        .UseAutoLinks()
        .UseTaskLists()
        .Build();

    public double FontSize { get; set; } = 14;

    public List<WBlock> Render(string markdown)
    {
        var result = new List<WBlock>();
        if (string.IsNullOrEmpty(markdown)) return result;
        MarkdownDocument doc;
        try
        {
            doc = Markdown.Parse(MarkdownText.NormalizeMath(markdown), Pipeline);
        }
        catch (Exception ex)
        {
            Log.Warn("Markdown", "Parse failed", ex);
            result.Add(new Paragraph(new Run(markdown)));
            return result;
        }
        foreach (var b in doc) AddBlock(b, result, 0);
        return result;
    }

    private void AddBlock(MdBlock block, ICollection<WBlock> target, int depth)
    {
        switch (block)
        {
            case HeadingBlock h:
            {
                var p = new Paragraph
                {
                    FontSize = FontSize * h.Level switch { 1 => 1.6, 2 => 1.35, 3 => 1.18, _ => 1.05 },
                    FontWeight = FontWeights.SemiBold,
                    Margin = new Thickness(0, h.Level <= 2 ? 14 : 10, 0, 4),
                };
                AddInlines(h.Inline, p.Inlines);
                target.Add(p);
                break;
            }
            case MathBlock mb:
            {
                var latex = mb.Lines.ToString().Trim();
                target.Add(new BlockUIContainer(MathView.Create(latex, true, FontSize)) { Margin = new Thickness(0, 4, 0, 4) });
                break;
            }
            case FencedCodeBlock fc:
                target.Add(new BlockUIContainer(CodeBlockView.Create(fc.Lines.ToString().TrimEnd('\n', '\r'), fc.Info)) { Margin = new Thickness(0, 6, 0, 6) });
                break;
            case CodeBlock cb:
                target.Add(new BlockUIContainer(CodeBlockView.Create(cb.Lines.ToString().TrimEnd('\n', '\r'), null)) { Margin = new Thickness(0, 6, 0, 6) });
                break;
            case ParagraphBlock pb:
            {
                var p = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
                AddInlines(pb.Inline, p.Inlines);
                target.Add(p);
                break;
            }
            case QuoteBlock q:
            {
                var section = new Section
                {
                    Padding = new Thickness(12, 2, 0, 2),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Margin = new Thickness(0, 4, 0, 8),
                };
                section.SetResourceReference(Section.BorderBrushProperty, ThemeKeys.QuoteBar);
                section.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.TextSecondary);
                foreach (var child in q) AddBlock(child, section.Blocks, depth + 1);
                target.Add(section);
                break;
            }
            case ListBlock lb:
            {
                var list = new List
                {
                    MarkerStyle = lb.IsOrdered ? TextMarkerStyle.Decimal : (depth % 2 == 0 ? TextMarkerStyle.Disc : TextMarkerStyle.Circle),
                    Margin = new Thickness(0, 0, 0, 8),
                    Padding = new Thickness(22, 0, 0, 0),
                };
                if (lb.IsOrdered && int.TryParse(lb.OrderedStart, out var start) && start > 1) list.StartIndex = start;
                foreach (var item in lb.OfType<ListItemBlock>())
                {
                    var li = new ListItem();
                    foreach (var child in item) AddBlock(child, li.Blocks, depth + 1);
                    foreach (var p in li.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0, 0, 0, 2);
                    list.ListItems.Add(li);
                }
                target.Add(list);
                break;
            }
            case MdTable t:
                target.Add(RenderTable(t));
                break;
            case ThematicBreakBlock:
            {
                var line = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) };
                line.SetResourceReference(Border.BackgroundProperty, ThemeKeys.Divider);
                target.Add(new BlockUIContainer(line));
                break;
            }
            case HtmlBlock html:
                target.Add(new Paragraph(new Run(html.Lines.ToString())) { Margin = new Thickness(0, 0, 0, 8) });
                break;
            case ContainerBlock container:
                foreach (var child in container) AddBlock(child, target, depth);
                break;
            case LeafBlock leaf when leaf.Inline != null:
            {
                var p = new Paragraph();
                AddInlines(leaf.Inline, p.Inlines);
                target.Add(p);
                break;
            }
        }
    }

    private WTable RenderTable(MdTable t)
    {
        var table = new WTable { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 10), BorderThickness = new Thickness(1) };
        table.SetResourceReference(WTable.BorderBrushProperty, ThemeKeys.CardBorder);
        var columns = t.ColumnDefinitions.Count > 0 ? t.ColumnDefinitions.Count : t.OfType<MdRow>().Select(r => r.Count).DefaultIfEmpty(1).Max();
        for (var i = 0; i < columns; i++) table.Columns.Add(new TableColumn());
        var group = new TableRowGroup();
        foreach (var row in t.OfType<MdRow>())
        {
            var tr = new WRow();
            if (row.IsHeader)
            {
                tr.FontWeight = FontWeights.SemiBold;
                tr.SetResourceReference(WRow.BackgroundProperty, ThemeKeys.CodeBackground);
            }
            var col = 0;
            foreach (var cell in row.OfType<MdCell>())
            {
                var tc = new WCell { Padding = new Thickness(8, 4, 8, 4), BorderThickness = new Thickness(0, 0, 1, 1) };
                tc.SetResourceReference(WCell.BorderBrushProperty, ThemeKeys.CardBorder);
                foreach (var child in cell) AddBlock(child, tc.Blocks, 1);
                foreach (var p in tc.Blocks.OfType<Paragraph>()) p.Margin = new Thickness(0);
                if (col < t.ColumnDefinitions.Count)
                {
                    var align = t.ColumnDefinitions[col].Alignment switch
                    {
                        TableColumnAlign.Center => TextAlignment.Center,
                        TableColumnAlign.Right => TextAlignment.Right,
                        _ => TextAlignment.Left,
                    };
                    foreach (var p in tc.Blocks.OfType<Paragraph>()) p.TextAlignment = align;
                }
                tr.Cells.Add(tc);
                col++;
            }
            group.Rows.Add(tr);
        }
        table.RowGroups.Add(group);
        return table;
    }

    private void AddInlines(ContainerInline? container, InlineCollection target)
    {
        if (container == null) return;
        foreach (var inline in container) AddInline(inline, target);
    }

    private void AddInline(MdInline inline, InlineCollection target)
    {
        switch (inline)
        {
            case LiteralInline lit:
                target.Add(new Run(lit.Content.ToString()));
                break;
            case MathInline mi:
                target.Add(new InlineUIContainer(MathView.Create(mi.Content.ToString().Trim(), false, FontSize)) { BaselineAlignment = BaselineAlignment.Center });
                break;
            case CodeInline code:
            {
                var run = new Run(code.Content) { FontSize = FontSize * 0.92 };
                run.SetResourceReference(TextElement.FontFamilyProperty, ThemeKeys.MonoFontFamily);
                run.SetResourceReference(TextElement.BackgroundProperty, ThemeKeys.CodeBackground);
                target.Add(run);
                break;
            }
            case EmphasisInline em:
            {
                Span span = em.DelimiterChar switch
                {
                    '~' when em.DelimiterCount == 2 => new Span { TextDecorations = TextDecorations.Strikethrough },
                    '=' => new Span(),
                    _ when em.DelimiterCount >= 2 => new Bold(),
                    _ => new Italic(),
                };
                AddInlines(em, span.Inlines);
                target.Add(span);
                break;
            }
            case LinkInline link when link.IsImage:
                target.Add(new Run("🖼 " + (link.FirstChild?.ToString() ?? link.Url)));
                break;
            case LinkInline link:
            {
                var h = CreateLink(link.Url);
                AddInlines(link, h.Inlines);
                if (h.Inlines.Count == 0) h.Inlines.Add(new Run(link.Url));
                target.Add(h);
                break;
            }
            case AutolinkInline auto:
            {
                var h = CreateLink(auto.IsEmail ? "mailto:" + auto.Url : auto.Url);
                h.Inlines.Add(new Run(auto.Url));
                target.Add(h);
                break;
            }
            case LineBreakInline lb:
                target.Add(lb.IsHard ? new LineBreak() : new Run(" "));
                break;
            case HtmlEntityInline entity:
                target.Add(new Run(entity.Transcoded.ToString()));
                break;
            case HtmlInline html:
                target.Add(new Run(html.Tag.Equals("<br>", StringComparison.OrdinalIgnoreCase) || html.Tag.StartsWith("<br", StringComparison.OrdinalIgnoreCase) ? "\n" : html.Tag));
                break;
            case TaskList task:
                target.Add(new Run(task.Checked ? "☑ " : "☐ "));
                break;
            case ContainerInline c:
                AddInlines(c, target);
                break;
        }
    }

    private static Hyperlink CreateLink(string? url)
    {
        var h = new Hyperlink();
        h.SetResourceReference(TextElement.ForegroundProperty, ThemeKeys.AccentText);
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" or "mailto")
        {
            h.NavigateUri = uri;
            h.ToolTip = uri.ToString();
            h.RequestNavigate += (_, e) =>
            {
                try { Process.Start(new ProcessStartInfo(e.Uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException) { }
                e.Handled = true;
            };
        }
        return h;
    }
}

/// <summary>Блок кода: заголовок с языком и кнопкой «Копировать», подсветка, горизонтальная прокрутка.</summary>
public static class CodeBlockView
{
    public static FrameworkElement Create(string code, string? language)
    {
        var border = new Border { CornerRadius = new CornerRadius(6), BorderThickness = new Thickness(1) };
        border.SetResourceReference(Border.BackgroundProperty, ThemeKeys.CodeBackground);
        border.SetResourceReference(Border.BorderBrushProperty, ThemeKeys.CardBorder);

        var dock = new DockPanel();
        var header = new DockPanel { Margin = new Thickness(10, 4, 4, 0) };
        var lang = new TextBlock { Text = string.IsNullOrWhiteSpace(language) ? "code" : language!.Trim(), FontSize = 11.5, VerticalAlignment = VerticalAlignment.Center };
        lang.SetResourceReference(TextBlock.ForegroundProperty, ThemeKeys.TextTertiary);
        var copy = new Button { Content = L.Get("Chat_Copy"), FontSize = 11.5, Padding = new Thickness(8, 2, 8, 2), MinHeight = 0 };
        copy.SetResourceReference(FrameworkElement.StyleProperty, "SubtleButtonStyle");
        copy.Click += (_, _) =>
        {
            try { Clipboard.SetText(code); copy.Content = L.Get("Chat_Copied"); }
            catch (System.Runtime.InteropServices.COMException) { }
        };
        DockPanel.SetDock(copy, Dock.Right);
        header.Children.Add(copy);
        header.Children.Add(lang);
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);

        var text = new TextBlock { FontSize = 12.5, Margin = new Thickness(10, 2, 10, 10), TextWrapping = TextWrapping.NoWrap };
        text.SetResourceReference(TextBlock.FontFamilyProperty, ThemeKeys.MonoFontFamily);
        foreach (var r in SyntaxHighlighter.Highlight(code, language)) text.Inlines.Add(r);
        var scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = text,
            Focusable = false,
        };
        scroll.PreviewMouseWheel += ScrollForwarding.ForwardToParent;
        dock.Children.Add(scroll);
        border.Child = dock;
        return border;
    }
}
