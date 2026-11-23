using Avalonia.Automation;
using Avalonia.Controls;
using Bazaar.Client.Chat.Abstractions;
using Bazaar.Client.Presence.Abstractions;
using Bazaar.Contracts.Wire;

namespace Bazaar.Client.Presence;

/// <summary>
/// The presence adornment for the GC presence chip (docs/design/23, "Client views"; #975 extension point). It shows the label the
/// server derived, which comes from the fixed set of DECISIONS-LOG C43, followed by the overlays, and nothing for an agent with no
/// row. It adds no label of its own. It asks the chip again when the receiver changes a row for that agent.
/// </summary>
public sealed class PresenceChipAdornment : IPresenceChipAdornment, IDisposable
{
    /// <summary>The adornment's id in the registry.</summary>
    public const string AdornmentId = "presence";

    private readonly IAgentPresenceView _view;

    /// <param name="view">The held presence rows.</param>
    public PresenceChipAdornment(IAgentPresenceView view)
    {
        _view = view ?? throw new ArgumentNullException(nameof(view));
        _view.Changed += OnViewChanged;
    }

    /// <inheritdoc />
    public string Id => AdornmentId;

    /// <inheritdoc />
    public int Order => -10;

    /// <inheritdoc />
    public event Action<Guid>? Changed;

    /// <inheritdoc />
    public Control? Create(Guid agentId)
    {
        if (_view.TryGetRow(agentId) is not { } row)
        {
            return null;
        }

        var text = Describe(row);
        var block = new TextBlock { Text = text, Tag = row.Presence };
        AutomationProperties.SetName(block, text);
        return block;
    }

    /// <summary>The chip text for a row: the label, then the overlays in parentheses, such as <c>thinking (throttled)</c>.</summary>
    public static string Describe(WirePresenceRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.Overlays.Count == 0
            ? row.Presence
            : $"{row.Presence} ({string.Join(", ", row.Overlays.Select(OverlayText))})";
    }

    /// <inheritdoc />
    public void Dispose() => _view.Changed -= OnViewChanged;

    private void OnViewChanged(Guid agentId) => Changed?.Invoke(agentId);

    private static string OverlayText(string overlay) => overlay switch
    {
        "OnBreak" => "on break",
        "WaitingForApproval" => "waiting for approval",
        "Throttled" => "throttled",
        "Covered" => "covered",
        "Ward" => "ward",
        "Idle" => "idle",
        _ => overlay,
    };
}
