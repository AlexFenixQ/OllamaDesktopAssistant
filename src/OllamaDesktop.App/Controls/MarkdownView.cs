using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using Markdig;
using OllamaDesktop.App.Infrastructure;
using MD = Markdig.Syntax;
using MI = Markdig.Syntax.Inlines;
using MT = Markdig.Extensions.Tables;
namespace OllamaDesktop.App.Controls;
// Native WPF blocks only. Never execute HTML/XAML, fetch remote images or use an embedded browser.
public sealed class MarkdownView : RichTextBox
{
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().DisableHtml().Build();
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.Register(nameof(Markdown), typeof(string), typeof(MarkdownView), new PropertyMetadata("", Changed));
    public string Markdown { get => (string)GetValue(MarkdownProperty); set => SetValue(MarkdownProperty, value); }
    public MarkdownView()
    {
        IsReadOnly = true; IsDocumentEnabled = true; IsUndoEnabled = false; BorderThickness = new Thickness(0); Padding = new Thickness(0);
        Background = Brushes.Transparent; FontFamily = new FontFamily("Segoe UI"); FontSize = 15;
        SetResourceReference(ForegroundProperty, "TextBrush"); VerticalScrollBarVisibility = ScrollBarVisibility.Disabled;
        HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
    }
    private static void Changed(DependencyObject obj, DependencyPropertyChangedEventArgs args) => ((MarkdownView)obj).Render((string?)args.NewValue ?? "");
    private void Render(string source)
    {
        source = System.Text.RegularExpressions.Regex.Replace(source, @"<br\s*/?>", "\n", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        var document = new FlowDocument { PagePadding = new Thickness(0), FontFamily = FontFamily, FontSize = 15 };
        document.SetResourceReference(ForegroundProperty, "TextBrush");
        try
        {
            // Large answers remain entirely selectable, without a quadratic Markdown layout cost.
            if (source.Length > 60_000) document.Blocks.Add(new Paragraph(new Run(source)));
            else foreach (var block in Markdig.Markdown.Parse(source, Pipeline)) AddBlock(document.Blocks, block, source, 0);
        }
        catch { document.Blocks.Clear(); document.Blocks.Add(new Paragraph(new Run(source))); }
        Document = document;
    }
    private static void AddBlock(BlockCollection blocks, MD.Block block, string source, int depth)
    {
        if (depth > 16) { blocks.Add(new Paragraph(new Run(Raw(block, source)))); return; }
        switch (block)
        {
            case MD.HeadingBlock heading:
                var h = Paragraph(heading.Inline); h.FontSize = heading.Level switch { 1 => 25, 2 => 21, _ => 18 }; h.FontWeight = FontWeights.SemiBold; h.Margin = new Thickness(0, 13, 0, 7); blocks.Add(h); break;
            case MD.ParagraphBlock paragraph: blocks.Add(Paragraph(paragraph.Inline)); break;
            case MD.CodeBlock code:
                var text = code.Lines.ToString();
                var panel = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 7, 0, 7) };
                panel.SetResourceReference(Panel.BackgroundProperty, "CodeBrush");
                var copy = new Button { Content = "Копировать код", HorizontalAlignment = HorizontalAlignment.Right, FontSize = 11, Padding = new Thickness(8, 3, 8, 3) };
                copy.Click += (_, _) => { try { if (text.Length > 0) Clipboard.SetText(text); } catch { MessageBox.Show("Буфер обмена временно занят."); } };
                DockPanel.SetDock(copy, Dock.Top); panel.Children.Add(copy);
                var box = new TextBox { Text = text, IsReadOnly = true, IsUndoEnabled = false, AcceptsReturn = true, FontFamily = new FontFamily("Consolas"), FontSize = 13, TextWrapping = TextWrapping.NoWrap, BorderThickness = new Thickness(0), Padding = new Thickness(10), HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, MaxHeight = 500 };
                box.SetResourceReference(BackgroundProperty, "CodeBrush"); panel.Children.Add(box); blocks.Add(new BlockUIContainer(panel)); break;
            case MD.ListBlock list:
                var output = new System.Windows.Documents.List { MarkerStyle = list.IsOrdered ? TextMarkerStyle.Decimal : TextMarkerStyle.Disc, Margin = new Thickness(0, 5, 0, 8), Padding = new Thickness(24, 0, 0, 0) };
                foreach (var child in list)
                {
                    var item = new ListItem();
                    if (child is MD.ContainerBlock listContainer) foreach (var nested in listContainer) AddBlock(item.Blocks, nested, source, depth + 1);
                    if (item.Blocks.Count == 0) item.Blocks.Add(new Paragraph(new Run(Raw(child, source)))); output.ListItems.Add(item);
                }
                blocks.Add(output); break;
            case MD.QuoteBlock quote:
                var section = new Section { BorderThickness = new Thickness(3, 0, 0, 0), Padding = new Thickness(12, 3, 0, 3), Margin = new Thickness(0, 7, 0, 8) };
                section.SetResourceReference(Section.BorderBrushProperty, "AccentBrush");
                foreach (var child in quote) AddBlock(section.Blocks, child, source, depth + 1); blocks.Add(section); break;
            case MT.Table table:
                var t = new System.Windows.Documents.Table { CellSpacing = 0, Margin = new Thickness(0, 8, 0, 10) }; var group = new TableRowGroup(); t.RowGroups.Add(group);
                foreach (var rowBlock in table)
                {
                    if (rowBlock is not MT.TableRow row) continue;
                    var outputRow = new System.Windows.Documents.TableRow(); group.Rows.Add(outputRow);
                    foreach (var cellBlock in row)
                    {
                        var outputCell = new System.Windows.Documents.TableCell { BorderThickness = new Thickness(0.5), Padding = new Thickness(7) };
                        outputCell.SetResourceReference(System.Windows.Documents.TableCell.BorderBrushProperty, "BorderBrush");
                        if (row.IsHeader) outputCell.FontWeight = FontWeights.SemiBold;
                        if (cellBlock is MD.ContainerBlock cell) foreach (var nested in cell) AddBlock(outputCell.Blocks, nested, source, depth + 1);
                        if (outputCell.Blocks.Count == 0) outputCell.Blocks.Add(new Paragraph()); outputRow.Cells.Add(outputCell);
                    }
                }
                blocks.Add(t); break;
            case MD.ThematicBreakBlock:
                var border = new Border { Height = 1, Margin = new Thickness(0, 8, 0, 8) }; border.SetResourceReference(Border.BackgroundProperty, "BorderBrush"); blocks.Add(new BlockUIContainer(border)); break;
            case MD.ContainerBlock otherContainer:
                foreach (var child in otherContainer) AddBlock(blocks, child, source, depth + 1); break;
            default: blocks.Add(new Paragraph(new Run(Raw(block, source)))); break;
        }
    }
    private static Paragraph Paragraph(MI.ContainerInline? source)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 3, 0, 8), LineHeight = 22 }; if (source is not null) AddInlines(paragraph.Inlines, source, 0); return paragraph;
    }
    private static void AddInlines(InlineCollection target, MI.ContainerInline source, int depth)
    {
        if (depth > 20) { target.Add(new Run("[Слишком глубокая разметка]")); return; }
        for (var node = source.FirstChild; node is not null; node = node.NextSibling)
        {
            switch (node)
            {
                case MI.LiteralInline literal: target.Add(new Run(literal.Content.ToString())); break;
                case MI.LineBreakInline: target.Add(new LineBreak()); break;
                case MI.CodeInline code:
                    var run = new Run(code.Content) { FontFamily = new FontFamily("Consolas") }; run.SetResourceReference(TextElement.BackgroundProperty, "CodeBrush"); target.Add(run); break;
                case MI.EmphasisInline emphasis:
                    var span = new Span(); if (emphasis.DelimiterChar == '~') span.TextDecorations = TextDecorations.Strikethrough;
                    else if (emphasis.DelimiterCount >= 2) span.FontWeight = FontWeights.Bold; else span.FontStyle = FontStyles.Italic;
                    AddInlines(span.Inlines, emphasis, depth + 1); target.Add(span); break;
                case MI.LinkInline link:
                    if (link.IsImage) { target.Add(new Run("[Изображение: ")); AddInlines(target, link, depth + 1); target.Add(new Run("]")); }
                    else if (Uri.TryCreate(link.Url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0)
                    {
                        var hyperlink = new Hyperlink { NavigateUri = uri }; hyperlink.SetResourceReference(TextElement.ForegroundProperty, "AccentBrush"); AddInlines(hyperlink.Inlines, link, depth + 1);
                        hyperlink.RequestNavigate += (_, args) => { args.Handled = true; if (MessageBox.Show("Открыть ссылку в браузере?\n" + args.Uri.AbsoluteUri, "Внешняя ссылка", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes) try { SafeShell.Web(args.Uri.AbsoluteUri); } catch { MessageBox.Show("Браузер не удалось открыть."); } };
                        target.Add(hyperlink);
                    }
                    else AddInlines(target, link, depth + 1); break;
                case MI.AutolinkInline automatic: target.Add(new Run(automatic.Url)); break;
                case MI.ContainerInline container: AddInlines(target, container, depth + 1); break;
                default: target.Add(new Run(node.ToString() ?? "")); break;
            }
        }
    }
    private static string Raw(MD.Block block, string source)
    {
        int start = Math.Clamp(block.Span.Start, 0, source.Length), end = Math.Clamp(block.Span.End + 1, start, source.Length); return source[start..end];
    }
}
