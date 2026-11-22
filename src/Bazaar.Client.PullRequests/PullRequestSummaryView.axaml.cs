using System.ComponentModel;
using Avalonia.Controls;

namespace Bazaar.Client.PullRequests;

/// <summary>
/// One PR: summary, files, checks, reviews, the native diff and the merge confirmation. Every button calls the view model; the
/// merge runs only from the Confirm tap, and the source passed is always <see cref="OwnerActionSource.Button"/>.
/// </summary>
public sealed partial class PullRequestSummaryView : UserControl
{
    private readonly PullRequestDiffSurface _diff = new();

    public PullRequestSummaryView()
    {
        InitializeComponent();
        DiffHost.Child = _diff;
        DataContextChanged += (_, _) => Attach(DataContext as PullRequestSummaryViewModel);
    }

    private PullRequestSummaryViewModel? _vm;

    private void Attach(PullRequestSummaryViewModel? vm)
    {
        if (_vm is not null)
        {
            _vm.PropertyChanged -= OnVmChanged;
        }

        _vm = vm;
        if (vm is not null)
        {
            vm.PropertyChanged += OnVmChanged;
            _diff.Render(vm.DiffLines);
        }
    }

    private void OnVmChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_vm is { } vm)
        {
            _diff.Render(vm.DiffLines);
        }
    }

    private void OnMergeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.BeginMerge();

    private void OnCancelMergeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e) => _vm?.CancelMerge();

    private void OnConfirmMergeClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is { } vm)
        {
            _ = vm.ConfirmMergeAsync(OwnerActionSource.Button);
        }
    }

    private void OnRequestChangesClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is { } vm)
        {
            _ = vm.RequestChangesAsync(ReviewBody.Text ?? "");
        }
    }

    private void OnCommentClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is { } vm)
        {
            _ = vm.CommentAsync(ReviewBody.Text ?? "");
        }
    }

    private void OnDiffClick(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        if (_vm is { } vm)
        {
            _ = vm.OpenDiffAsync(DiffPathBox.Text ?? "");
        }
    }
}
