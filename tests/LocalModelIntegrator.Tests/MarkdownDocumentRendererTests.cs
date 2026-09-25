using System;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Threading;
using LocalModelIntegrator.ToolWindows;
using Xunit;

namespace LocalModelIntegrator.Tests
{
    public class MarkdownDocumentRendererTests
    {
        [Fact]
        public void RendersHeadingsEmphasisAndNestedLists() => OnSta(() =>
        {
            var document = MarkdownDocumentRenderer.Render("# Heading\n\n**bold** and *italic* with `code`\n\n3. first\n   - nested\n4. second");
            var blocks = document.Blocks.ToArray();
            Assert.Equal(FontWeights.Bold, Assert.IsType<Paragraph>(blocks[0]).FontWeight);
            var paragraph = Assert.IsType<Paragraph>(blocks[1]);
            Assert.Equal(FontWeights.Bold, Assert.IsType<Span>(paragraph.Inlines.FirstInline).FontWeight);
            Assert.DoesNotContain("**", Text(document));
            var list = Assert.IsType<List>(blocks[2]);
            Assert.Equal(3, list.StartIndex);
            Assert.Equal(2, list.ListItems.Count);
            Assert.IsType<List>(list.ListItems.FirstListItem.Blocks.LastBlock);
        });

        [Fact]
        public void PreservesCodeAndHandlesAnUnclosedStreamingFence() => OnSta(() =>
        {
            const string code = "var value = \"<tag> **literal**\";\n    return value;";
            foreach (string ending in new[] { "", "\n```" })
            {
                var document = MarkdownDocumentRenderer.Render("```csharp\n" + code + ending);
                var paragraph = Assert.IsType<Paragraph>(document.Blocks.FirstBlock);
                Assert.Equal("Consolas", paragraph.FontFamily.Source);
                Assert.Equal(code, new TextRange(paragraph.ContentStart, paragraph.ContentEnd).Text.TrimEnd('\r', '\n').Replace("\r\n", "\n"));
            }
        });

        [Fact]
        public void RendersTablesQuotesAndStrikethrough() => OnSta(() =>
        {
            var document = MarkdownDocumentRenderer.Render("| Name | Value |\n| --- | --- |\n| one | two |\n\n> quoted\n\n~~removed~~");
            var blocks = document.Blocks.ToArray();
            var table = Assert.IsType<Table>(blocks[0]);
            Assert.Equal(2, table.RowGroups[0].Rows.Count);
            Assert.Equal(FontWeights.Bold, table.RowGroups[0].Rows[0].FontWeight);
            Assert.IsType<Section>(blocks[1]);
            Assert.Equal(TextDecorations.Strikethrough,
                Assert.IsType<Span>(Assert.IsType<Paragraph>(blocks[2]).Inlines.FirstInline).TextDecorations);
        });

        [Fact]
        public void OnlyWebLinksAreNavigableAndImagesAndHtmlStayText() => OnSta(() =>
        {
            var document = MarkdownDocumentRenderer.Render(
                "[web](https://example.com) [file](file:///C:/secret.txt) [script](javascript:alert) ![image label](https://example.com/image.png) <b>literal</b>");
            var inlines = Assert.IsType<Paragraph>(document.Blocks.FirstBlock).Inlines.ToArray();
            var link = Assert.Single(inlines.OfType<Hyperlink>());
            Assert.Equal("https://example.com/", link.NavigateUri.AbsoluteUri);
            Assert.DoesNotContain(inlines, inline => inline is InlineUIContainer);
            Assert.Contains("image label", Text(document));
            Assert.Contains("<b>literal</b>", Text(document));
        });

        [Fact]
        public void PreservesEscapedTextAndEntities() => OnSta(() =>
        {
            var document = MarkdownDocumentRenderer.Render(@"A &amp; B &lt; C \*literal\*");
            Assert.Equal("A & B < C *literal*", Text(document).Trim());
        });

        [Fact]
        public void StreamingUpdatesRenderTheLatestSourceAndAcceptNull() => OnSta(() =>
        {
            var view = new MarkdownContent { Markdown = "**incomplete" };
            view.Markdown = "**complete**";
            view.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Assert.Equal("complete", Text(view.Document).Trim());
            Assert.Equal("**complete**", view.Markdown);
            view.Markdown = null;
            view.Dispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(() => { }));
            Assert.Equal(string.Empty, Text(view.Document).Trim());
        });

        private static string Text(FlowDocument document) =>
            new TextRange(document.ContentStart, document.ContentEnd).Text;

        private static void OnSta(Action action)
        {
            Exception error = null;
            var thread = new Thread(() =>
            {
                try { action(); }
                catch (Exception exception) { error = exception; }
                finally { Dispatcher.CurrentDispatcher.InvokeShutdown(); }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (error != null) ExceptionDispatchInfo.Capture(error).Throw();
        }
    }
}
