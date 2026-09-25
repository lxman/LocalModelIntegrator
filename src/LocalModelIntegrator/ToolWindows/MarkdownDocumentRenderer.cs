using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;
using MdTable = Markdig.Extensions.Tables.Table;
using MdRow = Markdig.Extensions.Tables.TableRow;
using MdCell = Markdig.Extensions.Tables.TableCell;

namespace LocalModelIntegrator.ToolWindows
{
    internal static class MarkdownDocumentRenderer
    {
        private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
            .UsePipeTables()
            .UseEmphasisExtras(Markdig.Extensions.EmphasisExtras.EmphasisExtraOptions.Strikethrough)
            .DisableHtml().Build();
        private static readonly FontFamily CodeFont = new FontFamily("Consolas");

        public static FlowDocument Render(string source)
        {
            var document = new FlowDocument { PagePadding = new Thickness(0) };
            AddBlocks(document.Blocks, Markdown.Parse(source ?? string.Empty, Pipeline));
            return document;
        }

        private static void AddBlocks(BlockCollection target, ContainerBlock source)
        {
            foreach (var block in source)
            {
                switch (block)
                {
                    case HeadingBlock heading:
                        var title = Paragraph(heading);
                        title.FontSize = 24 - (heading.Level - 1) * 2;
                        title.FontWeight = FontWeights.Bold;
                        target.Add(title);
                        break;
                    case CodeBlock code:
                        target.Add(new Paragraph(new Run(code.Lines.ToString()))
                        {
                            FontFamily = CodeFont,
                            Margin = new Thickness(0, 4, 0, 8),
                            Padding = new Thickness(8),
                            BorderThickness = new Thickness(1),
                            BorderBrush = Brushes.Gray
                        });
                        break;
                    case ListBlock list:
                        var renderedList = new System.Windows.Documents.List
                        {
                            MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc,
                            Margin = new Thickness(0, 0, 0, 8),
                            Padding = new Thickness(24, 0, 0, 0)
                        };
                        if (list.IsOrdered && int.TryParse(list.OrderedStart, out int start) && start > 0)
                            renderedList.StartIndex = start;
                        foreach (ListItemBlock item in list)
                        {
                            var renderedItem = new ListItem();
                            AddBlocks(renderedItem.Blocks, item);
                            renderedList.ListItems.Add(renderedItem);
                        }
                        target.Add(renderedList);
                        break;
                    case QuoteBlock quote:
                        var section = new Section
                        {
                            BorderThickness = new Thickness(3, 0, 0, 0),
                            BorderBrush = Brushes.Gray,
                            Padding = new Thickness(10, 0, 0, 0),
                            Margin = new Thickness(0, 4, 0, 8)
                        };
                        AddBlocks(section.Blocks, quote);
                        target.Add(section);
                        break;
                    case MdTable table:
                        target.Add(RenderTable(table));
                        break;
                    case ThematicBreakBlock _:
                        target.Add(new Paragraph
                        {
                            BorderBrush = Brushes.Gray,
                            BorderThickness = new Thickness(0, 1, 0, 0),
                            Margin = new Thickness(0, 8, 0, 8),
                            FontSize = 1
                        });
                        break;
                    case LeafBlock leaf:
                        target.Add(Paragraph(leaf));
                        break;
                    case ContainerBlock container:
                        AddBlocks(target, container);
                        break;
                }
            }
        }

        private static Paragraph Paragraph(LeafBlock source)
        {
            var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 8) };
            if (source.Inline != null) AddInlines(paragraph.Inlines, source.Inline);
            else paragraph.Inlines.Add(new Run(source.Lines.ToString()));
            return paragraph;
        }

        private static Table RenderTable(MdTable source)
        {
            var table = new Table { CellSpacing = 0, Margin = new Thickness(0, 4, 0, 8) };
            var rows = new TableRowGroup();
            table.RowGroups.Add(rows);
            foreach (MdRow row in source)
            {
                var renderedRow = new TableRow();
                if (row.IsHeader) renderedRow.FontWeight = FontWeights.Bold;
                foreach (MdCell cell in row)
                {
                    var renderedCell = new TableCell
                    {
                        Padding = new Thickness(6),
                        BorderBrush = Brushes.Gray,
                        BorderThickness = new Thickness(0, 0, 0, 1)
                    };
                    AddBlocks(renderedCell.Blocks, cell);
                    renderedRow.Cells.Add(renderedCell);
                }
                rows.Rows.Add(renderedRow);
            }
            return table;
        }

        private static void AddInlines(InlineCollection target, ContainerInline source)
        {
            foreach (var inline in source)
            {
                switch (inline)
                {
                    case LiteralInline literal:
                        target.Add(new Run(literal.Content.ToString()));
                        break;
                    case HtmlEntityInline entity:
                        target.Add(new Run(entity.Transcoded.ToString()));
                        break;
                    case CodeInline code:
                        target.Add(new Run(code.Content) { FontFamily = CodeFont });
                        break;
                    case LineBreakInline lineBreak:
                        target.Add(lineBreak.IsHard ? (System.Windows.Documents.Inline)new LineBreak() : new Run(" "));
                        break;
                    case EmphasisInline emphasis:
                        var span = new Span();
                        if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                        else if (emphasis.DelimiterCount == 2) span.FontWeight = FontWeights.Bold;
                        else span.FontStyle = FontStyles.Italic;
                        AddInlines(span.Inlines, emphasis);
                        target.Add(span);
                        break;
                    case LinkInline link:
                        // Keep images as their alt text; rendering a reply must not fetch remote content.
                        var label = CreateLink(link.IsImage ? null : link.Url);
                        AddInlines(label.Inlines, link);
                        target.Add(label);
                        break;
                    case AutolinkInline link:
                        var autoLink = CreateLink(link.Url);
                        autoLink.Inlines.Add(new Run(link.Url));
                        target.Add(autoLink);
                        break;
                    case ContainerInline container:
                        AddInlines(target, container);
                        break;
                }
            }
        }

        private static Span CreateLink(string url)
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
                return new Span();

            var link = new Hyperlink { NavigateUri = uri, ToolTip = uri.AbsoluteUri };
            // Inherit the current theme's foreground instead of WPF's hard-coded blue.
            System.Windows.Data.BindingOperations.SetBinding(link, TextElement.ForegroundProperty,
                new System.Windows.Data.Binding("Foreground")
                {
                    RelativeSource = new System.Windows.Data.RelativeSource(
                        System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.RichTextBox), 1)
                });
            link.RequestNavigate += (sender, args) =>
            {
                args.Handled = true;
                try { Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }); }
                catch (Win32Exception) { /* No browser registered. The URL remains available in the tooltip. */ }
                catch (InvalidOperationException) { }
            };
            return link;
        }
    }
}
