using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Bazaar.Client.GitGui.Diff;
using Bazaar.Client.GitGui.ViewModels;
using Bazaar.Contracts.Git;

namespace Bazaar.Client.GitGui.Panes;

/// <summary>
/// The pane bodies. Each view rebuilds itself when its view model changes and starts the first load when it reaches the
/// screen. Colours come from the theme tokens, never a fixed value.
/// </summary>
internal static class GitGuiPaneViews
{
    internal static Control Prs(GitPrsPaneViewModel pane)
    {
        var list = pane.List;
        return Live(list, () => list.LoadAsync(), body =>
        {
            if (list.StaleBanner is { } banner)
            {
                body.Children.Add(Themed(new TextBlock { Text = banner, TextWrapping = TextWrapping.Wrap }, ThemeBrushes.TextAccent));
            }

            var filters = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            foreach (var state in PrListViewModel.States)
            {
                var button = new Button { Content = state, IsEnabled = !string.Equals(state, list.State, StringComparison.Ordinal) };
                AutomationProperties.SetName(button, $"Show {state} {list.PrNoun}s");
                var chosen = state;
                button.Click += (_, _) => _ = list.SetStateAsync(chosen);
                filters.Children.Add(button);
            }

            body.Children.Add(filters);
            Status(body, list.IsLoading, list.Error);

            if (list.Items.Count == 0 && !list.IsLoading && list.Error is null)
            {
                body.Children.Add(Themed(new TextBlock { Text = $"No {list.State} {list.PrNoun}s." }, ThemeBrushes.TextSecondary));
            }

            foreach (var pr in list.Items)
            {
                var draft = pr.Draft ? " · draft" : string.Empty;
                body.Children.Add(new TextBlock
                {
                    Text = $"#{pr.Number} {pr.Title} · {pr.AuthorLogin} · {pr.State}{draft} · {pr.HeadBranch} → {pr.BaseBranch}",
                    TextWrapping = TextWrapping.Wrap,
                });
            }

            var pager = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
            var previous = new Button { Content = "Previous", IsEnabled = list.HasPrevious };
            previous.Click += (_, _) => _ = list.PreviousPageAsync();
            var next = new Button { Content = "Next", IsEnabled = list.HasMore };
            next.Click += (_, _) => _ = list.NextPageAsync();
            pager.Children.Add(previous);
            pager.Children.Add(new TextBlock { Text = $"Page {list.Page}", VerticalAlignment = VerticalAlignment.Center });
            pager.Children.Add(next);
            body.Children.Add(pager);
        });
    }

