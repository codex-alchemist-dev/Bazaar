using System.Globalization;
using Bazaar.Client.Core.Panes;
using Bazaar.Contracts.Panes;

namespace Bazaar.Client.Interrupts.Panes;

/// <summary>
/// The interrupt stack pane (kind <see cref="InterruptPanes.StackKind"/>, doc 24 section 3). Read only: it lists the agents
/// with at least one live frame, top frame first, and never throws on a stack over the limit of 2 (it caps and notes it).
/// A malformed model raises <see cref="PaneModelException"/>, so the registry falls back to the generic view.
/// </summary>
public sealed class InterruptStackPaneViewModel : IPaneViewModel
{
    public InterruptStackPaneViewModel(PaneDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        Kind = descriptor.Kind;
        Title = "Interrupts";
        Agents = InterruptStackModelReader.Read(descriptor.Model)
            .Where(a => a.Depth >= 1)
            .Select(ToRow)
            .ToArray();
    }

    public string Kind { get; }

    public string Title { get; }

    /// <summary>Agents with depth 1 or more, in model order.</summary>
    public IReadOnlyList<InterruptAgentRow> Agents { get; }

    private static InterruptAgentRow ToRow(AgentInterruptStack stack)
    {
        var frames = stack.LiveFramesTopFirst
            .Take(AgentInterruptStack.MaxDepth)
            .Select((f, i) => new InterruptFrameRow(
                StackIndicatorViewModel.SourceLabel(f.Source),
                f.Reason,
                f.PushedAt.ToUniversalTime().ToString("yyyy-MM-dd HH:mm 'UTC'", CultureInfo.InvariantCulture),
                i == 0))
            .ToArray();
        var note = stack.IsOverLimit
            ? $"Stack over limit: {stack.LiveFrameCount} live frames, showing {AgentInterruptStack.MaxDepth}."
            : null;
        return new InterruptAgentRow(stack.Name, StackIndicatorViewModel.From(stack), frames, note);
    }
}

/// <summary>One agent in the pane. <see cref="OverLimitNote"/> is set only when the model carried too many live frames.</summary>
public sealed record InterruptAgentRow(string Name, StackIndicatorViewModel Indicator, IReadOnlyList<InterruptFrameRow> Frames, string? OverLimitNote);

/// <summary>One live frame: source as text, the reason (personal text, never logged), and the pushed time.</summary>
public sealed record InterruptFrameRow(string Source, string Reason, string PushedAt, bool IsTop);
