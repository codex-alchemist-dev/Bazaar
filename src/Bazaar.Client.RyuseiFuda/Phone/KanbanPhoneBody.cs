using Avalonia.Controls;
using Avalonia.Layout;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.RyuseiFuda.Phone;

/// <summary>
/// The Kanban body the phone pager shows (doc 29, work package 9). A head calls <see cref="Create"/> for
/// <see cref="PaneKinds.Kanban"/> in place of the static placeholder board, handing it the head's <see cref="IKanbanBoardClient"/>.
/// </summary>
public static class KanbanPhoneBody
{
    /// <summary>Shown when the head has no project to load. No request is sent in that case.</summary>
    public const string NoProjectText = "No project is selected, so there is no board to show.";

    /// <summary>
    /// Builds the live board body and starts loading it. Call on the UI thread: the pane continues on the caller's context.
    /// A failed load shows the pane's status line; it never throws out of here.
    /// </summary>
    /// <param name="client">The board service the head supplies.</param>
    /// <param name="projectId">The project to show, or null when none is selected.</param>
    public static Control Create(IKanbanBoardClient client, Guid? projectId)
    {
        ArgumentNullException.ThrowIfNull(client);

        if (projectId is not { } id)
        {
            return new TextBlock { Text = NoProjectText, TextWrapping = Avalonia.Media.TextWrapping.Wrap, Margin = new Avalonia.Thickness(8), HorizontalAlignment = HorizontalAlignment.Left };
        }

        var pane = new KanbanPhonePaneViewModel(client, id);
        var view = KanbanPhonePaneView.Create(pane);
        // LoadAsync reports failures through Status; only cancellation can escape, and that is the pane going away.
        _ = LoadQuietlyAsync(pane);
        return view;
    }

    private static async Task LoadQuietlyAsync(KanbanPhonePaneViewModel pane)
    {
        try
        {
            await pane.LoadAsync().ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
        }
    }
}