    internal static Control Pr(GitPrPaneViewModel pane)
    {
        var detail = pane.Detail;
        return Live(detail, () => detail.LoadAsync(), body =>
        {
            Status(body, detail.IsLoading, detail.Error);
            if (detail.Detail is not { } d)
            {
                return;
            }

            var ui = detail.ProviderUi;
            if (detail.StaleNotice is { } stale)
            {
                body.Children.Add(Themed(new TextBlock { Text = stale, TextWrapping = TextWrapping.Wrap }, ThemeBrushes.TextAccent));
            }

            var s = d.Summary;
            body.Children.Add(new TextBlock { Text = $"{s.AuthorLogin} · {s.State}{(s.Draft ? " · draft" : string.Empty)} · {s.HeadBranch} → {s.BaseBranch}" });
            body.Children.Add(new TextBlock { Text = $"{d.ChangedFiles} files · +{d.Additions} -{d.Deletions}" });
            if (d.Body.Length > 0)
            {
                body.Children.Add(new TextBlock { Text = d.Body, TextWrapping = TextWrapping.Wrap });
            }

            body.Children.Add(Heading("Files"));
            foreach (var file in detail.Files)
            {
                var button = new Button { Content = $"{file.Path} ({file.Status} +{file.Additions} -{file.Deletions})", HorizontalAlignment = HorizontalAlignment.Stretch };
                var path = file.Path;
                button.Click += (_, _) => detail.SelectFile(path);
                body.Children.Add(button);
            }

            body.Children.Add(Heading("Checks"));
            if (detail.Checks.Count == 0)
            {
                body.Children.Add(Themed(new TextBlock { Text = "No checks reported." }, ThemeBrushes.TextSecondary));
            }

            foreach (var check in detail.Checks)
            {
                body.Children.Add(new TextBlock { Text = $"{check.Name}: {ui?.CheckLabel(check.State) ?? check.State}" });
            }

            body.Children.Add(Heading("Reviews"));
            if (detail.Reviews.Count == 0)
            {
                body.Children.Add(Themed(new TextBlock { Text = "No reviews yet." }, ThemeBrushes.TextSecondary));
            }

            foreach (var review in detail.Reviews)
            {
                body.Children.Add(new TextBlock { Text = $"{review.AuthorLogin}: {ui?.ReviewLabel(review.Verdict) ?? review.Verdict}" });
            }

            if (detail.Merge is { } card)
            {
                var merge = new Button { Content = "Request owner merge", IsEnabled = detail.CanMerge, HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(merge, $"Request owner merge of head {card.HeadSha}");
                merge.Click += (_, _) => _ = detail.MergeAsync();
                body.Children.Add(merge);
                body.Children.Add(Themed(
                    new TextBlock { Text = card.BlockedReason ?? $"Ready for the owner at head {card.HeadSha}.", TextWrapping = TextWrapping.Wrap },
                    ThemeBrushes.TextSecondary));
                if (detail.MergeMessage is { } mergeMessage)
                {
                    body.Children.Add(Themed(new TextBlock { Text = mergeMessage, TextWrapping = TextWrapping.Wrap }, ThemeBrushes.TextAccent));
                }
            }

            if (detail.SelectedDiff is { } diff)
            {
                body.Children.Add(DiffBlock(pane.Surfaces, diff));
            }
        });
    }

    internal static Control DiffPane(GitDiffPaneViewModel pane)
    {
        var detail = pane.Detail;
        return Live(detail, () => pane.LoadAsync(), body =>
        {
            Status(body, detail.IsLoading, detail.Error);
            if (detail.StaleNotice is { } stale)
            {
                body.Children.Add(Themed(new TextBlock { Text = stale, TextWrapping = TextWrapping.Wrap }, ThemeBrushes.TextAccent));
            }

            if (detail.SelectedDiff is { } diff)
            {
                body.Children.Add(DiffBlock(pane.Surfaces, diff));
            }
            else if (!detail.IsLoading && detail.Error is null)
            {
                body.Children.Add(Themed(new TextBlock { Text = "No file selected." }, ThemeBrushes.TextSecondary));
            }
        });
    }

    internal static Control View(GitViewPaneViewModel pane)
    {
        if (pane.View is not { } view)
        {
            return Themed(new TextBlock { Text = "No repository source is connected.", TextWrapping = TextWrapping.Wrap }, ThemeBrushes.TextSecondary);
        }

        return Live(view, () => view.LoadAsync(), body =>
        {
            if (view.Error is { } error)
            {
                body.Children.Add(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap });
            }

            body.Children.Add(Heading("Repositories"));
            foreach (var repo in view.Repos)
            {
                var button = new Button { Content = repo.FullName, IsEnabled = repo != view.SelectedRepo };
                var chosen = repo;
                button.Click += (_, _) => _ = view.SelectRepoAsync(chosen);
                body.Children.Add(button);
            }

            body.Children.Add(Heading("Branches"));
            foreach (var branch in view.Branches)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var select = new Button { Content = branch.Name, IsEnabled = branch.Name != view.SelectedBranch };
                var name = branch.Name;
                select.Click += (_, _) => _ = view.SelectBranchAsync(name);
                var constellation = new Button { Content = "Open in Constellation" };
                AutomationProperties.SetName(constellation, $"Open branch {branch.Name} in Constellation");
                constellation.Click += (_, _) => view.OpenInConstellation(name);
                row.Children.Add(select);
                row.Children.Add(constellation);
                body.Children.Add(row);
            }

            body.Children.Add(Heading("Recent commits"));
            foreach (var commit in view.Commits)
            {
                var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                var marker = commit.Sha == view.SelectedCommitSha ? "> " : string.Empty;
                row.Children.Add(new TextBlock
                {
                    Text = $"{marker}{commit.Sha[..Math.Min(7, commit.Sha.Length)]} {commit.Summary} · {commit.AuthorLogin}",
                    VerticalAlignment = VerticalAlignment.Center,
                });
                var constellation = new Button { Content = "Open in Constellation" };
                AutomationProperties.SetName(constellation, $"Open commit {commit.Sha} in Constellation");
                var sha = commit.Sha;
                constellation.Click += (_, _) => view.OpenInConstellation(sha);
                row.Children.Add(constellation);
                body.Children.Add(row);
            }
        });
    }

    private static Border DiffBlock(DiffSurfaceRegistry surfaces, DiffModel model) =>
        new Border { Height = 420, Child = surfaces.Resolve().Create(model) };

    /// <summary>
    /// Builds a host panel, fills it with <paramref name="render"/> now and after every change of <paramref name="source"/>, and
    /// runs <paramref name="load"/> once when the panel first reaches the screen.
    /// </summary>
    private static StackPanel Live(INotifyPropertyChanged source, Func<Task> load, Action<StackPanel> render)
    {
        var host = new StackPanel { Spacing = 6 };

        void Rebuild()
        {
            host.Children.Clear();
            render(host);
        }

        source.PropertyChanged += (_, _) => Rebuild();
        var started = false;
        host.AttachedToVisualTree += (_, _) =>
        {
            if (!started)
            {
                started = true;
                _ = load();
            }
        };
        Rebuild();
        return host;
    }

    private static void Status(StackPanel body, bool loading, string? error)
    {
        if (loading)
        {
            body.Children.Add(Themed(new TextBlock { Text = "Loading..." }, ThemeBrushes.TextSecondary));
        }

        if (error is not null)
        {
            body.Children.Add(Themed(new TextBlock { Text = error, TextWrapping = TextWrapping.Wrap }, ThemeBrushes.TextAccent));
        }
    }

    private static TextBlock Heading(string text) => new() { Text = text, FontWeight = FontWeight.SemiBold };

    private static TextBlock Themed(TextBlock block, string token)
    {
        if (ThemeBrushes.Find(token) is { } brush)
        {
            block.Foreground = brush;
        }

        return block;
    }
}
