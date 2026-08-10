using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using b1_chat_console.Converters;
using b1_chat_console.Models;
using b1_chat_console.Services;
using b1_chat_console.ViewModels;

namespace b1_chat_console;

public partial class HelpWindow : Window
{
    private readonly List<(HelpPage Page, Section Section)> _pageSections = new();
    private ScrollViewer? _contentScrollViewer;
    private bool _suppressScrollSync;

    public HelpWindow()
    {
        InitializeComponent();
        DarkTitleBar.Apply(this);

        var vm = new HelpViewModel();
        vm.PageNavigationRequested += NavigateToPage;
        DataContext = vm;

        BuildContinuousDocument(vm);
        ContentViewer.AddHandler(
            ScrollViewer.ScrollChangedEvent,
            new ScrollChangedEventHandler(ContentViewer_ScrollChanged));

        Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            _contentScrollViewer = FindVisualChild<ScrollViewer>(ContentViewer);
            UpdateCurrentPageFromScroll();
        }, DispatcherPriority.Loaded);
    }

    private void BuildContinuousDocument(HelpViewModel vm)
    {
        var pages = vm.Sections.SelectMany(section => section.Pages).ToList();
        if (pages.Count == 0)
        {
            ContentViewer.Document = MarkdownToFlowDocumentConverter.ToFlowDocument(
                "# Help unavailable\n\nThe Help manifest could not be loaded. Reinstall B1 Chat Console.");
            return;
        }

        var document = new FlowDocument();
        if (TryFindResource(Markdig.Wpf.Styles.DocumentStyleKey) is Style documentStyle)
            document.Style = documentStyle;

        var divider = TryFindResource("BevelBorderBrush") as Brush;
        foreach (var page in pages)
        {
            var renderedPage = MarkdownToFlowDocumentConverter.ToFlowDocument(vm.GetPageMarkdown(page));
            var section = new Section { Tag = page };

            if (_pageSections.Count > 0)
            {
                section.BorderBrush = divider;
                section.BorderThickness = new Thickness(0, 2, 0, 0);
                section.Padding = new Thickness(0, 34, 0, 0);
                section.Margin = new Thickness(0, 38, 0, 0);
            }

            while (renderedPage.Blocks.FirstBlock is Block block)
            {
                renderedPage.Blocks.Remove(block);
                section.Blocks.Add(block);
            }

            document.Blocks.Add(section);
            _pageSections.Add((page, section));
        }

        ContentViewer.Document = document;
    }

    private void NavigateToPage(HelpPage page)
    {
        var target = _pageSections.FirstOrDefault(entry => ReferenceEquals(entry.Page, page));
        if (target.Section == null) return;

        _suppressScrollSync = true;
        target.Section.BringIntoView();

        // BringIntoView guarantees that an off-screen section is formatted. The second pass
        // aligns its heading close to the top instead of merely exposing its last few pixels.
        Dispatcher.BeginInvoke(() =>
        {
            _contentScrollViewer ??= FindVisualChild<ScrollViewer>(ContentViewer);
            var rect = target.Section.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (_contentScrollViewer != null && !rect.IsEmpty)
            {
                var desiredTop = 18.0;
                _contentScrollViewer.ScrollToVerticalOffset(
                    Math.Max(0, _contentScrollViewer.VerticalOffset + rect.Top - desiredTop));
            }

            _suppressScrollSync = false;
            UpdateCurrentPageFromScroll();
        }, DispatcherPriority.Background);
    }

    private void ContentViewer_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _contentScrollViewer ??= e.OriginalSource as ScrollViewer;
        if (_suppressScrollSync || e.VerticalChange == 0) return;

        Dispatcher.BeginInvoke(UpdateCurrentPageFromScroll, DispatcherPriority.ContextIdle);
    }

    private void UpdateCurrentPageFromScroll()
    {
        if (_suppressScrollSync || _pageSections.Count == 0) return;

        // A page becomes active when its first heading crosses the upper fifth of the reader.
        // At the very top, fall back to the first page; at the bottom, the last crossed heading
        // remains active even when no later heading is visible yet.
        var threshold = Math.Max(80, ContentViewer.ActualHeight * 0.20);
        var active = _pageSections[0].Page;

        foreach (var entry in _pageSections)
        {
            var rect = entry.Section.ContentStart.GetCharacterRect(LogicalDirection.Forward);
            if (rect.IsEmpty) continue;
            if (rect.Top <= threshold)
                active = entry.Page;
            else
                break;
        }

        if (DataContext is HelpViewModel vm && !ReferenceEquals(vm.CurrentPage, active))
        {
            vm.CurrentPage = active;
            vm.CurrentTitle = active.Title;
        }
    }

    private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } nested) return nested;
        }

        return null;
    }

    private void Hyperlink_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (e.Parameter is not string url) return;
        var vm = (HelpViewModel)DataContext;
        if (vm.TryNavigateInternalLink(url)) return;

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo { FileName = url, UseShellExecute = true });
    }
}
