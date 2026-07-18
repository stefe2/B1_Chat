using System.Globalization;
using System.Windows.Data;
using Markdig;
using Markdig.Wpf;

namespace b1_chat_console.Converters;

public class MarkdownToFlowDocumentConverter : IValueConverter
{
    // Markdig.Wpf.Markdown.ToFlowDocument defaults to a bare pipeline with no extensions (no
    // tables, no task lists, ...) unless one is passed explicitly — MarkdownViewer's own default
    // pipeline (UseSupportedExtensions) isn't used by the static ToFlowDocument helper we call here.
    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().UseSupportedExtensions().Build();

    public object? Convert(object? value, Type targetType, object parameter, CultureInfo culture) =>
        value is string markdown ? Markdig.Wpf.Markdown.ToFlowDocument(markdown, Pipeline) : null;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => throw new NotSupportedException();
}
