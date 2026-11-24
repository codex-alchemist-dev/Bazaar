using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;

namespace Bazaar.Client.Archive.Panes;

/// <summary>The pane body of the Archive book. The pane module registers it with the pane registry.</summary>
internal static class ArchivePaneViews
{
    // Theme token (one theme file drives every head). Views bind to this, never to a fixed colour.
    private const string _secondaryTextToken = "Bazaar.brush.color.text.secondary";

    /// <summary>
    /// The book: chapter navigation on top, then the selected chapter's sentences. Each sentence carries its source as a
    /// link; a sentence without a usable source shows the explicit "missing source" link. Read-only.
    /// </summary>
    internal static StackPanel Book(ArchivePaneViewModel archive)
    {
        var body = new StackPanel { Spacing = 10 };

        // Rebuilds the body from the view model. Called once now and again after each navigation.
        void Render()
        {
            body.Children.Clear();
            if (archive.SelectedChapter is not { } chapter)
            {
                body.Children.Add(new TextBlock { Text = "No chapters yet.", Opacity = 0.7 });
                return;
            }

            body.Children.Add(Navigation(archive, Render));
            body.Children.Add(new TextBlock { Text = chapter.Title, FontWeight = FontWeight.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap });
            if (Predecessor(archive, chapter, Render) is { } predecessor)
            {
                body.Children.Add(predecessor);
            }

            if (chapter.Sentences.Count == 0)
            {
                body.Children.Add(new TextBlock { Text = "This chapter has no sentences.", Opacity = 0.7 });
            }

            foreach (var sentence in chapter.Sentences)
            {
                body.Children.Add(Sentence(sentence));
            }
        }

        Render();
        return body;
    }

    /// <summary>Previous and next buttons, a line of chapter position, and the buttons that select a chapter by id.</summary>
    private static StackPanel Navigation(ArchivePaneViewModel archive, Action render)
    {
        var previous = new Button
        {
            Content = "Previous",
            IsEnabled = archive.PreviousChapter is not null,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(previous, "Previous chapter");
        previous.Click += (_, _) =>
        {
            archive.SelectPrevious();
            render();
        };

        var next = new Button
        {
            Content = "Next",
            IsEnabled = archive.NextChapter is not null,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        AutomationProperties.SetName(next, "Next chapter");
        next.Click += (_, _) =>
        {
            archive.SelectNext();
            render();
        };

        var position = archive.Chapters.Count == 0
            ? string.Empty
            : $"Chapter {archive.SelectedIndex + 1} of {archive.Chapters.Count}";
        var contents = new WrapPanel { Orientation = Orientation.Horizontal };
        contents.Children.Add(previous);
        contents.Children.Add(next);

        var stack = new StackPanel { Spacing = 6, Children = { contents, new TextBlock { Text = position, Opacity = 0.7 } } };
        foreach (var chapter in archive.Chapters)
        {
            var id = chapter.Id;
            var select = new Button
            {
                Content = chapter.Title,
                IsEnabled = !ReferenceEquals(chapter, archive.SelectedChapter),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = id,
            };
            AutomationProperties.SetName(select, $"Open chapter {chapter.Title}");
            select.Click += (_, _) =>
            {
                archive.SelectChapter(id);
                render();
            };
            stack.Children.Add(select);
        }

        return stack;
    }

    /// <summary>The predecessor line of a regenerated chapter: a link to the predecessor when it is in this book, else its id.</summary>
    private static Control? Predecessor(ArchivePaneViewModel archive, ArchiveChapterRow chapter, Action render)
    {
        if (chapter.PredecessorId is not { } predecessorId)
        {
            return null;
        }

        if (archive.PredecessorChapter is null)
        {
            return new TextBlock { Text = $"Regenerates chapter {predecessorId} (not in this book).", TextWrapping = TextWrapping.Wrap, Opacity = 0.7 };
        }

        var link = new HyperlinkButton { Content = $"Regenerates {archive.PredecessorChapter.Title}", Tag = predecessorId };
        AutomationProperties.SetName(link, $"Open predecessor chapter {archive.PredecessorChapter.Title}");
        link.Click += (_, _) =>
        {
            archive.SelectChapter(predecessorId);
            render();
        };
        return link;
    }

    /// <summary>One sentence with its source link. A missing source is shown as an explicit link, never as a blank.</summary>
    private static StackPanel Sentence(ArchiveSentenceRow sentence)
    {
        var source = new HyperlinkButton
        {
            Content = sentence.SourceLabel,
            Tag = sentence.SourceRef,
            FontSize = 11,
        };
        AutomationProperties.SetName(source, sentence.IsMissingSource ? "Missing source" : $"Source {sentence.SourceRef}");
        if (sentence.IsMissingSource)
        {
            SetThemeBrush(source, TemplatedControl.ForegroundProperty, _secondaryTextToken);
        }

        return new StackPanel
        {
            Spacing = 2,
            Children =
            {
                new TextBlock { Text = sentence.Text, TextWrapping = TextWrapping.Wrap },
                source,
            },
        };
    }

    /// <summary>
    /// Sets a brush property from a theme token. A token the theme does not define falls back to transparent,
    /// so the view still renders.
    /// </summary>
    private static void SetThemeBrush(AvaloniaObject target, AvaloniaProperty property, string token)
    {
        var app = Application.Current;
        var brush = app is not null && app.TryGetResource(token, app.ActualThemeVariant, out var value) && value is IBrush found
            ? found
            : Brushes.Transparent;
        target.SetValue(property, brush);
    }
}
